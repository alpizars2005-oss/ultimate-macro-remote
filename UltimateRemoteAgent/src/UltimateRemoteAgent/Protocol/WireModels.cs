namespace UltimateRemoteAgent.Protocol;

public enum RemoteOperation
{
    GetStatus,
    ListStrategies,
    StartStrategy,
    StopSafe,
    SwitchStrategy,
}

public enum MacroState
{
    NotRunning,
    Idle,
    Running,
    Unknown,
}

public enum CommandUpdateStatus
{
    Accepted,
    Executing,
    Completed,
    Failed,
}

public enum ActionResult
{
    StrategyStarted,
    StoppedSafe,
    SwitchedSafe,
}

public sealed record MacroSnapshot(
    MacroState MacroState,
    bool RobloxRunning,
    string? CurrentStrategyId);

public sealed record StrategySummary(string StrategyId, string Name);

public sealed record CommandError(string Code, string Message);

public abstract record ServerMessage;

public sealed record ReconciliationCommand(Guid CommandId, RemoteOperation Operation);

public sealed record WelcomeMessage(
    int HeartbeatIntervalSeconds,
    DateTimeOffset ServerTime,
    IReadOnlyList<ReconciliationCommand> ReconcileCommands) : ServerMessage;

public sealed record CommandArguments(string? StrategyId)
{
    public static CommandArguments Empty { get; } = new((string?)null);
}

public sealed record CommandMessage(
    Guid CommandId,
    RemoteOperation Operation,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    CommandArguments Arguments) : ServerMessage;
