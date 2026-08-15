using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UltimateRemoteAgent.Enrollment;

internal interface IEnrollmentProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    byte[] Unprotect(ReadOnlySpan<byte> protectedData);
}

internal sealed class DpapiCurrentUserProtector : IEnrollmentProtector
{
    private static readonly byte[] OptionalEntropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("UltimateRemoteAgent/enrollment/v1\0CurrentUser"));

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        byte[] input = plaintext.ToArray();
        try
        {
            return ProtectedData.Protect(input, OptionalEntropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedData)
    {
        byte[] input = protectedData.ToArray();
        try
        {
            return ProtectedData.Unprotect(input, OptionalEntropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }
}

internal sealed class DpapiEnrollmentStore
{
    private const int MaxEnvelopeBytes = 64 * 1024;
    private readonly string _path;
    private readonly IEnrollmentProtector _protector;

    internal DpapiEnrollmentStore(string path, IEnrollmentProtector? protector = null)
    {
        _path = Path.GetFullPath(path);
        _protector = protector ?? new DpapiCurrentUserProtector();
    }

    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UltimateRemoteAgent",
        "enrollment.v1.bin");

    internal EnrollmentRecord Load()
    {
        byte[] protectedBytes;
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length is <= 0 or > MaxEnvelopeBytes)
            {
                throw new EnrollmentException("ENROLLMENT_NOT_FOUND");
            }

            protectedBytes = File.ReadAllBytes(_path);
        }
        catch (EnrollmentException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new EnrollmentException("ENROLLMENT_READ_FAILED", exception);
        }

        byte[] plaintext;
        try
        {
            plaintext = _protector.Unprotect(protectedBytes);
        }
        catch (CryptographicException exception)
        {
            throw new EnrollmentException("ENROLLMENT_DECRYPT_FAILED", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }

        try
        {
            return EnrollmentValidator.Validate(ParseEnvelope(plaintext), requireFiles: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal void Save(EnrollmentRecord record)
    {
        EnrollmentRecord validated = EnrollmentValidator.Validate(record, requireFiles: true);
        byte[] plaintext = SerializeEnvelope(validated);
        byte[]? protectedBytes = null;
        string? temporaryPath = null;
        try
        {
            protectedBytes = _protector.Protect(plaintext);
            if (protectedBytes.Length is <= 0 or > MaxEnvelopeBytes)
            {
                throw new EnrollmentException("ENROLLMENT_ENCRYPT_FAILED");
            }

            string directory = Path.GetDirectoryName(_path)
                ?? throw new EnrollmentException("ENROLLMENT_PATH_INVALID");
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
            temporaryPath = null;
        }
        catch (EnrollmentException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new EnrollmentException("ENROLLMENT_WRITE_FAILED", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static byte[] SerializeEnvelope(EnrollmentRecord record)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", record.Version);
            writer.WriteString("https_origin", record.HttpsOrigin.AbsoluteUri);
            writer.WriteString("agent_websocket_uri", record.WebSocketUri.AbsoluteUri);
            writer.WriteString("macro_root", record.MacroRoot);
            writer.WriteString("device_credential", record.DeviceCredential);
            writer.WriteEndObject();
        }

        byte[] result = buffer.ToArray();
        if (buffer.TryGetBuffer(out ArraySegment<byte> internalBuffer) && internalBuffer.Array is not null)
        {
            CryptographicOperations.ZeroMemory(
                internalBuffer.Array.AsSpan(internalBuffer.Offset, checked((int)buffer.Length)));
        }
        return result;
    }

    private static EnrollmentRecord ParseEnvelope(byte[] payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload.AsMemory(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new EnrollmentException("ENROLLMENT_FORMAT_INVALID");
            }

            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!values.TryAdd(property.Name, property.Value.Clone()))
                {
                    throw new EnrollmentException("ENROLLMENT_FORMAT_INVALID");
                }
            }

            string[] expected = ["version", "https_origin", "agent_websocket_uri", "macro_root", "device_credential"];
            if (values.Count != expected.Length || expected.Any(key => !values.ContainsKey(key)))
            {
                throw new EnrollmentException("ENROLLMENT_FORMAT_INVALID");
            }

            if (!values["version"].TryGetInt32(out int version) ||
                values["https_origin"].ValueKind != JsonValueKind.String ||
                values["agent_websocket_uri"].ValueKind != JsonValueKind.String ||
                values["macro_root"].ValueKind != JsonValueKind.String ||
                values["device_credential"].ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(values["https_origin"].GetString(), UriKind.Absolute, out Uri? origin) ||
                !Uri.TryCreate(values["agent_websocket_uri"].GetString(), UriKind.Absolute, out Uri? socket))
            {
                throw new EnrollmentException("ENROLLMENT_FORMAT_INVALID");
            }

            return new EnrollmentRecord(
                version,
                origin,
                socket,
                values["macro_root"].GetString()!,
                values["device_credential"].GetString()!);
        }
        catch (EnrollmentException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new EnrollmentException("ENROLLMENT_FORMAT_INVALID", exception);
        }
    }
}
