using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Blitztext.Core.Abstractions;
using Blitztext.Core.Models;

namespace Blitztext.LocalTranscription;

public sealed class WhisperCppLocalTranscriptionService : ITranscriptionBackend, ILocalModelService
{
    private static readonly WhisperModel[] SupportedModels =
    [
        new(LocalTranscriptionDefaults.RecommendedFastModelName, "Whisper Small", "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin"),
        new(LocalTranscriptionDefaults.FastModelName, "Whisper Medium", "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin"),
        new(LocalTranscriptionDefaults.DefaultModelName, "Whisper Large v3 Turbo", "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin")
    ];

    private readonly string _modelsDirectory;
    private readonly string _runtimeDirectory;
    private readonly string _executablePath;
    private readonly string? _environmentExecutablePath;
    private readonly HttpClient _httpClient;

    public WhisperCppLocalTranscriptionService(string appDataDirectory, string? executablePath = null, HttpClient? httpClient = null)
    {
        _modelsDirectory = Path.Combine(appDataDirectory, "models", "whispercpp");
        _runtimeDirectory = Path.Combine(appDataDirectory, "tools", "whispercpp");
        _environmentExecutablePath = Environment.GetEnvironmentVariable("BLITZTEXT_WHISPER_CPP");
        _executablePath = executablePath ?? _environmentExecutablePath ?? Path.Combine(_runtimeDirectory, "whisper-cli.exe");
        _httpClient = httpClient ?? new HttpClient();
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Blitztext-Windows/1.0");
        }
    }

    public IReadOnlyList<LocalModelInfo> GetModelOptions()
    {
        Directory.CreateDirectory(_modelsDirectory);
        return SupportedModels
            .Select(model => new LocalModelInfo(model.Id, model.DisplayName, IsModelInstalled(model.Id), ModelPath(model.Id)))
            .ToArray();
    }

    public bool IsModelInstalled(string modelName) => File.Exists(ModelPath(NormalizeModelName(modelName)));

    public LocalRuntimeInfo GetRuntimeInfo() => new(File.Exists(_executablePath), _executablePath);

    public async Task<LocalRuntimeInfo> InstallRuntimeAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(_executablePath))
        {
            progress?.Report(1);
            return GetRuntimeInfo();
        }

        if (!string.IsNullOrWhiteSpace(_environmentExecutablePath) &&
            !string.Equals(_environmentExecutablePath, Path.Combine(_runtimeDirectory, "whisper-cli.exe"), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"BLITZTEXT_WHISPER_CPP zeigt auf '{_environmentExecutablePath}', aber die Datei existiert nicht. Entferne die Umgebungsvariable oder setze sie auf eine vorhandene whisper-cli.exe.");
        }

        Directory.CreateDirectory(_runtimeDirectory);
        var assetUrl = await ResolveRuntimeAssetUrlAsync(cancellationToken);
        var zipPath = Path.Combine(Path.GetTempPath(), $"blitztext-whispercpp-{Guid.NewGuid():N}.zip");
        var extractRoot = Path.Combine(Path.GetTempPath(), $"blitztext-whispercpp-{Guid.NewGuid():N}");

        try
        {
            await DownloadFileAsync(assetUrl, zipPath, progress, cancellationToken);
            Directory.CreateDirectory(extractRoot);
            ZipFile.ExtractToDirectory(zipPath, extractRoot, overwriteFiles: true);

            var executable = Directory
                .EnumerateFiles(extractRoot, "whisper-cli.exe", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (executable is null)
            {
                throw new InvalidOperationException("Das whisper.cpp Release enthielt keine whisper-cli.exe.");
            }

            CopyDirectory(Path.GetDirectoryName(executable)!, _runtimeDirectory);
            progress?.Report(1);
            return GetRuntimeInfo();
        }
        finally
        {
            TryDelete(zipPath);
            TryDeleteDirectory(extractRoot);
        }
    }

    public async Task<LocalModelInfo> InstallAsync(
        string modelName,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var model = ResolveModel(modelName);
        var destination = ModelPath(model.Id);
        Directory.CreateDirectory(_modelsDirectory);

        if (File.Exists(destination))
        {
            progress?.Report(1);
            return new LocalModelInfo(model.Id, model.DisplayName, true, destination);
        }

        var tempPath = destination + ".download";
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        using var response = await _httpClient.GetAsync(model.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(tempPath);

        var buffer = new byte[1024 * 128];
        long readTotal = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            readTotal += read;

            if (totalBytes is > 0)
            {
                progress?.Report(Math.Clamp(readTotal / (double)totalBytes.Value, 0, 1));
            }
        }

        output.Close();
        File.Move(tempPath, destination, overwrite: true);
        progress?.Report(1);

        return new LocalModelInfo(model.Id, model.DisplayName, true, destination);
    }

    public async Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        var model = ResolveModel(request.LocalModelName ?? LocalTranscriptionDefaults.RecommendedFastModelName);
        var modelPath = ModelPath(model.Id);
        if (!File.Exists(modelPath))
        {
            throw new InvalidOperationException($"Lokales Whisper-Modell fehlt: {modelPath}");
        }

        if (!File.Exists(_executablePath))
        {
            throw new InvalidOperationException(
                "whisper.cpp ist noch nicht installiert. Öffne Blitztext, Bereich 'Lokales Whisper', und installiere die Runtime.");
        }

        var outputStem = Path.Combine(Path.GetTempPath(), $"blitztext-local-{Guid.NewGuid():N}");
        var outputTextPath = outputStem + ".txt";

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = _executablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            process.StartInfo.ArgumentList.Add("-m");
            process.StartInfo.ArgumentList.Add(modelPath);
            process.StartInfo.ArgumentList.Add("-f");
            process.StartInfo.ArgumentList.Add(request.AudioPath);
            process.StartInfo.ArgumentList.Add("-l");
            process.StartInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(request.Language) ? "auto" : request.Language.Trim());
            process.StartInfo.ArgumentList.Add("-otxt");
            process.StartInfo.ArgumentList.Add("-of");
            process.StartInfo.ArgumentList.Add(outputStem);

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Lokale Transkription fehlgeschlagen: {stderr.Trim()}");
            }

            var text = File.Exists(outputTextPath)
                ? await File.ReadAllTextAsync(outputTextPath, cancellationToken)
                : stdout;

            text = text.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("Das lokale Modell hat keinen Text erkannt.");
            }

            return text;
        }
        finally
        {
            TryDelete(outputTextPath);
        }
    }

    private string ModelPath(string modelName) => Path.Combine(_modelsDirectory, NormalizeModelName(modelName));

    private async Task<string> ResolveRuntimeAssetUrlAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            "https://api.github.com/repos/ggml-org/whisper.cpp/releases/latest",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var assets = document.RootElement.GetProperty("assets").EnumerateArray();
        var preferredName = Environment.Is64BitProcess ? "whisper-bin-x64.zip" : "whisper-bin-Win32.zip";

        foreach (var asset in assets)
        {
            if (asset.GetProperty("name").GetString()?.Equals(preferredName, StringComparison.OrdinalIgnoreCase) == true)
            {
                return asset.GetProperty("browser_download_url").GetString()
                    ?? throw new InvalidOperationException("Das whisper.cpp Release-Asset hat keine Download-URL.");
            }
        }

        assets = document.RootElement.GetProperty("assets").EnumerateArray();
        foreach (var asset in assets)
        {
            var name = asset.GetProperty("name").GetString() ?? string.Empty;
            if (name.Contains("whisper-bin", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("blas", StringComparison.OrdinalIgnoreCase) &&
                (Environment.Is64BitProcess ? name.Contains("x64", StringComparison.OrdinalIgnoreCase) : name.Contains("Win32", StringComparison.OrdinalIgnoreCase)))
            {
                return asset.GetProperty("browser_download_url").GetString()
                    ?? throw new InvalidOperationException("Das whisper.cpp Release-Asset hat keine Download-URL.");
            }
        }

        throw new InvalidOperationException("Kein passendes Windows-Release von whisper.cpp gefunden.");
    }

    private async Task DownloadFileAsync(
        string url,
        string destinationPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(destinationPath);
        var buffer = new byte[1024 * 128];
        long readTotal = 0;

        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            readTotal += read;

            if (totalBytes is > 0)
            {
                progress?.Report(Math.Clamp(readTotal / (double)totalBytes.Value * 0.85, 0, 0.85));
            }
        }

        progress?.Report(0.9);
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }

    private static string NormalizeModelName(string modelName)
    {
        return string.IsNullOrWhiteSpace(modelName)
            ? LocalTranscriptionDefaults.RecommendedFastModelName
            : modelName.Trim();
    }

    private static WhisperModel ResolveModel(string modelName)
    {
        var normalized = NormalizeModelName(modelName);
        return SupportedModels.FirstOrDefault(model => string.Equals(model.Id, normalized, StringComparison.OrdinalIgnoreCase))
            ?? SupportedModels[0];
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
            // Temporary local transcription outputs are cleaned best-effort.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Temporary downloads are cleaned best-effort.
        }
    }

    private sealed record WhisperModel(string Id, string DisplayName, string DownloadUrl);
}
