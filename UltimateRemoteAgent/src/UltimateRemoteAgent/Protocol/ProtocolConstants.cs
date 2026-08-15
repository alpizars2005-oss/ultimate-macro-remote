namespace UltimateRemoteAgent.Protocol;

public static class ProtocolConstants
{
    public const int Version = 1;
    public const int MaximumMessageBytes = 64 * 1024;
    public const int MaximumStrategies = 500;

    public static IReadOnlyList<RemoteOperation> R3Capabilities { get; } =
        Array.AsReadOnly(
            new[]
            {
                RemoteOperation.GetStatus,
                RemoteOperation.ListStrategies,
            });
}
