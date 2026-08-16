using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltimateRemoteAgent.Enrollment;

internal sealed record OnboardingStart(Uri AuthorizationUri, DateTimeOffset ExpiresAt);
internal sealed record OnboardingReady(string DeviceCredential, Uri WebSocketUri)
{
    public override string ToString() =>
        "OnboardingReady { DeviceCredential = [redacted], WebSocketUri = [redacted] }";
}

internal sealed partial class OnboardingClient : IDisposable
{
    private const int MaxResponseBytes = 64 * 1024;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    internal OnboardingClient(HttpClient? client = null)
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
        return $"uron_v1.{encoded}";
    }

    internal async Task<OnboardingStart> BeginAsync(
        Uri httpsOrigin,
        string setupSecret,
        CancellationToken cancellationToken)
    {
        Uri origin = EnrollmentValidator.ValidateOrigin(httpsOrigin);
        ValidateSetupSecret(setupSecret);
        using HttpResponseMessage response = await SendAsync(
            new Uri(origin, "/remote/v1/onboarding/begin"),
            setupSecret,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw await CreateServerExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }

        JsonElement root = await ReadObjectAsync(response, cancellationToken).ConfigureAwait(false);
        RequireExactKeys(root, "protocol", "authorization_url", "expires_at");
        if (root.GetProperty("protocol").GetInt32() != 1 ||
            root.GetProperty("authorization_url").ValueKind != JsonValueKind.String ||
            root.GetProperty("expires_at").ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(root.GetProperty("authorization_url").GetString(), UriKind.Absolute, out Uri? authorizationUri) ||
            !DateTimeOffset.TryParse(root.GetProperty("expires_at").GetString(), out DateTimeOffset expiresAt))
        {
            throw new OnboardingClientException("ONBOARDING_RESPONSE_INVALID");
        }
        ValidateDiscordAuthorizationUri(authorizationUri);
        if (expiresAt.Offset != TimeSpan.Zero)
        {
            expiresAt = expiresAt.ToUniversalTime();
        }
        return new OnboardingStart(authorizationUri, expiresAt);
    }

    internal async Task<OnboardingReady?> PollAsync(
        Uri httpsOrigin,
        string setupSecret,
        CancellationToken cancellationToken)
    {
        Uri origin = EnrollmentValidator.ValidateOrigin(httpsOrigin);
        ValidateSetupSecret(setupSecret);
        using HttpResponseMessage response = await SendAsync(
            new Uri(origin, "/remote/v1/onboarding/status"),
            setupSecret,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            JsonElement pending = await ReadObjectAsync(response, cancellationToken).ConfigureAwait(false);
            RequireExactKeys(pending, "protocol", "status");
            if (pending.GetProperty("protocol").GetInt32() != 1 ||
                pending.GetProperty("status").GetString() != "pending")
            {
                throw new OnboardingClientException("ONBOARDING_RESPONSE_INVALID");
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
            throw new OnboardingClientException("ONBOARDING_RESPONSE_INVALID");
        }

        string credential = root.GetProperty("device_credential").GetString()!;
        if (!DeviceCredentialPattern().IsMatch(credential))
        {
            throw new OnboardingClientException("ONBOARDING_RESPONSE_INVALID");
        }
        var socketBuilder = new UriBuilder(origin)
        {
            Scheme = "wss",
            Path = EnrollmentValidator.AgentPath,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        Uri socket = EnrollmentValidator.ValidateWebSocketUri(socketBuilder.Uri, origin);
        return new OnboardingReady(credential, socket);
    }

    internal async Task CompleteAsync(
        Uri httpsOrigin,
        string setupSecret,
        CancellationToken cancellationToken)
    {
        Uri origin = EnrollmentValidator.ValidateOrigin(httpsOrigin);
        ValidateSetupSecret(setupSecret);
        using HttpResponseMessage response = await SendAsync(
            new Uri(origin, "/remote/v1/onboarding/complete"),
            setupSecret,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            throw await CreateServerExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static void ValidateDiscordAuthorizationUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.IdnHost, "discord.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.Equals(uri.AbsolutePath, "/oauth2/authorize", StringComparison.Ordinal) ||
            string.IsNullOrEmpty(uri.Query))
        {
            throw new OnboardingClientException("OAUTH_AUTHORIZATION_URI_INVALID");
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
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Onboarding", setupSecret);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = null;
        try
        {
            HttpResponseMessage response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            request.Dispose();
            if (response.Headers.Location is not null ||
                response.Content.Headers.ContentLength > MaxResponseBytes ||
                response.Headers.CacheControl?.NoStore != true)
            {
                response.Dispose();
                throw new OnboardingClientException("ONBOARDING_RESPONSE_INVALID");
            }
            return response;
        }
        catch
        {
            request.Dispose();
            throw;
        }
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
                throw new OnboardingClientException("ONBOARDING_RESPONSE_INVALID");
            }
            RejectDuplicateProperties(document.RootElement);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new OnboardingClientException("ONBOARDING_RESPONSE_INVALID", exception);
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
                    throw new OnboardingClientException("ONBOARDING_RESPONSE_INVALID");
                }
                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static async Task<OnboardingClientException> CreateServerExceptionAsync(
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
                return new OnboardingClientException(code.GetString()!);
            }
        }
        catch (OnboardingClientException)
        {
        }
        return new OnboardingClientException("ONBOARDING_REJECTED");
    }

    private static void RequireExactKeys(JsonElement root, params string[] expected)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!keys.Add(property.Name))
            {
                throw new OnboardingClientException("ONBOARDING_RESPONSE_INVALID");
            }
        }
        if (keys.Count != expected.Length || expected.Any(key => !keys.Contains(key)))
        {
            throw new OnboardingClientException("ONBOARDING_RESPONSE_INVALID");
        }
    }

    private static void RejectDuplicateProperties(JsonElement root)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!keys.Add(property.Name))
            {
                throw new OnboardingClientException("ONBOARDING_RESPONSE_INVALID");
            }
        }
    }

    private static void ValidateSetupSecret(string setupSecret)
    {
        if (!SetupSecretPattern().IsMatch(setupSecret ?? string.Empty))
        {
            throw new OnboardingClientException("ONBOARDING_SECRET_INVALID");
        }
    }

    [GeneratedRegex(@"\Auron_v1\.[A-Za-z0-9_-]{43}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SetupSecretPattern();

    [GeneratedRegex(
        @"\Aurad_v1\.[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.[A-Za-z0-9_-]{43}\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex DeviceCredentialPattern();

    [GeneratedRegex(@"\A[A-Z][A-Z0-9_]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ServerErrorCodePattern();
}

internal sealed class OnboardingClientException : Exception
{
    internal OnboardingClientException(string code, Exception? innerException = null)
        : base(code, innerException) => Code = code;

    internal string Code { get; }
}
