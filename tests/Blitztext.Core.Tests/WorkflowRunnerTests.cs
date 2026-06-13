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

    private sealed class FakeRecorder(string path, TimeSpan duration) : IAudioRecorder
    {
        public bool IsRecording { get; private set; }
        public float AudioLevel => IsRecording ? 0.5f : 0;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = true;
            return Task.CompletedTask;
        }

        public Task<RecordedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = false;
            return Task.FromResult(new RecordedAudio(path, duration));
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
