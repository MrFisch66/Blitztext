using Blitztext.Core.Abstractions;
using Blitztext.Core.Models;
using Blitztext.Core.Workflows;

namespace Blitztext.Core.Tests;

public sealed class WorkflowRunnerTests
{
    [Fact]
    public async Task TextImprover_TranscribesThenRewrites()
    {
        using var recording = TempRecording();
        var recorder = new FakeRecorder(recording.Path, TimeSpan.FromSeconds(1));
        var remote = new FakeTranscriptionBackend("roher text");
        var local = new FakeTranscriptionBackend("local text");
        var rewrite = new FakeRewriteClient("verbesserter text");
        var runner = new BlitztextWorkflowRunner(recorder, remote, local, rewrite);
        var settings = new SettingsContainer();
        settings.TextImprovement.CustomTerms.Add("Blitztext");

        string? output = null;
        runner.OutputProduced += (_, text) => output = text;

        await runner.StartAsync(WorkflowType.TextImprover, settings);
        await runner.StopAsync();

        Assert.Equal("verbesserter text", output);
        Assert.Single(remote.Requests);
        Assert.Empty(local.Requests);
        Assert.Single(rewrite.Requests);
        Assert.Equal("Blitztext", remote.Requests[0].CustomTerms.Single());
    }

    [Fact]
    public async Task SecureTranscription_UsesLocalBackend()
    {
        using var recording = TempRecording();
        var recorder = new FakeRecorder(recording.Path, TimeSpan.FromSeconds(1));
        var remote = new FakeTranscriptionBackend("remote text");
        var local = new FakeTranscriptionBackend("local text");
        var rewrite = new FakeRewriteClient("unused");
        var runner = new BlitztextWorkflowRunner(recorder, remote, local, rewrite);
        var settings = new SettingsContainer
        {
            App = { SecureLocalModeEnabled = true }
        };

        string? output = null;
        runner.OutputProduced += (_, text) => output = text;

        await runner.StartAsync(WorkflowType.Transcription, settings);
        await runner.StopAsync();

        Assert.Equal("local text", output);
        Assert.Empty(remote.Requests);
        Assert.Single(local.Requests);
        Assert.Empty(rewrite.Requests);
    }

    [Fact]
    public async Task SilentTap_ProducesNoOutputAndDoesNotTranscribe()
    {
        using var recording = TempRecording();
        // Short and near-silent: the signature of an accidental hotkey tap.
        var recorder = new FakeRecorder(recording.Path, TimeSpan.FromMilliseconds(500), peakAmplitude: 0.004f);
        var remote = new FakeTranscriptionBackend("halluzinierter text");
        var local = new FakeTranscriptionBackend("local text");
        var rewrite = new FakeRewriteClient("unused");
        var runner = new BlitztextWorkflowRunner(recorder, remote, local, rewrite);

        string? output = null;
        runner.OutputProduced += (_, text) => output = text;

        await runner.StartAsync(WorkflowType.Transcription, new SettingsContainer());
        await runner.StopAsync();

        Assert.Null(output);
        Assert.Empty(remote.Requests);
        Assert.Empty(local.Requests);
        Assert.Equal(WorkflowPhaseKind.Error, runner.Phase.Kind);
    }

    [Fact]
    public async Task LongSilentRecording_ProducesNoOutputAndDoesNotTranscribe()
    {
        using var recording = TempRecording();
        // Holding the hotkey for seconds without speaking: long, but effectively silent.
        var recorder = new FakeRecorder(recording.Path, TimeSpan.FromSeconds(3), peakAmplitude: 0.004f);
        var remote = new FakeTranscriptionBackend("Eigennamen und Begriffe: Codex, Claude Code");
        var local = new FakeTranscriptionBackend("local text");
        var rewrite = new FakeRewriteClient("unused");
        var runner = new BlitztextWorkflowRunner(recorder, remote, local, rewrite);

        string? output = null;
        runner.OutputProduced += (_, text) => output = text;

        await runner.StartAsync(WorkflowType.Transcription, new SettingsContainer());
        await runner.StopAsync();

        Assert.Null(output);
        Assert.Empty(remote.Requests);
        Assert.Equal(WorkflowPhaseKind.Error, runner.Phase.Kind);
    }

    [Fact]
    public async Task EchoedPromptTranscript_ProducesNoOutput()
    {
        using var recording = TempRecording();
        // Audible noise passes the silence check, but the transcriber echoes the biasing prompt.
        var recorder = new FakeRecorder(recording.Path, TimeSpan.FromSeconds(3), peakAmplitude: 0.2f);
        var remote = new FakeTranscriptionBackend("Eigennamen und Begriffe: Codex, Claude Code");
        var local = new FakeTranscriptionBackend("local text");
        var rewrite = new FakeRewriteClient("unused");
        var runner = new BlitztextWorkflowRunner(recorder, remote, local, rewrite);
        var settings = new SettingsContainer();
        settings.TextImprovement.CustomTerms.AddRange(["Codex", "Claude Code"]);

        string? output = null;
        runner.OutputProduced += (_, text) => output = text;

        await runner.StartAsync(WorkflowType.Transcription, settings);
        await runner.StopAsync();

        Assert.Null(output);
        Assert.Single(remote.Requests);
        Assert.Equal(WorkflowPhaseKind.Error, runner.Phase.Kind);
    }

    [Fact]
    public async Task LiveTranscription_IsUsedInsteadOfUpload()
    {
        using var recording = TempRecording();
        var recorder = new FakeRecorder(recording.Path, TimeSpan.FromSeconds(2));
        var remote = new FakeTranscriptionBackend("upload text");
        var live = new FakeStreamingBackend("live text");
        var runner = new BlitztextWorkflowRunner(recorder, remote, new FakeTranscriptionBackend("local"), new FakeRewriteClient("unused"), live);
        var settings = new SettingsContainer();
        settings.TextImprovement.CustomTerms.Add("Blitztext");

        string? output = null;
        runner.OutputProduced += (_, text) => output = text;

        await runner.StartAsync(WorkflowType.Transcription, settings);
        recorder.Emit([1, 0, 2, 0]);
        await runner.StopAsync();

        Assert.Equal("live text", output);
        Assert.Empty(remote.Requests);
        Assert.Equal(4, live.Session!.ReceivedBytes);
        Assert.Equal("Blitztext", live.Options!.CustomTerms.Single());
        Assert.True(live.Session.Disposed);
    }

    [Fact]
    public async Task LiveTranscriptionFailure_FallsBackToUpload()
    {
        using var recording = TempRecording();
        var recorder = new FakeRecorder(recording.Path, TimeSpan.FromSeconds(2));
        var remote = new FakeTranscriptionBackend("upload text");
        var live = new FakeStreamingBackend(failure: new InvalidOperationException("socket"));
        var runner = new BlitztextWorkflowRunner(recorder, remote, new FakeTranscriptionBackend("local"), new FakeRewriteClient("unused"), live);

        string? output = null;
        Exception? reported = null;
        runner.OutputProduced += (_, text) => output = text;
        runner.LiveTranscriptionFailed += (_, ex) => reported = ex;

        await runner.StartAsync(WorkflowType.Transcription, new SettingsContainer());
        await runner.StopAsync();

        Assert.Equal("upload text", output);
        Assert.Single(remote.Requests);
        Assert.Equal("socket", reported?.Message);
    }

    [Fact]
    public async Task LiveTranscription_IsNotUsedForLocalOrWhenDisabled()
    {
        using var recording = TempRecording();
        var recorder = new FakeRecorder(recording.Path, TimeSpan.FromSeconds(2));
        var live = new FakeStreamingBackend("live text");
        var runner = new BlitztextWorkflowRunner(recorder, new FakeTranscriptionBackend("upload"), new FakeTranscriptionBackend("local"), new FakeRewriteClient("unused"), live);

        await runner.StartAsync(WorkflowType.LocalTranscription, new SettingsContainer());
        await runner.StopAsync();
        await runner.StartAsync(WorkflowType.Transcription, new SettingsContainer { App = { LiveTranscriptionEnabled = false } });
        await runner.StopAsync();

        Assert.Equal(0, live.SessionsStarted);
    }

    [Fact]
    public async Task SilentRecording_DisposesLiveSessionWithoutCompleting()
    {
        using var recording = TempRecording();
        var recorder = new FakeRecorder(recording.Path, TimeSpan.FromMilliseconds(500), peakAmplitude: 0.004f);
        var live = new FakeStreamingBackend("halluziniert");
        var runner = new BlitztextWorkflowRunner(recorder, new FakeTranscriptionBackend("upload"), new FakeTranscriptionBackend("local"), new FakeRewriteClient("unused"), live);

        await runner.StartAsync(WorkflowType.Transcription, new SettingsContainer());
        await runner.StopAsync();

        Assert.False(live.Session!.Completed);
        Assert.True(live.Session.Disposed);
    }

    private static TempFile TempRecording()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blitztext-test-{Guid.NewGuid():N}.wav");
        File.WriteAllText(path, "audio");
        return new TempFile(path);
    }

    private sealed class TempFile(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }

    private sealed class FakeRecorder(string path, TimeSpan duration, float peakAmplitude = 0.5f) : IAudioRecorder
    {
        public bool IsRecording { get; private set; }
        public float AudioLevel => IsRecording ? 0.5f : 0;

        public event EventHandler<ReadOnlyMemory<byte>>? AudioAvailable;

        public void Emit(byte[] pcm16) => AudioAvailable?.Invoke(this, pcm16);

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = true;
            return Task.CompletedTask;
        }

        public Task<RecordedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = false;
            return Task.FromResult(new RecordedAudio(path, duration, peakAmplitude));
        }

        public Task DiscardAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeTranscriptionBackend(string text) : ITranscriptionBackend
    {
        public List<TranscriptionRequest> Requests { get; } = [];

        public Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(text);
        }
    }

    private sealed class FakeStreamingBackend(string text = "", Exception? failure = null) : IStreamingTranscriptionBackend
    {
        public int SessionsStarted { get; private set; }
        public StreamingTranscriptionOptions? Options { get; private set; }
        public FakeStreamingSession? Session { get; private set; }

        public IStreamingTranscriptionSession StartSession(StreamingTranscriptionOptions options)
        {
            SessionsStarted++;
            Options = options;
            Session = new FakeStreamingSession(text, failure);
            return Session;
        }
    }

    private sealed class FakeStreamingSession(string text, Exception? failure) : IStreamingTranscriptionSession
    {
        public int ReceivedBytes { get; private set; }
        public bool Completed { get; private set; }
        public bool Disposed { get; private set; }

        public void AppendAudio(ReadOnlySpan<byte> pcm16) => ReceivedBytes += pcm16.Length;

        public Task<string> CompleteAsync(CancellationToken cancellationToken = default)
        {
            Completed = true;
            return failure is null ? Task.FromResult(text) : Task.FromException<string>(failure);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeRewriteClient(string text) : ITextRewriteClient
    {
        public List<TextRewriteRequest> Requests { get; } = [];

        public Task<string> RewriteAsync(TextRewriteRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(text);
        }
    }
}
