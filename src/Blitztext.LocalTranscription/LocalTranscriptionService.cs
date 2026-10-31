using Blitztext.Core.Abstractions;
using Blitztext.Core.Models;

namespace Blitztext.LocalTranscription;

/// <summary>
/// Single entry point for local transcription: routes the selected model to Parakeet (in-process,
/// sherpa-onnx) or to whisper.cpp (external runtime).
/// </summary>
public sealed class LocalTranscriptionService(
    ParakeetLocalTranscriptionService parakeet,
    WhisperCppLocalTranscriptionService whisper) : ITranscriptionBackend, ILocalModelService, IDisposable
{
    public IReadOnlyList<LocalModelInfo> GetModelOptions() =>
        [parakeet.GetModelInfo(), .. whisper.GetModelOptions()];

    public bool IsModelInstalled(string modelName) =>
        ParakeetLocalTranscriptionService.Handles(modelName) ? parakeet.IsInstalled : whisper.IsModelInstalled(modelName);

    public bool RequiresRuntime(string modelName) => !ParakeetLocalTranscriptionService.Handles(modelName);

    public LocalRuntimeInfo GetRuntimeInfo() => whisper.GetRuntimeInfo();

    public Task<LocalRuntimeInfo> InstallRuntimeAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        whisper.InstallRuntimeAsync(progress, cancellationToken);

    public async Task<LocalModelInfo> InstallAsync(string modelName, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!ParakeetLocalTranscriptionService.Handles(modelName))
        {
            return await whisper.InstallAsync(modelName, progress, cancellationToken);
        }

        var installed = await parakeet.InstallAsync(progress, cancellationToken);
        _ = PrewarmAsync(modelName);
        return installed;
    }

    public Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default) =>
        ParakeetLocalTranscriptionService.Handles(request.LocalModelName)
            ? parakeet.TranscribeAsync(request, cancellationToken)
            : whisper.TranscribeAsync(request, cancellationToken);

    /// <summary>Loads the selected in-process model in the background; a no-op for whisper.cpp models.</summary>
    public async Task PrewarmAsync(string? modelName)
    {
        if (!ParakeetLocalTranscriptionService.Handles(modelName))
        {
            return;
        }

        try
        {
            await parakeet.PrewarmAsync();
        }
        catch
        {
            // A failed prewarm surfaces again (with its message) on the first real transcription.
        }
    }

    public void Dispose() => parakeet.Dispose();
}
