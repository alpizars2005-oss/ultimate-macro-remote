using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltimateRemoteAgent.Enrollment;

internal sealed record LinkingStart(string Code, DateTimeOffset ExpiresAt);
internal sealed record LinkingReady(string DeviceCredential, Uri WebSocketUri)
{
    public override string ToString() =>
        "LinkingReady { DeviceCredential = [redacted], WebSocketUri = [redacted] }";
}

internal sealed partial class LinkingClient : IDisposable
{
    private const int MaxResponseBytes = 64 * 1024;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    internal LinkingClient(HttpClient? client = null)
    {
        if (client is not null)
        {
            _client = client;
            _ownsClient = false;
            return;
        }

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        };
        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        _ownsClient = true;
    }

    internal static string CreateSetupSecret()
    {
        Span<byte> random = stackalloc byte[32];
        RandomNumberGenerator.Fill(random);
        string encoded = Convert.ToBase64String(random)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        CryptographicOperations.ZeroMemory(random);
        return $"urlink_v1.{encoded}";
    }

    internal async Task<LinkingStart> BeginAsync(
        Uri httpsOrigin,
        string setupSecret,
        CancellationToken cancellationToken)
    {
        Uri origin = EnrollmentValidator.ValidateOrigin(httpsOrigin);
        ValidateSetupSecret(setupSecret);
        using HttpResponseMessage response = await SendAsync(
            new Uri(origin, "/remote/v1/link/begin"),
            setupSecret,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw await CreateServerExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }

        JsonElement root = await ReadObjectAsync(response, cancellationToken).ConfigureAwait(false);
        RequireExactKeys(root, "protocol", "link_code", "expires_at");
        if (root.GetProperty("protocol").GetInt32() != 1 ||
            root.GetProperty("link_code").ValueKind != JsonValueKind.String ||
            root.GetProperty("expires_at").ValueKind != JsonValueKind.String ||
            !LinkCodePattern().IsMatch(root.GetProperty("link_code").GetString() ?? string.Empty) ||
            !DateTimeOffset.TryParse(root.GetProperty("expires_at").GetString(), out DateTimeOffset expiresAt))
        {
            throw new LinkingClientException("LINK_RESPONSE_INVALID");
        }
        if (expiresAt.Offset != TimeSpan.Zero)
        {
            expiresAt = expiresAt.ToUniversalTime();
        }
        return new LinkingStart(root.GetProperty("link_code").GetString()!, expiresAt);
    }

    internal async Task<LinkingReady?> PollAsync(
        Uri httpsOrigin,
        string setupSecret,
        CancellationToken cancellationToken)
    {
        Uri origin = EnrollmentValidator.ValidateOrigin(httpsOrigin);
        ValidateSetupSecret(setupSecret);
        using HttpResponseMessage response = await SendAsync(
            new Uri(origin, "/remote/v1/link/status"),
            setupSecret,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            JsonElement pending = await ReadObjectAsync(response, cancellationToken).ConfigureAwait(false);
            RequireExactKeys(pending, "protocol", "status");
            if (pending.GetProperty("protocol").GetInt32() != 1 ||
                pending.GetProperty("status").GetString() != "pending")
            {
                throw new LinkingClientException("LINK_RESPONSE_INVALID");
            }
            return null;
        }
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw await CreateServerExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }

        JsonElement root = await ReadObjectAsync(response, cancellationToken).ConfigureAwait(false);
        RequireExactKeys(
            root,
            "protocol",
            "status",
            "device_credential",
            "agent_websocket_path");
        if (root.GetProperty("protocol").GetInt32() != 1 ||
            root.GetProperty("status").GetString() != "ready" ||
            root.GetProperty("device_credential").ValueKind != JsonValueKind.String ||
            root.GetProperty("agent_websocket_path").GetString() != EnrollmentValidator.AgentPath)
        {
            throw new LinkingClientException("LINK_RESPONSE_INVALID");
        }

        string credential = root.GetProperty("device_credential").GetString()!;
        if (!DeviceCredentialPattern().IsMatch(credential))
        {
            throw new LinkingClientException("LINK_RESPONSE_INVALID");
        }
        var socketBuilder = new UriBuilder(origin)
        {
            Scheme = "wss",
            Path = EnrollmentValidator.AgentPath,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        Uri socket = EnrollmentValidator.ValidateWebSocketUri(socketBuilder.Uri, origin);
        return new LinkingReady(credential, socket);
    }

    internal async Task CompleteAsync(
        Uri httpsOrigin,
        string setupSecret,
        CancellationToken cancellationToken)
    {
        Uri origin = EnrollmentValidator.ValidateOrigin(httpsOrigin);
        ValidateSetupSecret(setupSecret);
        using HttpResponseMessage response = await SendAsync(
            new Uri(origin, "/remote/v1/link/complete"),
            setupSecret,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            throw await CreateServerExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri endpoint,
        string setupSecret,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Linking", setupSecret);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = null;
        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LinkingClientException("LINK_NETWORK_FAILED", exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new LinkingClientException("LINK_NETWORK_FAILED", exception);
        }

        if (response.Headers.Location is not null ||
            response.Content.Headers.ContentLength > MaxResponseBytes ||
            response.Headers.CacheControl?.NoStore != true)
        {
            response.Dispose();
            throw new LinkingClientException("LINK_RESPONSE_INVALID");
        }
        return response;
    }

    private static async Task<JsonElement> ReadObjectAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        byte[] payload = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload.AsMemory(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 12,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new LinkingClientException("LINK_RESPONSE_INVALID");
            }
            RejectDuplicateProperties(document.RootElement);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new LinkingClientException("LINK_RESPONSE_INVALID", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        try
        {
            await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var output = new MemoryStream();
            byte[] buffer = new byte[4096];
            try
            {
                while (true)
                {
                    int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return output.ToArray();
                    }
                    if (output.Length + read > MaxResponseBytes)
                    {
                        throw new LinkingClientException("LINK_RESPONSE_INVALID");
                    }
                    output.Write(buffer, 0, read);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LinkingClientException("LINK_NETWORK_FAILED", exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new LinkingClientException("LINK_NETWORK_FAILED", exception);
        }
    }

    private static async Task<LinkingClientException> CreateServerExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            JsonElement root = await ReadObjectAsync(response, cancellationToken).ConfigureAwait(false);
            if (root.TryGetProperty("error", out JsonElement error) &&
                error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("code", out JsonElement code) &&
                code.ValueKind == JsonValueKind.String &&
                ServerErrorCodePattern().IsMatch(code.GetString() ?? string.Empty))
            {
                return new LinkingClientException(code.GetString()!);
            }
        }
        catch (LinkingClientException exception) when (exception.Code != "LINK_NETWORK_FAILED")
        {
        }
        return new LinkingClientException("LINK_REJECTED");
    }

    private static void RequireExactKeys(JsonElement root, params string[] expected)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!keys.Add(property.Name))
            {
                throw new LinkingClientException("LINK_RESPONSE_INVALID");
            }
        }
        if (keys.Count != expected.Length || expected.Any(key => !keys.Contains(key)))
        {
            throw new LinkingClientException("LINK_RESPONSE_INVALID");
        }
    }

    private static void RejectDuplicateProperties(JsonElement root)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!keys.Add(property.Name))
            {
                throw new LinkingClientException("LINK_RESPONSE_INVALID");
            }
        }
    }

    private static void ValidateSetupSecret(string setupSecret)
    {
        if (!SetupSecretPattern().IsMatch(setupSecret ?? string.Empty))
        {
            throw new LinkingClientException("LINK_SECRET_INVALID");
        }
    }

    [GeneratedRegex(@"\Aurlink_v1\.[A-Za-z0-9_-]{43}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SetupSecretPattern();

    [GeneratedRegex(
        @"\AULT-[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{5}-[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{5}\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex LinkCodePattern();

    [GeneratedRegex(
        @"\Aurad_v1\.[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.[A-Za-z0-9_-]{43}\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex DeviceCredentialPattern();

    [GeneratedRegex(@"\A[A-Z][A-Z0-9_]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ServerErrorCodePattern();
}

internal sealed class LinkingClientException : Exception
{
    internal LinkingClientException(string code, Exception? innerException = null)
        : base(code, innerException) => Code = code;

    internal string Code { get; }
}
