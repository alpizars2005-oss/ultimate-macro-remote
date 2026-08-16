using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using UltimateRemoteAgent.Enrollment;

namespace UltimateRemoteAgent.Runtime;

internal sealed record RemotePreferences(
    int Version,
    string TermsVersion,
    bool RemoteEnabled,
    bool StartWithWindows,
    DateTimeOffset AcceptedAtUtc)
{
    internal const int CurrentVersion = 1;
    internal const string CurrentTermsVersion = "preview-2026-08-15";
}

internal sealed class RemotePreferencesStore
{
    private const int MaximumBytes = 16 * 1024;
    private readonly string _path;

    internal RemotePreferencesStore(string? path = null) =>
        _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UltimateRemoteAgent",
            "preferences.v1.json"));

    internal RemotePreferences? TryLoad()
    {
        if (!File.Exists(_path))
        {
            return null;
        }
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length is <= 0 or > MaximumBytes)
            {
                throw new AgentRuntimeException("PREFERENCES_INVALID");
            }
            byte[] payload = File.ReadAllBytes(_path);
            RemotePreferences? preferences = JsonSerializer.Deserialize<RemotePreferences>(
                payload,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    AllowTrailingCommas = false,
                    ReadCommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
            if (preferences is null ||
                preferences.Version != RemotePreferences.CurrentVersion ||
                string.IsNullOrWhiteSpace(preferences.TermsVersion) ||
                preferences.AcceptedAtUtc.Offset != TimeSpan.Zero)
            {
                throw new AgentRuntimeException("PREFERENCES_INVALID");
            }
            return preferences;
        }
        catch (AgentRuntimeException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new AgentRuntimeException("PREFERENCES_READ_FAILED", exception);
        }
    }

    internal void Save(RemotePreferences preferences)
    {
        if (preferences.Version != RemotePreferences.CurrentVersion ||
            preferences.AcceptedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new AgentRuntimeException("PREFERENCES_INVALID");
        }
        try
        {
            string directory = Path.GetDirectoryName(_path)
                ?? throw new AgentRuntimeException("PREFERENCES_PATH_INVALID");
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(
                directory,
                $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                preferences,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    WriteIndented = false,
                });
            if (payload.Length > MaximumBytes)
            {
                throw new AgentRuntimeException("PREFERENCES_INVALID");
            }
            try
            {
                using (var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(payload);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
                catch (IOException)
                {
                }
            }
        }
        catch (AgentRuntimeException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new AgentRuntimeException("PREFERENCES_WRITE_FAILED", exception);
        }
    }
}

internal sealed record ConsentDecision(bool Accepted, bool StartWithWindows);

internal static class RemoteConsentDialog
{
    private const string ConsentText =
        "Ultimate Macro Remote is optional. If enabled, its Agent may start with your Windows account, " +
        "connect outbound to the Remote service, read Ultimate Macro status and installed strategy names, " +
        "and execute only the supported macro commands you request (such as start, safe stop, and safe switch).\r\n\r\n" +
        "The Agent does not provide arbitrary remote desktop access, a shell, PowerShell/CMD execution, " +
        "or general access to personal files. Safe stop/switch requests wait for Ultimate Macro's validated " +
        "between-match boundary. The Agent starting with Windows does not start Roblox or a strategy by itself.\r\n\r\n" +
        "This development preview uses provisional Terms and Privacy text until the project owner publishes " +
        "formal policies. You can decline Remote and continue using Ultimate Macro normally.";

    internal static ConsentDecision Show()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var form = new Form
        {
            Text = "Ultimate Macro Remote",
            Width = 620,
            Height = 560,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            TopMost = true,
        };
        var title = new Label
        {
            Text = "Enable Remote Control?",
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 16, FontStyle.Bold),
            Left = 24,
            Top = 20,
        };
        var terms = new TextBox
        {
            Text = ConsentText,
            ReadOnly = true,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Left = 24,
            Top = 62,
            Width = 552,
            Height = 300,
            TabStop = false,
        };
        var agree = new CheckBox
        {
            Text = "I agree to the Remote Terms and Privacy Notice above.",
            AutoSize = true,
            Left = 24,
            Top = 380,
        };
        var startup = new CheckBox
        {
            Text = "Start the Remote Agent with Windows (recommended)",
            AutoSize = true,
            Checked = true,
            Left = 24,
            Top = 412,
        };
        var connect = new Button
        {
            Text = "Connect Discord",
            Enabled = false,
            Width = 160,
            Height = 38,
            Left = 416,
            Top = 458,
            DialogResult = DialogResult.OK,
        };
        var decline = new Button
        {
            Text = "Not now",
            Width = 110,
            Height = 38,
            Left = 294,
            Top = 458,
            DialogResult = DialogResult.Cancel,
        };
        agree.CheckedChanged += (_, _) => connect.Enabled = agree.Checked;
        form.Controls.AddRange([title, terms, agree, startup, connect, decline]);
        form.AcceptButton = connect;
        form.CancelButton = decline;

        DialogResult result = form.ShowDialog();
        return new ConsentDecision(
            result == DialogResult.OK && agree.Checked,
            startup.Checked);
    }
}

internal static class RemoteStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "UltimateMacroRemoteAgent";

    internal static void Apply(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new AgentRuntimeException("AUTOSTART_REGISTRY_UNAVAILABLE");
            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            string executable = Environment.ProcessPath
                ?? throw new AgentRuntimeException("AGENT_EXECUTABLE_UNAVAILABLE");
            executable = Path.GetFullPath(executable);
            if (!File.Exists(executable) || executable.IndexOfAny(['\r', '\n', '"']) >= 0)
            {
                throw new AgentRuntimeException("AGENT_EXECUTABLE_UNAVAILABLE");
            }
            key.SetValue(
                ValueName,
                $"\"{executable}\" run-background",
                RegistryValueKind.String);
        }
        catch (AgentRuntimeException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            throw new AgentRuntimeException("AUTOSTART_REGISTRATION_FAILED", exception);
        }
    }
}

internal static class RemoteServiceOrigin
{
    private const int MaximumBytes = 2048;

    internal static Uri ReadFromMacroRoot(string macroRoot)
    {
        string path = Path.Combine(macroRoot, "remote_service.url");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > MaximumBytes ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new AgentRuntimeException("REMOTE_SERVICE_CONFIG_MISSING");
            }
            string text = File.ReadAllText(path).Trim();
            if (text.Contains('\r', StringComparison.Ordinal) ||
                text.Contains('\n', StringComparison.Ordinal) ||
                !Uri.TryCreate(text, UriKind.Absolute, out Uri? origin))
            {
                throw new AgentRuntimeException("REMOTE_SERVICE_CONFIG_INVALID");
            }
            return EnrollmentValidator.ValidateOrigin(origin);
        }
        catch (AgentRuntimeException)
        {
            throw;
        }
        catch (EnrollmentException exception)
        {
            throw new AgentRuntimeException("REMOTE_SERVICE_CONFIG_INVALID", exception);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new AgentRuntimeException("REMOTE_SERVICE_CONFIG_READ_FAILED", exception);
        }
    }
}

internal static class RemoteBootstrap
{
    internal static async Task<int> RunAsync(
        string macroRootText,
        CancellationToken cancellationToken)
    {
        string macroRoot = EnrollmentValidator.ValidateMacroRoot(macroRootText, requireFiles: true);
        Uri origin = RemoteServiceOrigin.ReadFromMacroRoot(macroRoot);
        var preferencesStore = new RemotePreferencesStore();
        RemotePreferences? preferences = preferencesStore.TryLoad();
        if (preferences is null ||
            !string.Equals(
                preferences.TermsVersion,
                RemotePreferences.CurrentTermsVersion,
                StringComparison.Ordinal))
        {
            ConsentDecision decision = RemoteConsentDialog.Show();
            preferences = new RemotePreferences(
                RemotePreferences.CurrentVersion,
                RemotePreferences.CurrentTermsVersion,
                decision.Accepted,
                decision.Accepted && decision.StartWithWindows,
                DateTimeOffset.UtcNow);
            preferencesStore.Save(preferences);
        }

        if (!preferences.RemoteEnabled)
        {
            RemoteStartupRegistration.Apply(enabled: false);
            return 0;
        }

        var enrollmentStore = new DpapiEnrollmentStore(DpapiEnrollmentStore.DefaultPath);
        EnrollmentRecord? enrollment = TryLoadEnrollment(enrollmentStore);
        if (enrollment is null ||
            !string.Equals(enrollment.MacroRoot, macroRoot, StringComparison.OrdinalIgnoreCase) ||
            !SameOrigin(enrollment.HttpsOrigin, origin))
        {
            enrollment = await EnrollWithDiscordAsync(
                origin,
                macroRoot,
                enrollmentStore,
                cancellationToken).ConfigureAwait(false);
        }

        RemoteStartupRegistration.Apply(preferences.StartWithWindows);
        StartBackgroundAgent();
        SafeLog.Info("REMOTE_BOOTSTRAP_COMPLETE");
        return 0;
    }

    private static EnrollmentRecord? TryLoadEnrollment(DpapiEnrollmentStore store)
    {
        try
        {
            return store.Load();
        }
        catch (EnrollmentException exception) when (exception.Code == "ENROLLMENT_NOT_FOUND")
        {
            return null;
        }
    }

    private static async Task<EnrollmentRecord> EnrollWithDiscordAsync(
        Uri origin,
        string macroRoot,
        DpapiEnrollmentStore store,
        CancellationToken cancellationToken)
    {
        string setupSecret = OnboardingClient.CreateSetupSecret();
        try
        {
            using var client = new OnboardingClient();
            OnboardingStart start = await client.BeginAsync(
                origin,
                setupSecret,
                cancellationToken).ConfigureAwait(false);
            OpenDiscordAuthorization(start.AuthorizationUri);

            while (DateTimeOffset.UtcNow < start.ExpiresAt)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OnboardingReady? ready = await client.PollAsync(
                    origin,
                    setupSecret,
                    cancellationToken).ConfigureAwait(false);
                if (ready is not null)
                {
                    var enrollment = new EnrollmentRecord(
                        EnrollmentRecord.CurrentVersion,
                        origin,
                        ready.WebSocketUri,
                        macroRoot,
                        ready.DeviceCredential);
                    store.Save(enrollment);

                    // The server revokes unacknowledged credentials after setup expiry,
                    // so retry completion while this one-time setup secret is valid.
                    await CompleteWithRetryAsync(
                        client,
                        origin,
                        setupSecret,
                        start.ExpiresAt,
                        cancellationToken).ConfigureAwait(false);
                    return enrollment;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                    .ConfigureAwait(false);
            }
            throw new AgentRuntimeException("ONBOARDING_TIMEOUT");
        }
        catch (OnboardingClientException exception)
        {
            throw new AgentRuntimeException(exception.Code, exception);
        }
        finally
        {
            setupSecret = string.Empty;
        }
    }

    private static async Task CompleteWithRetryAsync(
        OnboardingClient client,
        Uri origin,
        string setupSecret,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        while (DateTimeOffset.UtcNow < expiresAt)
        {
            try
            {
                await client.CompleteAsync(origin, setupSecret, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (OnboardingClientException exception) when (
                exception.Code is "ONBOARDING_REJECTED" or "ONBOARDING_NETWORK_FAILED")
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        throw new AgentRuntimeException("ONBOARDING_CONFIRMATION_FAILED");
    }

    private static void OpenDiscordAuthorization(Uri uri)
    {
        OnboardingClient.ValidateDiscordAuthorizationUri(uri);
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true,
            });
            if (process is null)
            {
                throw new AgentRuntimeException("OAUTH_BROWSER_OPEN_FAILED");
            }
        }
        catch (AgentRuntimeException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new AgentRuntimeException("OAUTH_BROWSER_OPEN_FAILED", exception);
        }
    }

    internal static void StartBackgroundAgent()
    {
        string executable = Environment.ProcessPath
            ?? throw new AgentRuntimeException("AGENT_EXECUTABLE_UNAVAILABLE");
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable))
        {
            throw new AgentRuntimeException("AGENT_EXECUTABLE_UNAVAILABLE");
        }
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                ArgumentList = { "run-background" },
            });
            if (process is null)
            {
                throw new AgentRuntimeException("AGENT_BACKGROUND_START_FAILED");
            }
        }
        catch (AgentRuntimeException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new AgentRuntimeException("AGENT_BACKGROUND_START_FAILED", exception);
        }
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;
}
