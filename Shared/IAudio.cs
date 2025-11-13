using System;

namespace Audio;

public interface IAudio
{
    void SetPlayerHearing(int slot, bool hearing);
    
    void SetAllPlayerHearing(bool hearing);
    
    bool IsHearing(int slot);
    
    void PlayToPlayerFromFile(int slot, string audioFile, float volume = 1.0f);
    
    void PlayToPlayerFromBuffer(int slot, byte[] audioBuffer, float volume = 1.0f);
    
    void PlayFromFile(string audioFile, float volume = 1.0f);
    
    void PlayFromBuffer(byte[] audioBuffer, float volume = 1.0f);
    
    void StopAllPlaying();
    
    void StopPlaying(int slot);
    
    bool IsPlaying(int slot);
    
    bool IsAllPlaying();
    
    int RegisterPlayStartListener(Action<int> handler);
    
    void UnregisterPlayStartListener(int id);
    
    int RegisterPlayEndListener(Action<int> handler);
    
    void UnregisterPlayEndListener(int id);
    
    int RegisterPlayListener(Action<int> handler);
    
    void UnregisterPlayListener(int id);
    
    void SetPlayer(int slot);
    
    double GetCurrentPlaybackTime(int slot);
    
    double GetCurrentPlaybackTime();
}
