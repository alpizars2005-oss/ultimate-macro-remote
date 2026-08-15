using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using UltimateRemoteAgent.Enrollment;

namespace UltimateRemoteAgent.Tests;

[TestClass]
public sealed class EnrollmentTests
{
    private const string Credential =
        "urad_v1.11111111-1111-4111-8111-111111111111.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Ticket =
        "urpair_v1.BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [TestMethod]
    public void EnrollmentStorePersistsOnlyProtectedBytesAndRedactsRepresentations()
    {
        using var installation = new TemporaryMacroInstallation();
        string enrollmentPath = Path.Combine(installation.ParentDirectory, "enrollment.bin");
        var record = CreateRecord(installation.Root);
        var store = new DpapiEnrollmentStore(enrollmentPath, new XorProtector());

        store.Save(record);
        EnrollmentRecord loaded = store.Load();
        byte[] stored = File.ReadAllBytes(enrollmentPath);
        string storedText = Encoding.UTF8.GetString(stored);

        Assert.AreEqual(record, loaded);
        Assert.IsFalse(storedText.Contains(Credential, StringComparison.Ordinal));
        Assert.IsFalse(storedText.Contains(installation.Root, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(record.ToString().Contains(Credential, StringComparison.Ordinal));
        Assert.IsFalse(record.ToString().Contains(installation.Root, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void DpapiCurrentUserRoundTripRejectsTampering()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("DPAPI is Windows-only.");
        }

        using var installation = new TemporaryMacroInstallation();
        string enrollmentPath = Path.Combine(installation.ParentDirectory, "dpapi.bin");
        var store = new DpapiEnrollmentStore(enrollmentPath);
        EnrollmentRecord record = CreateRecord(installation.Root);
        store.Save(record);

        Assert.AreEqual(record, store.Load());
        byte[] protectedBytes = File.ReadAllBytes(enrollmentPath);
        protectedBytes[^1] ^= 0x7f;
        File.WriteAllBytes(enrollmentPath, protectedBytes);

        EnrollmentException exception = Assert.ThrowsExactly<EnrollmentException>(() => store.Load());
        Assert.AreEqual("ENROLLMENT_DECRYPT_FAILED", exception.Code);
    }

    [TestMethod]
    public async Task PairingUsesEmptyHttpsPostAndAuthorizationHeaderOnly()
    {
        var handler = new RecordingHandler(CreatePairingResponse());
        using var http = new HttpClient(handler);
        using var client = new PairingClient(http);

        PairingResult result = await client.RedeemAsync(
            new Uri("https://remote.example:9443/"),
            Ticket,
            CancellationToken.None);

        Assert.AreEqual(HttpMethod.Post, handler.Method);
        Assert.AreEqual("https://remote.example:9443/remote/v1/pair", handler.RequestUri?.AbsoluteUri);
        Assert.AreEqual("Pairing", handler.Authorization?.Scheme);
        Assert.AreEqual(Ticket, handler.Authorization?.Parameter);
        Assert.IsFalse(handler.HadContent);
        Assert.IsTrue(string.IsNullOrEmpty(handler.RequestUri?.Query));
        Assert.AreEqual(Credential, result.DeviceCredential);
        Assert.AreEqual("wss://remote.example:9443/remote/v1/agent", result.WebSocketUri.AbsoluteUri);
        Assert.IsFalse(result.ToString().Contains(Credential, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PairingRejectsRedirectsUnknownFieldsAndMissingNoStore()
    {
        using var redirectHttp = new HttpClient(
            new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Redirect)));
        using var redirectClient = new PairingClient(redirectHttp);
        PairingClientException redirect = await Assert.ThrowsExactlyAsync<PairingClientException>(
            () => redirectClient.RedeemAsync(new Uri("https://remote.example/"), Ticket, CancellationToken.None));
        Assert.AreEqual("PAIRING_REJECTED", redirect.Code);

        HttpResponseMessage extraResponse = CreatePairingResponse(extraJson: ",\"owner\":\"123\"");
        using var extraHttp = new HttpClient(new RecordingHandler(extraResponse));
        using var extraClient = new PairingClient(extraHttp);
        PairingClientException extra = await Assert.ThrowsExactlyAsync<PairingClientException>(
            () => extraClient.RedeemAsync(new Uri("https://remote.example/"), Ticket, CancellationToken.None));
        Assert.AreEqual("PAIRING_RESPONSE_INVALID", extra.Code);

        HttpResponseMessage cacheable = CreatePairingResponse();
        cacheable.Headers.CacheControl = null;
        using var cacheableHttp = new HttpClient(new RecordingHandler(cacheable));
        using var cacheableClient = new PairingClient(cacheableHttp);
        PairingClientException noStore = await Assert.ThrowsExactlyAsync<PairingClientException>(
            () => cacheableClient.RedeemAsync(new Uri("https://remote.example/"), Ticket, CancellationToken.None));
        Assert.AreEqual("PAIRING_RESPONSE_INVALID", noStore.Code);
    }

    [TestMethod]
    public async Task PairingRejectsPlaintextOriginAndWrongCredentialTypes()
    {
        using var http = new HttpClient(new RecordingHandler(CreatePairingResponse()));
        using var client = new PairingClient(http);

        EnrollmentException origin = await Assert.ThrowsExactlyAsync<EnrollmentException>(
            () => client.RedeemAsync(new Uri("http://remote.example/"), Ticket, CancellationToken.None));
        Assert.AreEqual("HTTPS_ORIGIN_INVALID", origin.Code);

        PairingClientException credential = await Assert.ThrowsExactlyAsync<PairingClientException>(
            () => client.RedeemAsync(new Uri("https://remote.example/"), Credential, CancellationToken.None));
        Assert.AreEqual("PAIRING_TICKET_INVALID", credential.Code);
    }

    private static EnrollmentRecord CreateRecord(string macroRoot) => new(
        EnrollmentRecord.CurrentVersion,
        new Uri("https://remote.example/"),
        new Uri("wss://remote.example/remote/v1/agent"),
        macroRoot,
        Credential);

    private static HttpResponseMessage CreatePairingResponse(string extraJson = "")
    {
        string json =
            "{\"protocol\":1,\"device_credential\":\"" + Credential +
            "\",\"agent_websocket_path\":\"/remote/v1/agent\"" + extraJson + "}";
        var response = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return response;
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        internal HttpMethod? Method { get; private set; }

        internal Uri? RequestUri { get; private set; }

        internal AuthenticationHeaderValue? Authorization { get; private set; }

        internal bool HadContent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            HadContent = request.Content is not null;
            return Task.FromResult(response);
        }
    }

    private sealed class XorProtector : IEnrollmentProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => Transform(plaintext);

        public byte[] Unprotect(ReadOnlySpan<byte> protectedData) => Transform(protectedData);

        private static byte[] Transform(ReadOnlySpan<byte> input)
        {
            byte[] output = input.ToArray();
            for (int index = 0; index < output.Length; index++)
            {
                output[index] ^= 0x5a;
            }
            return output;
        }
    }

    private sealed class TemporaryMacroInstallation : IDisposable
    {
        internal TemporaryMacroInstallation()
        {
            ParentDirectory = Path.Combine(
                Path.GetTempPath(),
                $"UltimateRemoteAgent.EnrollmentTests.{Guid.NewGuid():N}");
            Root = Path.Combine(ParentDirectory, "Macro");
            Directory.CreateDirectory(Path.Combine(Root, "submacros"));
            Directory.CreateDirectory(Path.Combine(Root, "Resources", "Strats"));
            File.WriteAllText(Path.Combine(Root, "Main_Remote.ahk"), "; test");
            File.WriteAllBytes(Path.Combine(Root, "submacros", "AutoHotkey64.exe"), [0x4d, 0x5a]);
        }

        internal string ParentDirectory { get; }

        internal string Root { get; }

        public void Dispose() => Directory.Delete(ParentDirectory, recursive: true);
    }
}
