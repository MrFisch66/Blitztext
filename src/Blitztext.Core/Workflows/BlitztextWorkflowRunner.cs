using Blitztext.Core.Abstractions;
using Blitztext.Core.Models;
using Blitztext.Core.Services;

namespace Blitztext.Core.Workflows;

public sealed class BlitztextWorkflowRunner(
    IAudioRecorder recorder,
    ITranscriptionBackend remoteTranscription,
    ITranscriptionBackend localTranscription,
    ITextRewriteClient rewriteClient,
    IStreamingTranscriptionBackend? streamingTranscription = null)
{
    // Once the key is released only the audio tail is left for the live model. If no final
    // transcript arrives within this window, the recorded file is uploaded instead.
    private static readonly TimeSpan LiveCompletionTimeout = TimeSpan.FromSeconds(5);

    private SettingsContainer _settings = new();
    private WorkflowType? _activeType;
    private CancellationTokenSource? _processingCts;
    private IStreamingTranscriptionSession? _liveSession;
    private IReadOnlyList<string> _liveCustomTerms = [];

    public WorkflowPhase Phase { get; private set; } = WorkflowPhase.Idle;
    public WorkflowType? ActiveType => _activeType;
    public bool IsRecording => recorder.IsRecording;
    public float AudioLevel => recorder.AudioLevel;

    public event EventHandler<WorkflowPhase>? PhaseChanged;
    public event EventHandler<string>? OutputProduced;

    /// <summary>Raised when live transcription failed and the recording was uploaded as a file instead.</summary>
    public event EventHandler<Exception>? LiveTranscriptionFailed;

    public async Task StartAsync(WorkflowType type, SettingsContainer settings, CancellationToken cancellationToken = default)
    {
        await ResetAsync(cancellationToken);
        _settings = settings;
        _activeType = type;
        _processingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        SetPhase(WorkflowPhase.Running("Aufnahme läuft ..."));
        StartLiveSession(type);
        try
        {
            await recorder.StartAsync(cancellationToken);
        }
        catch
        {
            await EndLiveSessionAsync(DetachLiveSession());
            throw;
        }
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
            await EndLiveSessionAsync(DetachLiveSession());
            SetPhase(WorkflowPhase.Idle);
            return;
        }

        RecordedAudio audio;
        try
        {
            audio = await recorder.StopAsync(cancellationToken);
        }
        catch
        {
            await EndLiveSessionAsync(DetachLiveSession());
            throw;
        }

        // Detach only after the recorder stopped: stopping flushes the last buffers through
        // AudioAvailable, and those carry the final words of the utterance.
        var liveSession = DetachLiveSession();
        await ProcessAsync(_activeType.Value, audio, liveSession, _processingCts?.Token ?? cancellationToken);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        _processingCts?.Cancel();
        await EndLiveSessionAsync(DetachLiveSession());
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

    private async Task ProcessAsync(
        WorkflowType type,
        RecordedAudio audio,
        IStreamingTranscriptionSession? liveSession,
        CancellationToken cancellationToken)
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
            var (rawText, customTerms) = await TranscribeAsync(type, audio, liveSession, cancellationToken);
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
            await EndLiveSessionAsync(liveSession);
            TryDelete(audio.FilePath);
        }
    }

    private void StartLiveSession(WorkflowType type)
    {
        if (streamingTranscription is null || !_settings.App.LiveTranscriptionEnabled || IsLocal(type))
        {
            return;
        }

        // The recording length is unknown up front, so the full term list is sent. Keywords are
        // literal hints (not a prompt), which makes the short-clip echo problem far less likely.
        _liveCustomTerms = _settings.TextImprovement.CustomTerms.ToArray();
        _liveSession = streamingTranscription.StartSession(
            new StreamingTranscriptionOptions(_liveCustomTerms, _settings.Transcription.Language));
        recorder.AudioAvailable += ForwardAudioToLiveSession;
    }

    private IStreamingTranscriptionSession? DetachLiveSession()
    {
        recorder.AudioAvailable -= ForwardAudioToLiveSession;
        var session = _liveSession;
        _liveSession = null;
        return session;
    }

    private void ForwardAudioToLiveSession(object? sender, ReadOnlyMemory<byte> pcm16) =>
        _liveSession?.AppendAudio(pcm16.Span);

    private static async Task EndLiveSessionAsync(IStreamingTranscriptionSession? session)
    {
        if (session is null)
        {
            return;
        }

        try
        {
            await session.DisposeAsync();
        }
        catch
        {
            // Closing the socket is best-effort.
        }
    }

    // For very short clips the biasing prompt does more harm than good: the model is more likely
    // to echo the term list than to transcribe the brief utterance.
    private IReadOnlyList<string> CustomTermsFor(RecordedAudio audio) =>
        audio.Duration.TotalSeconds >= 0.9 ? _settings.TextImprovement.CustomTerms : [];

    private async Task<(string Text, IReadOnlyList<string> CustomTerms)> TranscribeAsync(
        WorkflowType type,
        RecordedAudio audio,
        IStreamingTranscriptionSession? liveSession,
        CancellationToken cancellationToken)
    {
        if (liveSession is not null)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(LiveCompletionTimeout);
                var liveText = await liveSession.CompleteAsync(timeout.Token);
                if (!string.IsNullOrWhiteSpace(liveText))
                {
                    return (liveText, _liveCustomTerms);
                }

                throw new InvalidOperationException("Live-Transkription lieferte keinen Text.");
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Fall through to the regular file upload; the recording is still on disk.
                LiveTranscriptionFailed?.Invoke(this, ex);
            }
        }

        var customTerms = CustomTermsFor(audio);
        var request = new TranscriptionRequest(
            audio.FilePath,
            customTerms,
            _settings.Transcription.Language,
            audio.Duration,
            _settings.App.SelectedLocalTranscriptionModelName);

        var text = IsLocal(type)
            ? await localTranscription.TranscribeAsync(request, cancellationToken)
            : await remoteTranscription.TranscribeAsync(request, cancellationToken);
        return (text, customTerms);
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
