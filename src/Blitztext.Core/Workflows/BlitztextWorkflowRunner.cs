using Blitztext.Core.Abstractions;
using Blitztext.Core.Models;
using Blitztext.Core.Services;

namespace Blitztext.Core.Workflows;

public sealed class BlitztextWorkflowRunner(
    IAudioRecorder recorder,
    ITranscriptionBackend remoteTranscription,
    ITranscriptionBackend localTranscription,
    ITextRewriteClient rewriteClient)
{
    private SettingsContainer _settings = new();
    private WorkflowType? _activeType;
    private CancellationTokenSource? _processingCts;

    public WorkflowPhase Phase { get; private set; } = WorkflowPhase.Idle;
    public WorkflowType? ActiveType => _activeType;
    public bool IsRecording => recorder.IsRecording;
    public float AudioLevel => recorder.AudioLevel;

    public event EventHandler<WorkflowPhase>? PhaseChanged;
    public event EventHandler<string>? OutputProduced;

    public async Task StartAsync(WorkflowType type, SettingsContainer settings, CancellationToken cancellationToken = default)
    {
        await ResetAsync(cancellationToken);
        _settings = settings;
        _activeType = type;
        _processingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        SetPhase(WorkflowPhase.Running("Aufnahme läuft ..."));
        await recorder.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_activeType is null)
        {
            return;
        }

        if (!recorder.IsRecording)
        {
            _processingCts?.Cancel();
            SetPhase(WorkflowPhase.Idle);
            return;
        }

        var audio = await recorder.StopAsync(cancellationToken);
        await ProcessAsync(_activeType.Value, audio, _processingCts?.Token ?? cancellationToken);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        _processingCts?.Cancel();
        if (recorder.IsRecording)
        {
            try
            {
                _ = await recorder.StopAsync(cancellationToken);
            }
            catch
            {
                // Reset must be best-effort because it is also used while recovering from recorder failures.
            }
        }

        await recorder.DiscardAsync(cancellationToken);
        _activeType = null;
        SetPhase(WorkflowPhase.Idle);
    }

    private async Task ProcessAsync(WorkflowType type, RecordedAudio audio, CancellationToken cancellationToken)
    {
        try
        {
            if (TranscriptionQualityService.ShouldRejectRecording(audio.Duration, audio.PeakAmplitude))
            {
                await recorder.DiscardAsync(cancellationToken);
                SetPhase(WorkflowPhase.Error("Keine Aufnahme erkannt."));
                return;
            }

            SetPhase(WorkflowPhase.Running(IsLocal(type) ? "Wird lokal transkribiert ..." : "Wird transkribiert ..."));
            var customTerms = CustomTermsFor(audio);
            var rawText = await TranscribeAsync(type, audio, customTerms, cancellationToken);
            var cleanedRawText = TranscriptionQualityService.CleanedTranscript(rawText);

            if (TranscriptionQualityService.IsLikelyArtifact(cleanedRawText, audio.Duration, customTerms))
            {
                SetPhase(WorkflowPhase.Error("Keine Aufnahme erkannt."));
                return;
            }

            var finalText = type switch
            {
                WorkflowType.TextImprover => await RewriteAsync(
                    "Text wird verbessert ...",
                    PromptBuilder.BuildImproveRequest(cleanedRawText, _settings.TextImprovement),
                    cancellationToken),
                _ => cleanedRawText
            };

            var cleanedFinal = TranscriptionQualityService.CleanedTranscript(finalText);
            SetPhase(WorkflowPhase.Done(cleanedFinal));
            OutputProduced?.Invoke(this, cleanedFinal);
        }
        catch (OperationCanceledException)
        {
            SetPhase(WorkflowPhase.Idle);
        }
        catch (Exception ex)
        {
            SetPhase(WorkflowPhase.Error(ex.Message));
        }
        finally
        {
            TryDelete(audio.FilePath);
        }
    }

    // For very short clips the biasing prompt does more harm than good: the model is more likely
    // to echo the term list than to transcribe the brief utterance.
    private IReadOnlyList<string> CustomTermsFor(RecordedAudio audio) =>
        audio.Duration.TotalSeconds >= 0.9 ? _settings.TextImprovement.CustomTerms : [];

    private async Task<string> TranscribeAsync(WorkflowType type, RecordedAudio audio, IReadOnlyList<string> customTerms, CancellationToken cancellationToken)
    {
        var request = new TranscriptionRequest(
            audio.FilePath,
            customTerms,
            _settings.Transcription.Language,
            audio.Duration,
            _settings.App.SelectedLocalTranscriptionModelName);

        return IsLocal(type)
            ? await localTranscription.TranscribeAsync(request, cancellationToken)
            : await remoteTranscription.TranscribeAsync(request, cancellationToken);
    }

    private async Task<string> RewriteAsync(string phaseMessage, TextRewriteRequest request, CancellationToken cancellationToken)
    {
        SetPhase(WorkflowPhase.Running(phaseMessage));
        return await rewriteClient.RewriteAsync(request, cancellationToken);
    }

    private bool IsLocal(WorkflowType type)
    {
        return type == WorkflowType.LocalTranscription ||
               (type == WorkflowType.Transcription && _settings.App.SecureLocalModeEnabled);
    }

    private void SetPhase(WorkflowPhase phase)
    {
        Phase = phase;
        PhaseChanged?.Invoke(this, phase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temporary recordings are cleaned best-effort.
        }
    }
}
