using System.Diagnostics;
using System.IO;
using Blitztext.Core.Abstractions;
using NAudio.Wave;

namespace Blitztext.Platform.Windows;

public sealed class WindowsAudioRecorder : IAudioRecorder, IDisposable
{
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string? _currentPath;
    private readonly Stopwatch _stopwatch = new();
    private TaskCompletionSource? _recordingStopped;

    public bool IsRecording { get; private set; }

    public float AudioLevel { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsRecording)
        {
            return Task.CompletedTask;
        }

        Directory.CreateDirectory(WindowsPaths.CacheDirectory);
        _currentPath = Path.Combine(WindowsPaths.CacheDirectory, $"blitztext-{Guid.NewGuid():N}.wav");
        _recordingStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 50
        };
        _writer = new WaveFileWriter(_currentPath, _waveIn.WaveFormat);
        _waveIn.DataAvailable += HandleDataAvailable;
        _waveIn.RecordingStopped += HandleRecordingStopped;

        _stopwatch.Restart();
        _waveIn.StartRecording();
        IsRecording = true;
        return Task.CompletedTask;
    }

    public async Task<RecordedAudio> StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRecording || _waveIn is null || _currentPath is null)
        {
            throw new InvalidOperationException("Es läuft keine Aufnahme.");
        }

        _waveIn.StopRecording();
        if (_recordingStopped is not null)
        {
            await _recordingStopped.Task.WaitAsync(cancellationToken);
        }

        return new RecordedAudio(_currentPath, _stopwatch.Elapsed);
    }

    public Task DiscardAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_currentPath is not null && File.Exists(_currentPath))
        {
            File.Delete(_currentPath);
        }

        _currentPath = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _waveIn?.Dispose();
        _writer?.Dispose();
    }

    private void HandleDataAvailable(object? sender, WaveInEventArgs e)
    {
        _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        _writer?.Flush();
        AudioLevel = CalculatePeak(e.Buffer, e.BytesRecorded);
    }

    private void HandleRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _stopwatch.Stop();
        IsRecording = false;
        AudioLevel = 0;

        _waveIn?.Dispose();
        _writer?.Dispose();
        _waveIn = null;
        _writer = null;

        if (e.Exception is not null)
        {
            _recordingStopped?.TrySetException(e.Exception);
        }
        else
        {
            _recordingStopped?.TrySetResult();
        }
    }

    private static float CalculatePeak(byte[] buffer, int bytesRecorded)
    {
        var peak = 0;
        for (var index = 0; index + 1 < bytesRecorded; index += 2)
        {
            var sample = Math.Abs(BitConverter.ToInt16(buffer, index));
            if (sample > peak)
            {
                peak = sample;
            }
        }

        return Math.Clamp(peak / 32768f, 0f, 1f);
    }
}
