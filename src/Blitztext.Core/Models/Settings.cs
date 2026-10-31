using System.Text.Json.Serialization;

namespace Blitztext.Core.Models;

public enum HotkeyMode
{
    Hold,
    Toggle
}

public enum TranscriptionBackendKind
{
    Remote,
    Local
}

public enum TextTone
{
    Formal,
    Neutral,
    Casual
}

public sealed record AppSettings
{
    public HotkeyMode HotkeyMode { get; set; } = HotkeyMode.Hold;
    public bool HasSeenOnboarding { get; set; }
    public bool SecureLocalModeEnabled { get; set; }

    /// <summary>Stream audio to the transcriber while recording instead of uploading it afterwards.</summary>
    public bool LiveTranscriptionEnabled { get; set; } = true;
    public string SelectedLocalTranscriptionModelName { get; set; } = LocalTranscriptionDefaults.ParakeetModelName;
    public bool HasAutoSelectedFastLocalModel { get; set; }

    // Persisted position of the floating status pill (null = default bottom-center).
    public double? OverlayLeft { get; set; }
    public double? OverlayTop { get; set; }

    public Dictionary<WorkflowType, WorkflowSettings> Workflows { get; set; } = WorkflowSettings.CreateDefaults();
}

public sealed record TranscriptionSettings
{
    public string Language { get; set; } = "de";
}

public sealed record TextImprovementSettings
{
    public string SystemPrompt { get; set; } = string.Empty;
    public List<string> CustomTerms { get; set; } = [];
    public string Context { get; set; } = string.Empty;
    public TextTone Tone { get; set; } = TextTone.Neutral;
    public string CustomName { get; set; } = string.Empty;
}

public sealed record SettingsContainer
{
    public AppSettings App { get; set; } = new();
    public TranscriptionSettings Transcription { get; set; } = new();
    public TextImprovementSettings TextImprovement { get; set; } = new();

    [JsonIgnore]
    public IReadOnlyList<string> CustomTerms => TextImprovement.CustomTerms;
}

public static class LocalTranscriptionDefaults
{
    public const string ParakeetModelName = "parakeet-tdt-0.6b-v3-int8";

    public const string RecommendedFastModelName = "ggml-small.bin";
    public const string FastModelName = "ggml-medium.bin";
    public const string DefaultModelName = "ggml-large-v3-turbo.bin";
}

public sealed record WorkflowSettings
{
    public bool Enabled { get; set; } = true;
    public HotkeyBinding Hotkey { get; set; } = new();

    public static Dictionary<WorkflowType, WorkflowSettings> CreateDefaults() => new()
    {
        [WorkflowType.Transcription] = new WorkflowSettings
        {
            Enabled = true,
            Hotkey = new HotkeyBinding(Control: true, Windows: true)
        },
        [WorkflowType.LocalTranscription] = new WorkflowSettings
        {
            Enabled = true,
            Hotkey = new HotkeyBinding(Alt: true, Windows: true)
        },
        [WorkflowType.TextImprover] = new WorkflowSettings
        {
            Enabled = true,
            Hotkey = new HotkeyBinding(Control: true, Alt: true, Windows: true)
        }
    };

    public static void EnsureDefaults(AppSettings appSettings)
    {
        var defaults = CreateDefaults();
        foreach (var (workflowType, defaultSettings) in defaults)
        {
            if (!appSettings.Workflows.TryGetValue(workflowType, out var existing))
            {
                appSettings.Workflows[workflowType] = defaultSettings;
                continue;
            }

            // Only replace a binding that is genuinely unusable. Checking the regular key alone
            // would wrongly discard valid modifier-only combinations (e.g. Ctrl + Win), which
            // have no regular key by design.
            if (!existing.Hotkey.IsUsable())
            {
                existing.Hotkey = defaultSettings.Hotkey;
            }
        }
    }
}

public sealed record HotkeyBinding(
    bool Control = false,
    bool Alt = false,
    bool Shift = false,
    bool Windows = false,
    string Key = "")
{
    public bool HasModifier => Control || Alt || Shift || Windows;

    [JsonIgnore]
    public int ModifierCount =>
        (Control ? 1 : 0) + (Alt ? 1 : 0) + (Shift ? 1 : 0) + (Windows ? 1 : 0);

    /// <summary>
    /// A binding without a regular key (e.g. Ctrl + Win) that triggers purely on modifiers.
    /// Requires a low-level keyboard hook because Win32 RegisterHotKey cannot register these.
    /// </summary>
    [JsonIgnore]
    public bool IsModifierOnly => HasModifier && string.IsNullOrWhiteSpace(Key);

    [JsonIgnore]
    public string DisplayText
    {
        get
        {
            var parts = new List<string>();
            if (Control)
            {
                parts.Add("Ctrl");
            }

            if (Alt)
            {
                parts.Add("Alt");
            }

            if (Shift)
            {
                parts.Add("Shift");
            }

            if (Windows)
            {
                parts.Add("Win");
            }

            if (!string.IsNullOrWhiteSpace(Key))
            {
                parts.Add(DisplayKey(Key));
            }

            return parts.Count == 0 ? "Nicht gesetzt" : string.Join(" + ", parts);
        }
    }

    // A regular key needs at least one modifier; a modifier-only combo needs at least two
    // modifiers so a single stray Ctrl/Alt/Shift/Win press cannot fire it constantly.
    public bool IsUsable() =>
        (HasModifier && !string.IsNullOrWhiteSpace(Key)) ||
        (IsModifierOnly && ModifierCount >= 2);

    public static string DisplayKey(string key) => key switch
    {
        "Space" => "Space",
        "OemPlus" => "+",
        "OemMinus" => "-",
        "OemComma" => ",",
        "OemPeriod" => ".",
        _ when key.StartsWith("D", StringComparison.OrdinalIgnoreCase) && key.Length == 2 && char.IsDigit(key[1]) => key[1].ToString(),
        _ => key.ToUpperInvariant()
    };
}
