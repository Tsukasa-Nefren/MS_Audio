using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Concentus.Enums;
using Concentus.Structs;

namespace Audio;

internal class AudioProcessor
{
    private const int SampleRate = 48000;
    private const int FrameSize = 960;
    private const int Channels = 1;
    
    private readonly OpusEncoder _encoder;
    private readonly string _tempDir;
    private readonly string? _ffmpegPath;
    
    public AudioProcessor(string tempDir, string? ffmpegPath = null)
    {
        _tempDir = tempDir;
        _ffmpegPath = ffmpegPath;
        
        _encoder = new OpusEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO)
        {
            Bitrate = 64000,
            UseDTX = false,
            UseVBR = true
        };
    }
    
    public async Task<byte[]> ConvertAudioToPCM(string filePath, float volume, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Audio file not found: {filePath}");
            }
            
            var command = $"ffmpeg -y -i \"{filePath}\" -acodec pcm_s16le -ac {Channels} -ar {SampleRate} -filter:a \"volume={volume}\" -f s16le -";
            
            var ffmpegExe = "ffmpeg";
            if (!string.IsNullOrEmpty(_ffmpegPath) && File.Exists(_ffmpegPath))
            {
                ffmpegExe = _ffmpegPath;
            }
            else
            {
                var dllDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(dllDir))
                {
                    var ffmpegName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
                    var ffmpegInDllDir = Path.Combine(dllDir, ffmpegName);
                    if (File.Exists(ffmpegInDllDir))
                    {
                        ffmpegExe = ffmpegInDllDir;
                    }
                }
            }
            
            var processStartInfo = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                Arguments = command.Substring(command.IndexOf(' ') + 1),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            if (Path.IsPathRooted(ffmpegExe))
            {
                var ffmpegDir = Path.GetDirectoryName(ffmpegExe);
                if (!string.IsNullOrEmpty(ffmpegDir) && Directory.Exists(ffmpegDir))
                {
                    processStartInfo.WorkingDirectory = ffmpegDir;
                }
            }
            
            var outputBuffer = new List<byte>();
            
            using (var process = Process.Start(processStartInfo))
            {
                if (process == null)
                {
                    throw new Exception("Failed to start FFmpeg process");
                }
                
                var reader = process.StandardOutput.BaseStream;
                var buffer = new byte[4096];
                int bytesRead;
                
                while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        process.Kill();
                        throw new OperationCanceledException();
                    }
                    
                    outputBuffer.AddRange(buffer.Take(bytesRead));
                }
                
                process.WaitForExit();
                
                if (process.ExitCode != 0)
                {
                    var error = process.StandardError.ReadToEnd();
                    throw new Exception($"FFmpeg failed with exit code {process.ExitCode}: {error}");
                }
            }
            
            return outputBuffer.ToArray();
        }, cancellationToken);
    }
    
    public async Task<byte[]> ConvertAudioBufferToPCM(byte[] audioBuffer, float volume, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var tempFile = Path.Combine(_tempDir, $"audio_{Guid.NewGuid()}.tmp");
            
            try
            {
                File.WriteAllBytes(tempFile, audioBuffer);
                return ConvertAudioToPCM(tempFile, volume, cancellationToken).Result;
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try
                    {
                        File.Delete(tempFile);
                    }
                    catch
                    {
                    }
                }
            }
        }, cancellationToken);
    }
    
    public int EncodeOpusFrame(short[] samples, int sampleCount, byte[] outputBuffer)
    {
        if (sampleCount <= 0 || outputBuffer.Length < 2048)
        {
            return -1;
        }
        
        try
        {
            return _encoder.Encode(samples, 0, sampleCount, outputBuffer, 0, outputBuffer.Length);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AudioProcessor] Opus encoding error: {ex.Message}, sampleCount: {sampleCount}");
            return -1;
        }
    }
    
    public (byte[] combined, List<int> offsets) CombineOpusPackets(List<byte[]> packets)
    {
        var totalLength = packets.Sum(p => p.Length);
        var combined = new byte[totalLength];
        var offsets = new List<int>();
        var offset = 0;
        
        foreach (var packet in packets)
        {
            Array.Copy(packet, 0, combined, offset, packet.Length);
            offset += packet.Length;
            offsets.Add(offset);
        }
        
        return (combined, offsets);
    }

    public List<VoiceDataPacket> EncodeToOpus(byte[] pcmData, CancellationToken cancellationToken = default)
    {
        var packets = new List<VoiceDataPacket>();
        var opusBuffers = new List<byte[]>();
        
        var buffer = new List<byte>(pcmData);
        
        while (buffer.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            var frameSizeBytes = Math.Min(FrameSize, buffer.Count);
            
            if (frameSizeBytes <= 0)
            {
                break;
            }
            
            var extracted = buffer.GetRange(0, frameSizeBytes);
            buffer.RemoveRange(0, frameSizeBytes);
            
            if (frameSizeBytes < 2)
            {
                break;
            }
            
            var actualSamples = frameSizeBytes / 2;
            var samples = new short[actualSamples];
            
            for (int j = 0; j < actualSamples && j * 2 + 1 < extracted.Count; j++)
            {
                int byte1 = extracted[j * 2];
                int byte2 = extracted[j * 2 + 1];
                long data = (short)((byte2 << 8) | byte1);
                
                if (data < -32768) data = -32768;
                if (data > 32767) data = 32767;
                
                samples[j] = (short)(data & 0xFFFF);
            }
            
            try
            {
                var opusBuffer = new byte[2048];
                var encodedLength = _encoder.Encode(samples, 0, actualSamples, opusBuffer, 0, opusBuffer.Length);
                
                if (encodedLength > 0)
                {
                    var opusData = new byte[encodedLength];
                    Array.Copy(opusBuffer, opusData, encodedLength);
                    opusBuffers.Add(opusData);
                    
                    if (opusBuffers.Count == 3)
                    {
                        var (combinedData, packetOffsets) = CombineOpusPackets(opusBuffers);
                        packets.Add(new VoiceDataPacket(combinedData, 3, packetOffsets));
                        opusBuffers.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioProcessor] Opus encoding error: {ex.Message}, actualSamples: {actualSamples}, frameSizeBytes: {frameSizeBytes}");
            }
        }
        
        if (opusBuffers.Count > 0)
        {
            var (combinedData, packetOffsets) = CombineOpusPackets(opusBuffers);
            packets.Add(new VoiceDataPacket(combinedData, opusBuffers.Count, packetOffsets));
        }
        
        return packets;
    }
}
