using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Blitztext.Core.Abstractions;

namespace Blitztext.Core.Services;

/// <summary>
/// Live transcription over the OpenAI Realtime API (gpt-live-transcribe). Audio is streamed while
/// the user speaks; releasing the hotkey only commits the buffer, so the final text is ready almost
/// immediately instead of after a full upload + transcription round trip.
/// </summary>
public sealed class OpenAIRealtimeTranscriptionClient(Func<CancellationToken, Task<string?>> apiKeyProvider) : IStreamingTranscriptionBackend
{
    public const string Model = "gpt-live-transcribe";

    // "low" keeps the model close behind the speaker so only a short tail is left after the key is
    // released, without the accuracy loss of "minimal".
    public const string Delay = "low";

    private static readonly Uri RealtimeUri = new("wss://api.openai.com/v1/realtime?intent=transcription");

    public IStreamingTranscriptionSession StartSession(StreamingTranscriptionOptions options) =>
        new Session(apiKeyProvider, options);

    internal static JsonObject BuildSessionUpdate(StreamingTranscriptionOptions options)
    {
        var transcription = new JsonObject
        {
            ["model"] = Model,
            ["delay"] = Delay
        };

        var languages = OpenAITranscriptionOptions.Languages(options.Language);
        if (languages.Count > 0)
        {
            transcription["languages"] = new JsonArray(languages.Select(language => (JsonNode)language).ToArray());
        }

        var keywords = OpenAITranscriptionOptions.Keywords(options.CustomTerms);
        if (keywords.Count > 0)
        {
            transcription["keywords"] = new JsonArray(keywords.Select(keyword => (JsonNode)keyword).ToArray());
        }

        return new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["type"] = "transcription",
                ["audio"] = new JsonObject
                {
                    ["input"] = new JsonObject
                    {
                        ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = 24000 },
                        ["transcription"] = transcription,
                        // The live model has no server-side turn detection; the hotkey release is the turn end.
                        ["turn_detection"] = null
                    }
                }
            }
        };
    }

    private sealed class Session : IStreamingTranscriptionSession
    {
        // Coalesce audio that piled up while connecting into fewer, larger append events.
        private const int MaxAppendBytes = 96 * 1024;

        private readonly Channel<byte[]> _audio = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        private readonly PcmResampler _resampler = new();
        private readonly TaskCompletionSource<string> _transcript = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cts = new();
        private readonly ClientWebSocket _socket = new();
        private readonly Dictionary<string, string> _completedTranscripts = [];
        private readonly object _gate = new();
        private readonly Task _run;
        private string? _committedItemId;

        public Session(Func<CancellationToken, Task<string?>> apiKeyProvider, StreamingTranscriptionOptions options)
        {
            _run = Task.Run(() => RunAsync(apiKeyProvider, options, _cts.Token));
        }

        public void AppendAudio(ReadOnlySpan<byte> pcm16)
        {
            var resampled = _resampler.Resample16To24(pcm16);
            if (resampled.Length > 0)
            {
                _audio.Writer.TryWrite(resampled);
            }
        }

        public Task<string> CompleteAsync(CancellationToken cancellationToken = default)
        {
            _audio.Writer.TryComplete();
            return _transcript.Task.WaitAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _audio.Writer.TryComplete();
            _cts.Cancel();
            _socket.Abort();

            try
            {
                await _run;
            }
            catch
            {
                // The run loop reports its failures through the transcript task.
            }

            _socket.Dispose();
            _cts.Dispose();
        }

        private async Task RunAsync(
            Func<CancellationToken, Task<string?>> apiKeyProvider,
            StreamingTranscriptionOptions options,
            CancellationToken cancellationToken)
        {
            try
            {
                var apiKey = await apiKeyProvider(cancellationToken);
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new InvalidOperationException("OpenAI API Key fehlt. Bitte in den Einstellungen hinterlegen.");
                }

                _socket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                await _socket.ConnectAsync(RealtimeUri, cancellationToken);

                var receiveLoop = ReceiveLoopAsync(cancellationToken);
                await SendAsync(BuildSessionUpdate(options), cancellationToken);

                var buffer = new MemoryStream();
                while (await _audio.Reader.WaitToReadAsync(cancellationToken))
                {
                    buffer.SetLength(0);
                    while (buffer.Length < MaxAppendBytes && _audio.Reader.TryRead(out var chunk))
                    {
                        buffer.Write(chunk);
                    }

                    await SendAsync(new JsonObject
                    {
                        ["type"] = "input_audio_buffer.append",
                        ["audio"] = Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length)
                    }, cancellationToken);
                }

                await SendAsync(new JsonObject { ["type"] = "input_audio_buffer.commit" }, cancellationToken);
                await receiveLoop;
            }
            catch (Exception ex)
            {
                _transcript.TrySetException(ex);
            }
        }

        private async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            var message = new MemoryStream();

            while (!_transcript.Task.IsCompleted)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        var reason = string.IsNullOrWhiteSpace(_socket.CloseStatusDescription)
                            ? "Verbindung zur Live-Transkription wurde beendet."
                            : $"Live-Transkription beendet: {_socket.CloseStatusDescription}";
                        throw new InvalidOperationException(reason);
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                HandleServerEvent(message.GetBuffer().AsSpan(0, (int)message.Length));
            }
        }

        private void HandleServerEvent(ReadOnlySpan<byte> json)
        {
            using var document = JsonDocument.Parse(json.ToArray());
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

            switch (type)
            {
                case "input_audio_buffer.committed":
                    lock (_gate)
                    {
                        _committedItemId = StringProperty(root, "item_id");
                        if (_committedItemId is not null && _completedTranscripts.TryGetValue(_committedItemId, out var early))
                        {
                            _transcript.TrySetResult(early);
                        }
                    }

                    break;

                case "conversation.item.input_audio_transcription.completed":
                    lock (_gate)
                    {
                        var itemId = StringProperty(root, "item_id") ?? string.Empty;
                        // The live model occasionally emits doubled spaces where streamed segments join.
                        var text = Regex.Replace(StringProperty(root, "transcript") ?? string.Empty, " {2,}", " ");
                        if (_committedItemId is null || _committedItemId == itemId)
                        {
                            // Without server VAD there is exactly one item per session: ours.
                            _transcript.TrySetResult(text);
                        }
                        else
                        {
                            _completedTranscripts[itemId] = text;
                        }
                    }

                    break;

                case "conversation.item.input_audio_transcription.failed":
                case "error":
                    var error = root.TryGetProperty("error", out var errorElement) ? StringProperty(errorElement, "message") : null;
                    _transcript.TrySetException(new InvalidOperationException(
                        $"Live-Transkription fehlgeschlagen: {error ?? type}"));
                    break;
            }
        }

        private static string? StringProperty(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
