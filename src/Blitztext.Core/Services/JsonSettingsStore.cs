using System.Text.Json;
using System.Text.Json.Serialization;
using Blitztext.Core.Abstractions;
using Blitztext.Core.Models;

namespace Blitztext.Core.Services;

public sealed class JsonSettingsStore(string settingsPath) : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<SettingsContainer> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(settingsPath))
        {
            return new SettingsContainer();
        }

        SettingsContainer settings;
        try
        {
            await using var stream = File.OpenRead(settingsPath);
            settings = await JsonSerializer.DeserializeAsync<SettingsContainer>(stream, JsonOptions, cancellationToken)
                ?? new SettingsContainer();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Settings written by an incompatible/older version (e.g. a removed workflow type)
            // must not crash startup; fall back to defaults instead.
            settings = new SettingsContainer();
        }

        WorkflowSettings.EnsureDefaults(settings.App);
        return settings;
    }

    public async Task SaveAsync(SettingsContainer settings, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(settingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
    }
}
