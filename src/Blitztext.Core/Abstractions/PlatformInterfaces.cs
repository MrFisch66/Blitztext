using Blitztext.Core.Models;

namespace Blitztext.Core.Abstractions;

public enum SecretKey
{
    OpenAIApiKey
}

/// <param name="PeakAmplitude">
/// Loudest sample observed during the recording, normalized to 0..1. Used to distinguish a real
/// utterance from a near-silent accidental hotkey tap.
/// </param>
public sealed record RecordedAudio(string FilePath, TimeSpan Duration, float PeakAmplitude);

public interface IAudioRecorder
{
    bool IsRecording { get; }
    float AudioLevel { get; }

    /// <summary>
    /// Raw captured audio (16 kHz, mono, PCM16 little-endian) as it arrives, for live transcription.
    /// The memory is only valid during the callback; consumers must copy what they keep.
    /// </summary>
    event EventHandler<ReadOnlyMemory<byte>>? AudioAvailable;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task<RecordedAudio> StopAsync(CancellationToken cancellationToken = default);
    Task DiscardAsync(CancellationToken cancellationToken = default);
}

public interface ISecretStore
{
    Task SaveAsync(SecretKey key, string value, CancellationToken cancellationToken = default);
    Task<string?> LoadAsync(SecretKey key, CancellationToken cancellationToken = default);
    Task DeleteAsync(SecretKey key, CancellationToken cancellationToken = default);
}

public interface ISettingsStore
{
    Task<SettingsContainer> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(SettingsContainer settings, CancellationToken cancellationToken = default);
}

public sealed record TranscriptionRequest(
    string AudioPath,
    IReadOnlyList<string> CustomTerms,
    string Language,
    TimeSpan Duration,
    string? LocalModelName = null);

public interface ITranscriptionBackend
{
    Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default);
}

public sealed record StreamingTranscriptionOptions(IReadOnlyList<string> CustomTerms, string Language);

/// <summary>
/// A transcription that runs while the user is still speaking, so only the tail of the audio is
/// left to process when the recording stops.
/// </summary>
public interface IStreamingTranscriptionSession : IAsyncDisposable
{
    /// <summary>Feeds captured audio (16 kHz, mono, PCM16). Never blocks the capture thread.</summary>
    void AppendAudio(ReadOnlySpan<byte> pcm16);

    /// <summary>Marks the end of the recording and returns the final transcript.</summary>
    Task<string> CompleteAsync(CancellationToken cancellationToken = default);
}

public interface IStreamingTranscriptionBackend
{
    /// <summary>Starts a session immediately; it connects in the background and buffers audio until ready.</summary>
    IStreamingTranscriptionSession StartSession(StreamingTranscriptionOptions options);
}

public enum RewriteOperation
{
    Improve
}

public sealed record TextRewriteRequest(
    RewriteOperation Operation,
    string Text,
    string Instructions,
    string Model,
    double Temperature);

public interface ITextRewriteClient
{
    Task<string> RewriteAsync(TextRewriteRequest request, CancellationToken cancellationToken = default);
}

public sealed record LocalModelInfo(string Id, string DisplayName, bool IsInstalled, string Path);

public sealed record LocalRuntimeInfo(bool IsInstalled, string ExecutablePath);

public interface ILocalModelService
{
    IReadOnlyList<LocalModelInfo> GetModelOptions();
    bool IsModelInstalled(string modelName);

    /// <summary>Whether the model runs through the whisper.cpp runtime (and therefore needs it installed).</summary>
    bool RequiresRuntime(string modelName);
    LocalRuntimeInfo GetRuntimeInfo();
    Task<LocalRuntimeInfo> InstallRuntimeAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
    Task<LocalModelInfo> InstallAsync(
        string modelName,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IPasteService
{
    IntPtr CaptureCurrentTarget();
    Task CopyAsync(string text, CancellationToken cancellationToken = default);
    Task PasteAsync(string text, IntPtr targetWindow, CancellationToken cancellationToken = default);
}

public enum HotkeyEventKind
{
    Down,
    Up,
    Cancel
}

public sealed record HotkeyEvent(HotkeyEventKind Kind, WorkflowType? WorkflowType = null);

public interface IHotkeyService : IDisposable
{
    HotkeyMode Mode { get; set; }

    /// <summary>When true, hotkeys are observed but not acted on (e.g. while the user is rebinding them).</summary>
    bool Paused { get; set; }

    event EventHandler<HotkeyEvent>? Hotkey;
    void Configure(IReadOnlyDictionary<WorkflowType, HotkeyBinding> bindings);
    void Start();
    void Stop();
}

public interface IStartupService
{
    bool IsEnabled();
    void SetEnabled(bool enabled);
}
