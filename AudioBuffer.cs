using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Audio;

internal class VoiceDataPacket
{
    public byte[] Data { get; set; }
    public int NumPackets { get; set; }
    public List<int> PacketOffsets { get; set; }
    
    public VoiceDataPacket(byte[] data, int numPackets, List<int> packetOffsets)
    {
        Data = data;
        NumPackets = numPackets;
        PacketOffsets = packetOffsets;
    }
}

internal class AudioBuffer
{
    private readonly ConcurrentDictionary<int, Queue<VoiceDataPacket>> _playerBuffers = new();
    private readonly Queue<VoiceDataPacket> _globalBuffer = new();
    private readonly ConcurrentDictionary<int, bool> _playerHearing = new();
    private readonly object _bufferLock = new();
    
    public void AddPlayerPacket(int slot, VoiceDataPacket packet)
    {
        lock (_bufferLock)
        {
            if (!_playerBuffers.ContainsKey(slot))
            {
                _playerBuffers[slot] = new Queue<VoiceDataPacket>();
            }
            _playerBuffers[slot].Enqueue(packet);
        }
    }
    
    public void AddGlobalPacket(VoiceDataPacket packet)
    {
        lock (_bufferLock)
        {
            _globalBuffer.Enqueue(packet);
        }
    }
    
    public VoiceDataPacket? GetPlayerPacket(int slot)
    {
        lock (_bufferLock)
        {
            if (_playerBuffers.TryGetValue(slot, out var queue) && queue.Count > 0)
            {
                return queue.Dequeue();
            }
        }
        return null;
    }
    
    public VoiceDataPacket? GetGlobalPacket()
    {
        lock (_bufferLock)
        {
            if (_globalBuffer.Count > 0)
            {
                return _globalBuffer.Dequeue();
            }
        }
        return null;
    }
    
    public bool HasPlayerPackets(int slot)
    {
        lock (_bufferLock)
        {
            return _playerBuffers.TryGetValue(slot, out var queue) && queue.Count > 0;
        }
    }
    
    public bool HasGlobalPackets()
    {
        lock (_bufferLock)
        {
            return _globalBuffer.Count > 0;
        }
    }
    
    public void ClearPlayer(int slot)
    {
        lock (_bufferLock)
        {
            if (_playerBuffers.TryRemove(slot, out var queue))
            {
                queue.Clear();
            }
        }
    }
    
    public void ClearGlobal()
    {
        lock (_bufferLock)
        {
            _globalBuffer.Clear();
        }
    }
    
    public void SetPlayerHearing(int slot, bool hearing)
    {
        _playerHearing[slot] = hearing;
    }
    
    public void SetAllPlayerHearing(bool hearing)
    {
        foreach (var key in _playerHearing.Keys.ToList())
        {
            _playerHearing[key] = hearing;
        }
    }
    
    public bool IsHearing(int slot)
    {
        return _playerHearing.GetValueOrDefault(slot, true);
    }
    
    public int GetPlayerPacketCount(int slot)
    {
        lock (_bufferLock)
        {
            return _playerBuffers.TryGetValue(slot, out var queue) ? queue.Count : 0;
        }
    }
    
    public int GetGlobalPacketCount()
    {
        lock (_bufferLock)
        {
            return _globalBuffer.Count;
        }
    }
}
