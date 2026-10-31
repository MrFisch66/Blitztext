namespace Blitztext.Core.Models;

public enum WorkflowType
{
    Transcription,
    LocalTranscription,
    TextImprover
}

public static class WorkflowTypeExtensions
{
    public static IReadOnlyList<WorkflowType> MainMenuCases { get; } =
    [
        WorkflowType.Transcription,
        WorkflowType.TextImprover
    ];

    public static string DisplayName(this WorkflowType type) => type switch
    {
        WorkflowType.Transcription => "Blitztext",
        WorkflowType.LocalTranscription => "Blitztext Lokal",
        WorkflowType.TextImprover => "Blitztext+",
        _ => type.ToString()
    };

    public static string Subtitle(this WorkflowType type) => type switch
    {
        WorkflowType.Transcription => "Sprache rein. Text raus.",
        WorkflowType.LocalTranscription => "Nur lokal. Kein Server.",
        WorkflowType.TextImprover => "Geschrieben sprechen.",
        _ => string.Empty
    };

    public static string WindowsHotkeyLabel(this WorkflowType type) => type switch
    {
        WorkflowType.Transcription => "Ctrl + Win",
        WorkflowType.LocalTranscription => "Alt + Win",
        WorkflowType.TextImprover => "Ctrl + Alt + Win",
        _ => string.Empty
    };
}
