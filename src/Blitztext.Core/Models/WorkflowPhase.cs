namespace Blitztext.Core.Models;

public enum WorkflowPhaseKind
{
    Idle,
    Running,
    Done,
    Error
}

public sealed record WorkflowPhase(WorkflowPhaseKind Kind, string Message = "")
{
    public static WorkflowPhase Idle { get; } = new(WorkflowPhaseKind.Idle);

    public bool IsActive => Kind != WorkflowPhaseKind.Idle;

    public static WorkflowPhase Running(string message) => new(WorkflowPhaseKind.Running, message);

    public static WorkflowPhase Done(string text) => new(WorkflowPhaseKind.Done, text);

    public static WorkflowPhase Error(string message) => new(WorkflowPhaseKind.Error, message);
}
