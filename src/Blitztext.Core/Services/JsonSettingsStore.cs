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

        await using var stream = File.OpenRead(settingsPath);
        var settings = await JsonSerializer.DeserializeAsync<SettingsContainer>(stream, JsonOptions, cancellationToken)
            ?? new SettingsContainer();
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
