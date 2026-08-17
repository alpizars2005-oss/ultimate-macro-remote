namespace UltimateRemoteAgent.Protocol;

public static class ProtocolConstants
{
    public const int Version = 1;
    public const int MaximumMessageBytes = 64 * 1024;
    public const int MaximumStrategies = 500;

    public static IReadOnlyList<RemoteOperation> AgentCapabilities { get; } =
        Array.AsReadOnly(
            new[]
            {
                RemoteOperation.GetStatus,
                RemoteOperation.ListStrategies,
                RemoteOperation.StartStrategy,
                RemoteOperation.StopSafe,
                RemoteOperation.SwitchStrategy,
            });

    // ProtocolCodec used this name in R3. Keep it as a source-compatible alias while
    // R4 expands the advertised capability set without changing protocol version 1.
    public static IReadOnlyList<RemoteOperation> R3Capabilities => AgentCapabilities;
}
