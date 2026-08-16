using UltimateRemoteAgent.Enrollment;

namespace UltimateRemoteAgent.Tests;

[TestClass]
public sealed class EnrollmentRebindTests
{
    private const string Credential =
        "urad_v1.11111111-1111-4111-8111-111111111111.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public void BootstrapCanRebindProtectedEnrollmentAfterMacroFolderMoves()
    {
        string parent = Path.Combine(
            Path.GetTempPath(),
            $"UltimateRemoteAgent.RebindTests.{Guid.NewGuid():N}");
        string originalRoot = Path.Combine(parent, "Macro-Old");
        string movedRoot = Path.Combine(parent, "Macro-New");
        string enrollmentPath = Path.Combine(parent, "enrollment.bin");
        try
        {
            CreateInstallation(originalRoot);
            var store = new DpapiEnrollmentStore(enrollmentPath, new XorProtector());
            var original = new EnrollmentRecord(
                EnrollmentRecord.CurrentVersion,
                new Uri("https://remote.example/"),
                new Uri("wss://remote.example/remote/v1/agent"),
                originalRoot,
                Credential);
            store.Save(original);

            Directory.Move(originalRoot, movedRoot);

            EnrollmentException liveLoad = Assert.ThrowsExactly<EnrollmentException>(
                () => store.Load(requireFiles: true));
            Assert.AreEqual("MACRO_INSTALLATION_INVALID", liveLoad.Code);

            EnrollmentRecord protectedEnrollment = store.Load();
            Assert.AreEqual(Credential, protectedEnrollment.DeviceCredential);
            Assert.AreEqual(originalRoot, protectedEnrollment.MacroRoot);

            EnrollmentRecord rebound = protectedEnrollment with { MacroRoot = movedRoot };
            store.Save(rebound);

            Assert.AreEqual(rebound, store.Load(requireFiles: true));
        }
        finally
        {
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
    }

    private static void CreateInstallation(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "submacros"));
        Directory.CreateDirectory(Path.Combine(root, "Resources", "Strats"));
        File.WriteAllText(Path.Combine(root, "Main_Remote.ahk"), "; test");
        File.WriteAllBytes(
            Path.Combine(root, "submacros", "AutoHotkey64.exe"),
            [0x4d, 0x5a]);
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
                output[index] ^= 0x6d;
            }
            return output;
        }
    }
}
