using System.Buffers.Binary;
using Blitztext.Core.Abstractions;
using Blitztext.Core.Models;
using SherpaOnnx;

namespace Blitztext.LocalTranscription;

/// <summary>
/// Local transcription with NVIDIA Parakeet TDT 0.6B v3 (25 European languages incl. German) via
/// sherpa-onnx. Unlike the whisper.cpp path, the model stays loaded in-process, so a dictation only
/// pays for the actual decoding — no process start and no model load per recording.
/// </summary>
public sealed class ParakeetLocalTranscriptionService : ITranscriptionBackend, IDisposable
{
    public const string DisplayName = "Parakeet v3 (schnellstes, empfohlen)";

    private const string DownloadBaseUrl =
        "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main/";

    private static readonly string[] ModelFiles = ["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"];

    private readonly string _modelDirectory;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _recognizerGate = new(1, 1);
    private OfflineRecognizer? _recognizer;

    public ParakeetLocalTranscriptionService(string appDataDirectory, HttpClient? httpClient = null)
    {
        _modelDirectory = Path.Combine(appDataDirectory, "models", "parakeet", LocalTranscriptionDefaults.ParakeetModelName);
        _httpClient = httpClient ?? new HttpClient();
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Blitztext-Windows/1.0");
        }
    }

    public static bool Handles(string? modelName) =>
        string.Equals(modelName?.Trim(), LocalTranscriptionDefaults.ParakeetModelName, StringComparison.OrdinalIgnoreCase);

    public string ModelDirectory => _modelDirectory;

    public bool IsInstalled => ModelFiles.All(file => File.Exists(Path.Combine(_modelDirectory, file)));

    public LocalModelInfo GetModelInfo() =>
        new(LocalTranscriptionDefaults.ParakeetModelName, DisplayName, IsInstalled, _modelDirectory);

    public async Task<LocalModelInfo> InstallAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_modelDirectory);

        // Sizes of the published files, used to weight the overall progress (encoder dominates).
        long[] expectedBytes = [652_184_281, 11_845_275, 6_355_277, 93_939];
        var totalBytes = expectedBytes.Sum();
        long completedBytes = 0;

        for (var index = 0; index < ModelFiles.Length; index++)
        {
            var destination = Path.Combine(_modelDirectory, ModelFiles[index]);
            if (!File.Exists(destination))
            {
                var baseBytes = completedBytes;
                var fileProgress = new Progress<long>(read =>
                    progress?.Report(Math.Clamp((baseBytes + read) / (double)totalBytes, 0, 0.99)));
                await DownloadAsync(DownloadBaseUrl + ModelFiles[index], destination, fileProgress, cancellationToken);
            }

            completedBytes += expectedBytes[index];
            progress?.Report(Math.Clamp(completedBytes / (double)totalBytes, 0, 0.99));
        }

        progress?.Report(1);
        return GetModelInfo();
    }

    /// <summary>Loads the model ahead of the first dictation so that one is fast as well.</summary>
    public async Task PrewarmAsync(CancellationToken cancellationToken = default)
    {
        if (IsInstalled)
        {
            await GetRecognizerAsync(cancellationToken);
        }
    }

    public async Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsInstalled)
        {
            throw new InvalidOperationException("Das Parakeet-Modell ist noch nicht installiert. Installiere es im Bereich 'Lokale Modelle'.");
        }

        var samples = await ReadWaveSamplesAsync(request.AudioPath, cancellationToken);
        var recognizer = await GetRecognizerAsync(cancellationToken);

        await _recognizerGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                using var stream = recognizer.CreateStream();
                stream.AcceptWaveform(16000, samples);
                recognizer.Decode(stream);
                var text = stream.Result.Text.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new InvalidOperationException("Das lokale Modell hat keinen Text erkannt.");
                }

                return text;
            }, cancellationToken);
        }
        finally
        {
            _recognizerGate.Release();
        }
    }

    public void Dispose()
    {
        _recognizer?.Dispose();
        _recognizerGate.Dispose();
    }

    private async Task<OfflineRecognizer> GetRecognizerAsync(CancellationToken cancellationToken)
    {
        if (_recognizer is not null)
        {
            return _recognizer;
        }

        await _recognizerGate.WaitAsync(cancellationToken);
        try
        {
            if (_recognizer is null)
            {
                var config = new OfflineRecognizerConfig();
                config.ModelConfig.Transducer.Encoder = Path.Combine(_modelDirectory, "encoder.int8.onnx");
                config.ModelConfig.Transducer.Decoder = Path.Combine(_modelDirectory, "decoder.int8.onnx");
                config.ModelConfig.Transducer.Joiner = Path.Combine(_modelDirectory, "joiner.int8.onnx");
                config.ModelConfig.Tokens = Path.Combine(_modelDirectory, "tokens.txt");
                config.ModelConfig.ModelType = "nemo_transducer";
                config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
                config.ModelConfig.Provider = "cpu";
                config.DecodingMethod = "greedy_search";

                _recognizer = await Task.Run(() => new OfflineRecognizer(config), cancellationToken);
            }

            return _recognizer;
        }
        finally
        {
            _recognizerGate.Release();
        }
    }

    private async Task DownloadAsync(string url, string destination, IProgress<long> progress, CancellationToken cancellationToken)
    {
        var tempPath = destination + ".download";
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = File.Create(tempPath))
        {
            var buffer = new byte[1024 * 128];
            long readTotal = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                readTotal += read;
                progress.Report(readTotal);
            }
        }

        File.Move(tempPath, destination, overwrite: true);
    }

    /// <summary>Reads the recorder's 16 kHz mono PCM16 WAV into normalized float samples.</summary>
    internal static async Task<float[]> ReadWaveSamplesAsync(string path, CancellationToken cancellationToken)
    {
        return WaveToSamples(await File.ReadAllBytesAsync(path, cancellationToken));
    }

    internal static float[] WaveToSamples(byte[] bytes)
    {
        var data = FindDataChunk(bytes);
        var samples = new float[data.Length / 2];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(index * 2, 2)) / 32768f;
        }

        return samples;
    }

    private static ReadOnlySpan<byte> FindDataChunk(byte[] wav)
    {
        if (wav.Length < 12 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
        {
            throw new InvalidOperationException("Die Aufnahme ist keine gültige WAV-Datei.");
        }

        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, offset, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(offset + 4, 4));
            var start = offset + 8;
            if (id == "data")
            {
                var length = Math.Clamp(size, 0, wav.Length - start);
                return wav.AsSpan(start, length & ~1);
            }

            offset = start + size + (size & 1);
        }

        throw new InvalidOperationException("Die Aufnahme enthält keine Audiodaten.");
    }
}
