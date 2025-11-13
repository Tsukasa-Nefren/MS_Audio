using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Sharp.Shared;
using Sharp.Shared.Managers;

namespace Audio;

public sealed class Audio : IModSharpModule, IAudio
{
    private readonly ISharedSystem _sharedSystem;
    private readonly IModSharp _modSharp;
    private readonly IEntityManager _entityManager;
    private AudioBuffer? _buffer;
    private AudioProcessor? _processor;
    private AudioManager? _manager;
    private string? _tempDir;
    private readonly string _dllPath;
    
    private readonly ISharpModuleManager _moduleManager;
    
    public Audio(ISharedSystem sharedSystem,
        string dllPath,
        string sharpPath,
        Version version,
        IConfiguration configuration,
        bool hotReload)
    {
        _sharedSystem = sharedSystem;
        _modSharp = sharedSystem.GetModSharp();
        _entityManager = sharedSystem.GetEntityManager();
        _moduleManager = sharedSystem.GetSharpModuleManager();
        _dllPath = dllPath;
    }
    
    public bool Init()
    {
        try
        {
            var gamePath = _modSharp.GetGamePath();
            _tempDir = Path.Combine(gamePath, "sharp", "temp", "audio");
            Directory.CreateDirectory(_tempDir);
            
            string? ffmpegPath = null;
            var ffmpegName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
            var ffmpegInDllDir = Path.Combine(_dllPath, ffmpegName);
                   if (File.Exists(ffmpegInDllDir))
                   {
                       ffmpegPath = ffmpegInDllDir;
                   }
            
            _buffer = new AudioBuffer();
            _processor = new AudioProcessor(_tempDir, ffmpegPath);
            _manager = new AudioManager(_modSharp, _entityManager, _buffer, _processor);
            
            SetAllPlayerHearing(true);
            
            _manager.Start();
            
            _modSharp.LogMessage("[Audio] Audio module initialized successfully");
            return true;
        }
        catch (Exception ex)
        {
            _modSharp.LogWarning($"[Audio] Failed to initialize: {ex.Message}");
            return false;
        }
    }
    
    public void Shutdown()
    {
        try
        {
            _manager?.Stop();
            _manager = null;
            _processor = null;
            _buffer = null;
            
            if (!string.IsNullOrEmpty(_tempDir) && Directory.Exists(_tempDir))
            {
                try
                {
                    var files = Directory.GetFiles(_tempDir);
                    foreach (var file in files)
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
            }
            
        }
        catch (Exception ex)
        {
            _modSharp.LogWarning($"[Audio] Error during shutdown: {ex.Message}");
        }
    }
    
    public void PostInit()
    {
        _moduleManager.RegisterSharpModuleInterface<IAudio>(this, "Audio", this);
    }
    
    public void OnLevelShutdown()
    {
        StopAllPlaying();
    }
    
    #region IAudio Implementation
    
    public void SetPlayerHearing(int slot, bool hearing)
    {
        _buffer?.SetPlayerHearing(slot, hearing);
    }
    
    public void SetAllPlayerHearing(bool hearing)
    {
        _buffer?.SetAllPlayerHearing(hearing);
    }
    
    public bool IsHearing(int slot)
    {
        return _buffer?.IsHearing(slot) ?? true;
    }
    
    public void PlayToPlayerFromFile(int slot, string audioFile, float volume = 1.0f)
    {
        if (_manager == null)
        {
            _modSharp.LogWarning("[Audio] Manager not initialized");
            return;
        }
        
        _ = _manager.PlayToPlayer(slot, null, audioFile, volume);
    }
    
    public void PlayToPlayerFromBuffer(int slot, byte[] audioBuffer, float volume = 1.0f)
    {
        if (_manager == null)
        {
            _modSharp.LogWarning("[Audio] Manager not initialized");
            return;
        }
        
        _ = _manager.PlayToPlayer(slot, audioBuffer, null, volume);
    }
    
    public void PlayFromFile(string audioFile, float volume = 1.0f)
    {
        if (_manager == null)
        {
            _modSharp.LogWarning("[Audio] Manager not initialized");
            return;
        }
        
        _ = _manager.PlayGlobal(null, audioFile, volume);
    }
    
    public void PlayFromBuffer(byte[] audioBuffer, float volume = 1.0f)
    {
        if (_manager == null)
        {
            _modSharp.LogWarning("[Audio] Manager not initialized");
            return;
        }
        
        _ = _manager.PlayGlobal(audioBuffer, null, volume);
    }
    
    public void StopAllPlaying()
    {
        _manager?.StopAllPlaying();
    }
    
    public void StopPlaying(int slot)
    {
        _manager?.StopPlaying(slot);
    }
    
    public bool IsPlaying(int slot)
    {
        return _manager?.IsPlaying(slot) ?? false;
    }
    
    public bool IsAllPlaying()
    {
        return _manager?.IsAllPlaying() ?? false;
    }
    
    public int RegisterPlayStartListener(Action<int> handler)
    {
        return _manager?.RegisterPlayStartListener(handler) ?? -1;
    }
    
    public void UnregisterPlayStartListener(int id)
    {
        _manager?.UnregisterPlayStartListener(id);
    }
    
    public int RegisterPlayEndListener(Action<int> handler)
    {
        return _manager?.RegisterPlayEndListener(handler) ?? -1;
    }
    
    public void UnregisterPlayEndListener(int id)
    {
        _manager?.UnregisterPlayEndListener(id);
    }
    
    public int RegisterPlayListener(Action<int> handler)
    {
        return _manager?.RegisterPlayListener(handler) ?? -1;
    }
    
    public void UnregisterPlayListener(int id)
    {
        _manager?.UnregisterPlayListener(id);
    }
    
    public void SetPlayer(int slot)
    {
        _manager?.SetPlayer(slot);
    }
    
    public double GetCurrentPlaybackTime(int slot)
    {
        return _manager?.GetCurrentPlaybackTime(slot) ?? -1;
    }
    
    public double GetCurrentPlaybackTime()
    {
        return _manager?.GetCurrentPlaybackTime() ?? -1;
    }
    
    #endregion
    
    public string DisplayName => "Audio";
    public string DisplayAuthor => "samyycX, Ported to ModSharp by Tsukasa";
}
