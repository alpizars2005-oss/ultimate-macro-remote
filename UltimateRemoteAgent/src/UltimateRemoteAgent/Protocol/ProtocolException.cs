namespace UltimateRemoteAgent.Protocol;

public sealed class ProtocolException : Exception
{
    public ProtocolException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public ProtocolException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
