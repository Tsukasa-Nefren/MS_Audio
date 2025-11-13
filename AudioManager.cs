using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Audio;

internal class AudioManager
{
    private readonly IModSharp _modSharp;
    private readonly IEntityManager _entityManager;
    private readonly AudioBuffer _buffer;
    private readonly AudioProcessor _processor;
    private readonly List<Action<int>?> _playStartListeners = new();
    private readonly List<Action<int>?> _playEndListeners = new();
    private readonly List<Action<int>?> _playListeners = new();
    private readonly Dictionary<int, CancellationTokenSource> _processingTasks = new();
    private readonly Dictionary<int, List<byte[]>> _tempOpusBuffers = new();
    private readonly object _listenerLock = new();
    
    private uint _sectionNumber = 0;
    private int _audioPlayerSlot = -1;
    private Guid? _sendTimer;
    private bool _isPlaying = false;
    
    private readonly Dictionary<int, double> _playbackStartTimes = new();
    private readonly object _playbackTimeLock = new();
    
    public AudioManager(IModSharp modSharp, IEntityManager entityManager, AudioBuffer buffer, AudioProcessor processor)
    {
        _modSharp = modSharp;
        _entityManager = entityManager;
        _buffer = buffer;
        _processor = processor;
    }
    
    public void Start()
    {
        if (_sendTimer.HasValue)
        {
            return;
        }
        
        _sendTimer = _modSharp.PushTimer(SendVoiceData, 0.030, GameTimerFlags.Repeatable);
    }
    
    public void Stop()
    {
        if (_sendTimer.HasValue)
        {
            _modSharp.StopTimer(_sendTimer.Value);
            _sendTimer = null;
        }
        
        foreach (var cts in _processingTasks.Values)
        {
            cts.Cancel();
        }
        _processingTasks.Clear();
    }
    
    private void SendVoiceData()
    {
        var clients = GetConnectedClients();
        
        if (clients.Count == 0)
        {
            return;
        }
        
        bool played = false;
        
        if (!_buffer.HasGlobalPackets() && !HasPlayerPackets() && _processingTasks.Count == 0)
        {
            return;
        }
        
        var globalPacket = _buffer.GetGlobalPacket();
        CSVCMsg_VoiceData? globalMsg = null;
        
        if (globalPacket != null)
        {
            played = true;
            
            if (!_playbackStartTimes.ContainsKey(-1))
            {
                InitializePlaybackTime(-1);
            }
            
            globalMsg = CreateVoiceMessage(globalPacket, _audioPlayerSlot);
            
            int sentCount = 0;
            foreach (var client in clients)
            {
                var slot = client.PlayerSlot.AsPrimitive();
                if (_buffer.IsHearing((int)slot))
                {
                    var result = _modSharp.SendNetMessage<CSVCMsg_VoiceData>(new RecipientFilter(client.PlayerSlot), globalMsg);
                    sentCount++;
                    CallPlayListeners(-1);
                }
            }
            
            if (!_buffer.HasGlobalPackets() && !_processingTasks.ContainsKey(-1))
            {
                CallPlayEndListeners(-1);
            }
            else if (globalPacket != null)
            {
                CallPlayListeners(-1);
            }
        }
        
        foreach (var client in clients)
        {
            var slot = (int)client.PlayerSlot.AsPrimitive();
            
            if (!_buffer.IsHearing(slot))
            {
                continue;
            }
            
            var playerPacket = _buffer.GetPlayerPacket(slot);
            if (playerPacket != null)
            {
                played = true;
                
                if (!_playbackStartTimes.ContainsKey(slot))
                {
                    InitializePlaybackTime(slot);
                }
                
                var playerMsg = CreateVoiceMessage(playerPacket, _audioPlayerSlot);
                _modSharp.SendNetMessage<CSVCMsg_VoiceData>(new RecipientFilter(client.PlayerSlot), playerMsg);
                
                CallPlayListeners(slot);
                
                if (!_buffer.HasPlayerPackets(slot) && !_processingTasks.ContainsKey(slot))
                {
                    CallPlayEndListeners(slot);
                }
            }
        }
        
        if (played)
        {
            _sectionNumber++;
        }
        
        _isPlaying = _buffer.HasGlobalPackets() || _processingTasks.ContainsKey(-1) ||
                     HasPlayerPackets() || _processingTasks.Count > 0;
    }
    
    private CSVCMsg_VoiceData CreateVoiceMessage(VoiceDataPacket packet, int clientSlot)
    {
        var msg = new CSVCMsg_VoiceData
        {
            Client = clientSlot,
            Xuid = 0UL,
            Audio = new CMsgVoiceAudio
            {
                Format = VoiceDataFormat_t.VoicedataFormatOpus,
                SampleRate = 48000,
                VoiceData = ByteString.CopyFrom(packet.Data),
                SectionNumber = _sectionNumber,
                NumPackets = (uint)packet.NumPackets,
                SequenceBytes = 0,
                VoiceLevel = 0.0f
            }
        };
        
        foreach (var offset in packet.PacketOffsets)
        {
            msg.Audio.PacketOffsets.Add((uint)offset);
        }
        
        return msg;
    }
    
    public async Task PlayToPlayer(int slot, byte[]? audioBuffer, string? audioFile, float volume)
    {
        StopPlaying(slot);
        
        if (_processingTasks.TryGetValue(slot, out var existingCts))
        {
            existingCts.Cancel();
            _processingTasks.Remove(slot);
        }
        
        var cts = new CancellationTokenSource();
        _processingTasks[slot] = cts;
        _isPlaying = true;
        
        try
        {
            byte[]? pcmData = null;
            
            if (!string.IsNullOrEmpty(audioFile))
            {
                pcmData = await _processor.ConvertAudioToPCM(audioFile, volume, cts.Token);
            }
            else if (audioBuffer != null && audioBuffer.Length > 0)
            {
                pcmData = await _processor.ConvertAudioBufferToPCM(audioBuffer, volume, cts.Token);
            }
            
            if (pcmData == null)
            {
                return;
            }
            
            var packets = _processor.EncodeToOpus(pcmData, cts.Token);
            
            foreach (var packet in packets)
                {
                    if (cts.Token.IsCancellationRequested)
                    {
                        break;
                    }
                    _buffer.AddPlayerPacket(slot, packet);
                }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _modSharp.LogWarning($"[Audio] Error processing audio for player {slot}: {ex.Message}");
        }
        finally
        {
            if (_processingTasks.ContainsKey(slot))
            {
                _processingTasks.Remove(slot);
            }
            UpdatePlayingState();
        }
    }
    
    public async Task PlayGlobal(byte[]? audioBuffer, string? audioFile, float volume)
    {
        StopAllPlaying();
        
        if (_processingTasks.TryGetValue(-1, out var existingCts))
        {
            existingCts.Cancel();
            _processingTasks.Remove(-1);
        }
        
        var cts = new CancellationTokenSource();
        _processingTasks[-1] = cts;
        _isPlaying = true;
        
        _ = Task.Run(async () =>
        {
            try
            {
                byte[]? pcmData = null;
                
                if (!string.IsNullOrEmpty(audioFile))
                {
                    pcmData = await _processor.ConvertAudioToPCM(audioFile, volume, cts.Token);
                }
                else if (audioBuffer != null && audioBuffer.Length > 0)
                {
                    pcmData = await _processor.ConvertAudioBufferToPCM(audioBuffer, volume, cts.Token);
                }
                
                if (pcmData == null || cts.Token.IsCancellationRequested)
                {
                    return;
                }
                
                CallPlayStartListeners(-1);
                
                var buffer = new List<byte>(pcmData);
                int packetCount = 0;
                
                while (buffer.Count > 0 && !cts.Token.IsCancellationRequested)
                {
                    if (!_processingTasks.TryGetValue(-1, out var currentCts) || currentCts != cts)
                    {
                        break;
                    }
                    
                    const int FrameSize = 960;
                    var frameSizeBytes = Math.Min(FrameSize, buffer.Count);
                    
                    if (frameSizeBytes < 2)
                    {
                        break;
                    }
                    
                    var extracted = buffer.GetRange(0, frameSizeBytes);
                    buffer.RemoveRange(0, frameSizeBytes);
                    
                    var actualSamples = frameSizeBytes / 2;
                    var samples = new short[actualSamples];
                    
                    for (int i = 0; i < actualSamples && i * 2 + 1 < extracted.Count; i++)
                    {
                        int byte1 = extracted[i * 2];
                        int byte2 = extracted[i * 2 + 1];
                        long data = (short)((byte2 << 8) | byte1);
                        if (data < -32768) data = -32768;
                        if (data > 32767) data = 32767;
                        samples[i] = (short)(data & 0xFFFF);
                    }
                    
                    try
                    {
                        var opusBuffer = new byte[2048];
                        var encodedLength = _processor.EncodeOpusFrame(samples, actualSamples, opusBuffer);
                        
                        if (encodedLength > 0)
                        {
                            var opusData = new byte[encodedLength];
                            Array.Copy(opusBuffer, opusData, encodedLength);
                            
                            lock (_tempOpusBuffers)
                            {
                                if (!_tempOpusBuffers.ContainsKey(-1))
                                {
                                    _tempOpusBuffers[-1] = new List<byte[]>();
                                }
                                _tempOpusBuffers[-1].Add(opusData);
                                
                                if (_tempOpusBuffers[-1].Count == 3)
                                {
                                    var (combinedData, packetOffsets) = _processor.CombineOpusPackets(_tempOpusBuffers[-1]);
                                    _buffer.AddGlobalPacket(new VoiceDataPacket(combinedData, 3, packetOffsets));
                                    packetCount++;
                                    _tempOpusBuffers[-1].Clear();
                                 }
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
                
                lock (_tempOpusBuffers)
                {
                    if (_tempOpusBuffers.TryGetValue(-1, out var remainingBuffers) && remainingBuffers.Count > 0 && !cts.Token.IsCancellationRequested)
                    {
                        var (combinedData, packetOffsets) = _processor.CombineOpusPackets(remainingBuffers);
                        _buffer.AddGlobalPacket(new VoiceDataPacket(combinedData, remainingBuffers.Count, packetOffsets));
                        packetCount++;
                        _tempOpusBuffers.Remove(-1);
                    }
                    else if (_tempOpusBuffers.ContainsKey(-1))
                    {
                        _tempOpusBuffers.Remove(-1);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _modSharp.LogWarning($"[Audio] Error processing global audio: {ex.Message}");
            }
            finally
            {
                if (_processingTasks.TryGetValue(-1, out var finalCts) && finalCts == cts)
                {
                    _processingTasks.Remove(-1);
                }
            }
        }, cts.Token);
    }
    
    public void StopPlaying(int slot)
    {
        if (_processingTasks.TryGetValue(slot, out var cts))
        {
            cts.Cancel();
            _processingTasks.Remove(slot);
        }
        
        _buffer.ClearPlayer(slot);
        
        ClearPlaybackTime(slot);
        
        CallPlayEndListeners(slot);
        UpdatePlayingState();
    }
    
    public void StopAllPlaying()
    {
        bool wasPlaying = _buffer.HasGlobalPackets() || _processingTasks.ContainsKey(-1);
        
        if (_processingTasks.TryGetValue(-1, out var cts))
        {
            cts.Cancel();
            _processingTasks.Remove(-1);
        }
        
        _buffer.ClearGlobal();
        
        ClearPlaybackTime(-1);
        
        if (wasPlaying)
        {
            CallPlayEndListeners(-1);
        }
        
        UpdatePlayingState();
    }
    
    private void UpdatePlayingState()
    {
        _isPlaying = _buffer.HasGlobalPackets() || _processingTasks.ContainsKey(-1);
        
        if (!_isPlaying)
        {
            var maxClients = _modSharp.GetGlobals().MaxClients;
            for (int i = 0; i <= maxClients; i++)
            {
                if (_buffer.HasPlayerPackets(i) || _processingTasks.ContainsKey(i))
                {
                    _isPlaying = true;
                    break;
                }
            }
        }
    }
    
    private bool HasPlayerPackets()
    {
        var maxClients = _modSharp.GetGlobals().MaxClients;
        for (int i = 0; i <= maxClients; i++)
        {
            if (_buffer.HasPlayerPackets(i))
            {
                return true;
            }
        }
        return false;
    }
    
    public bool IsPlaying(int slot)
    {
        return _buffer.HasPlayerPackets(slot) || _processingTasks.ContainsKey(slot);
    }
    
    public bool IsAllPlaying()
    {
        return _buffer.HasGlobalPackets() || _processingTasks.ContainsKey(-1);
    }
    
    public void SetPlayer(int slot)
    {
        _audioPlayerSlot = slot;
    }
    
    public int RegisterPlayStartListener(Action<int> handler)
    {
        lock (_listenerLock)
        {
            _playStartListeners.Add(handler);
            return _playStartListeners.Count - 1;
        }
    }
    
    public void UnregisterPlayStartListener(int id)
    {
        lock (_listenerLock)
        {
            if (id >= 0 && id < _playStartListeners.Count)
            {
                _playStartListeners[id] = null!;
            }
        }
    }
    
    public int RegisterPlayEndListener(Action<int> handler)
    {
        lock (_listenerLock)
        {
            _playEndListeners.Add(handler);
            return _playEndListeners.Count - 1;
        }
    }
    
    public void UnregisterPlayEndListener(int id)
    {
        lock (_listenerLock)
        {
            if (id >= 0 && id < _playEndListeners.Count)
            {
                _playEndListeners[id] = null!;
            }
        }
    }
    
    public int RegisterPlayListener(Action<int> handler)
    {
        lock (_listenerLock)
        {
            _playListeners.Add(handler);
            return _playListeners.Count - 1;
        }
    }
    
    public void UnregisterPlayListener(int id)
    {
        lock (_listenerLock)
        {
            if (id >= 0 && id < _playListeners.Count)
            {
                _playListeners[id] = null!;
            }
        }
    }
    
    private void CallPlayStartListeners(int slot)
    {
        lock (_listenerLock)
        {
            foreach (var listener in _playStartListeners)
            {
                listener?.Invoke(slot);
            }
        }
    }
    
    private void CallPlayEndListeners(int slot)
    {
        lock (_listenerLock)
        {
            foreach (var listener in _playEndListeners)
            {
                listener?.Invoke(slot);
            }
        }
    }
    
    private void CallPlayListeners(int slot)
    {
        lock (_listenerLock)
        {
            foreach (var listener in _playListeners)
            {
                listener?.Invoke(slot);
            }
        }
    }
    
    private List<IPlayerController> GetConnectedClients()
    {
        var clients = new List<IPlayerController>();
        var maxClients = _modSharp.GetGlobals().MaxClients;
        
        for (int i = 0; i <= maxClients; i++)
        {
            var slot = new PlayerSlot((byte)i);
            var controller = _entityManager.FindPlayerControllerBySlot(slot);
            
            if (controller != null && 
                controller.ConnectedState == PlayerConnectedState.PlayerConnected &&
                controller.IsFakeClient == false)
            {
                clients.Add(controller);
            }
        }
        
        return clients;
    }
    
    private void InitializePlaybackTime(int slot)
    {
        lock (_playbackTimeLock)
        {
            _playbackStartTimes[slot] = _modSharp.GetGlobals().CurTime;
        }
    }
    
    private void UpdatePlaybackTime(int slot)
    {
    }
    
    private void ClearPlaybackTime(int slot)
    {
        lock (_playbackTimeLock)
        {
            _playbackStartTimes.Remove(slot);
        }
    }
    
    public double GetCurrentPlaybackTime(int slot)
    {
        lock (_playbackTimeLock)
        {
            if (!_playbackStartTimes.TryGetValue(slot, out var startTime))
            {
                return -1;
            }
            
            var currentTime = _modSharp.GetGlobals().CurTime;
            return currentTime - startTime;
        }
    }
    
    public double GetCurrentPlaybackTime()
    {
        return GetCurrentPlaybackTime(-1);
    }
}