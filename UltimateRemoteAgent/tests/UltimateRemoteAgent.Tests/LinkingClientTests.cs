using System.Net;
using System.Net.Http.Headers;
using System.Text;
using UltimateRemoteAgent.Enrollment;

namespace UltimateRemoteAgent.Tests;

[TestClass]
public sealed class LinkingClientTests
{
    private const string SetupSecret =
        "urlink_v1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Credential =
        "urad_v1.11111111-1111-4111-8111-111111111111.BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [TestMethod]
    public async Task BeginUsesEmptyAuthenticatedHttpsPostAndReturnsOnlyShortCode()
    {
        using var response = JsonResponse(
            HttpStatusCode.Created,
            "{\"protocol\":1,\"link_code\":\"ULT-23456-ABCDE\",\"expires_at\":\"2026-09-03T00:10:00.000Z\"}");
        var handler = new QueueHandler(response);
        using var http = new HttpClient(handler);
        using var client = new LinkingClient(http);

        LinkingStart result = await client.BeginAsync(
            new Uri("https://remote.example:9443/"),
            SetupSecret,
            CancellationToken.None);

        Assert.AreEqual(HttpMethod.Post, handler.LastMethod);
        Assert.AreEqual(
            "https://remote.example:9443/remote/v1/link/begin",
            handler.LastUri?.AbsoluteUri);
        Assert.AreEqual("Linking", handler.LastAuthorization?.Scheme);
        Assert.AreEqual(SetupSecret, handler.LastAuthorization?.Parameter);
        Assert.IsFalse(handler.LastHadContent);
        Assert.AreEqual("ULT-23456-ABCDE", result.Code);
        Assert.AreEqual(TimeSpan.Zero, result.ExpiresAt.Offset);
    }

    [TestMethod]
    public async Task PollHandlesPendingThenReadyAndNeverAcceptsAClientPath()
    {
        using var pending = JsonResponse(
            HttpStatusCode.Accepted,
            "{\"protocol\":1,\"status\":\"pending\"}");
        using var ready = JsonResponse(
            HttpStatusCode.Created,
            "{\"protocol\":1,\"status\":\"ready\",\"device_credential\":\"" +
            Credential +
            "\",\"agent_websocket_path\":\"/remote/v1/agent\"}");
        var handler = new QueueHandler(pending, ready);
        using var http = new HttpClient(handler);
        using var client = new LinkingClient(http);
        var origin = new Uri("https://remote.example/");

        Assert.IsNull(await client.PollAsync(origin, SetupSecret, CancellationToken.None));
        LinkingReady result = await client.PollAsync(
            origin,
            SetupSecret,
            CancellationToken.None)
            ?? throw new AssertFailedException("Ready response was not returned.");

        Assert.AreEqual(Credential, result.DeviceCredential);
        Assert.AreEqual(
            "wss://remote.example/remote/v1/agent",
            result.WebSocketUri.AbsoluteUri);
        Assert.IsFalse(result.ToString().Contains(Credential, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CompleteUsesTheSameHeaderOnlySecretAndNoRequestBody()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.NoContent);
        response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        var handler = new QueueHandler(response);
        using var http = new HttpClient(handler);
        using var client = new LinkingClient(http);

        await client.CompleteAsync(
            new Uri("https://remote.example/"),
            SetupSecret,
            CancellationToken.None);

        Assert.AreEqual(
            "https://remote.example/remote/v1/link/complete",
            handler.LastUri?.AbsoluteUri);
        Assert.AreEqual("Linking", handler.LastAuthorization?.Scheme);
        Assert.AreEqual(SetupSecret, handler.LastAuthorization?.Parameter);
        Assert.IsFalse(handler.LastHadContent);
    }

    [TestMethod]
    public void SetupSecretIsHighEntropyAndNeverUsesTheShortDisplayCodeAsCredential()
    {
        string first = LinkingClient.CreateSetupSecret();
        string second = LinkingClient.CreateSetupSecret();

        StringAssert.StartsWith(first, "urlink_v1.");
        StringAssert.StartsWith(second, "urlink_v1.");
        Assert.AreEqual(53, first.Length);
        Assert.AreNotEqual(first, second);
        Assert.IsFalse(first.Contains("ULT-", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task BeginRejectsWrongOrAmbiguousDisplayCodeAlphabet()
    {
        foreach (string code in new[]
        {
            "ULT-23450-ABCDE",
            "ULT-23456-ABCDI",
            "ULT-23456-ABCDO",
            "ULT-23456-ABCD1",
        })
        {
            using var response = JsonResponse(
                HttpStatusCode.Created,
                "{\"protocol\":1,\"link_code\":\"" + code +
                "\",\"expires_at\":\"2026-09-03T00:10:00.000Z\"}");
            using var http = new HttpClient(new QueueHandler(response));
            using var client = new LinkingClient(http);

            LinkingClientException exception = await Assert.ThrowsExactlyAsync<LinkingClientException>(
                () => client.BeginAsync(
                    new Uri("https://remote.example/"),
                    SetupSecret,
                    CancellationToken.None));
            Assert.AreEqual("LINK_RESPONSE_INVALID", exception.Code);
        }
    }

    [TestMethod]
    public async Task ResponseMustBeNoStoreAndSchemaClosed()
    {
        using var cacheable = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(
                "{\"protocol\":1,\"link_code\":\"ULT-23456-ABCDE\",\"expires_at\":\"2026-09-03T00:10:00.000Z\"}",
                Encoding.UTF8,
                "application/json"),
        };
        using var http = new HttpClient(new QueueHandler(cacheable));
        using var client = new LinkingClient(http);

        LinkingClientException exception = await Assert.ThrowsExactlyAsync<LinkingClientException>(
            () => client.BeginAsync(
                new Uri("https://remote.example/"),
                SetupSecret,
                CancellationToken.None));
        Assert.AreEqual("LINK_RESPONSE_INVALID", exception.Code);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return response;
    }

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        internal HttpMethod? LastMethod { get; private set; }
        internal Uri? LastUri { get; private set; }
        internal AuthenticationHeaderValue? LastAuthorization { get; private set; }
        internal bool LastHadContent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization;
            LastHadContent = request.Content is not null;
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No queued response.");
            }
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
