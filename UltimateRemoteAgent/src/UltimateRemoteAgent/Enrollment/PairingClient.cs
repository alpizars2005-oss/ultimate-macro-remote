using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltimateRemoteAgent.Enrollment;

internal sealed record PairingResult(string DeviceCredential, Uri WebSocketUri)
{
    public override string ToString() =>
        "PairingResult { DeviceCredential = [redacted], WebSocketUri = [redacted] }";
}

internal sealed partial class PairingClient : IDisposable
{
    private const int MaxResponseBytes = 64 * 1024;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    internal PairingClient(HttpClient? client = null)
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

    [GeneratedRegex(@"\Aurpair_v1\.[A-Za-z0-9_-]{43}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PairingTicketPattern();

    internal async Task<PairingResult> RedeemAsync(
        Uri httpsOrigin,
        string pairingTicket,
        CancellationToken cancellationToken)
    {
        Uri origin = EnrollmentValidator.ValidateOrigin(httpsOrigin);
        if (!PairingTicketPattern().IsMatch(pairingTicket))
        {
            throw new PairingClientException("PAIRING_TICKET_INVALID");
        }

        var endpoint = new Uri(origin, "/remote/v1/pair");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Pairing", pairingTicket);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw new PairingClientException("PAIRING_REJECTED");
        }

        if (response.Headers.Location is not null || response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new PairingClientException("PAIRING_RESPONSE_INVALID");
        }

        if (response.Headers.CacheControl?.NoStore != true)
        {
            throw new PairingClientException("PAIRING_RESPONSE_INVALID");
        }

        byte[] payload = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        try
        {
            return ParseResponse(payload, origin);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(payload);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > MaxResponseBytes)
            {
                throw new PairingClientException("PAIRING_RESPONSE_INVALID");
            }

            output.Write(buffer, 0, read);
        }
    }

    private static PairingResult ParseResponse(byte[] payload, Uri origin)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload.AsMemory(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new PairingClientException("PAIRING_RESPONSE_INVALID");
            }

            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!values.TryAdd(property.Name, property.Value.Clone()))
                {
                    throw new PairingClientException("PAIRING_RESPONSE_INVALID");
                }
            }

            string[] expected = ["protocol", "device_credential", "agent_websocket_path"];
            if (values.Count != expected.Length || expected.Any(key => !values.ContainsKey(key)) ||
                !values["protocol"].TryGetInt32(out int protocol) || protocol != 1 ||
                values["device_credential"].ValueKind != JsonValueKind.String ||
                values["agent_websocket_path"].ValueKind != JsonValueKind.String ||
                values["agent_websocket_path"].GetString() != EnrollmentValidator.AgentPath)
            {
                throw new PairingClientException("PAIRING_RESPONSE_INVALID");
            }

            string credential = values["device_credential"].GetString()!;
            var webSocketBuilder = new UriBuilder(origin)
            {
                Scheme = "wss",
                Path = EnrollmentValidator.AgentPath,
                Query = string.Empty,
                Fragment = string.Empty,
            };
            var candidate = new EnrollmentRecord(
                EnrollmentRecord.CurrentVersion,
                origin,
                webSocketBuilder.Uri,
                Path.GetPathRoot(Environment.SystemDirectory)!,
                credential);
            Uri socket = EnrollmentValidator.ValidateWebSocketUri(candidate.WebSocketUri, origin);
            if (!DeviceCredentialPattern().IsMatch(credential))
            {
                throw new PairingClientException("PAIRING_RESPONSE_INVALID");
            }

            return new PairingResult(credential, socket);
        }
        catch (PairingClientException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new PairingClientException("PAIRING_RESPONSE_INVALID", exception);
        }
    }

    [GeneratedRegex(
        @"\Aurad_v1\.[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.[A-Za-z0-9_-]{43}\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex DeviceCredentialPattern();
}

internal sealed class PairingClientException : Exception
{
    internal PairingClientException(string code, Exception? innerException = null)
        : base(code, innerException) => Code = code;

    internal string Code { get; }
}
