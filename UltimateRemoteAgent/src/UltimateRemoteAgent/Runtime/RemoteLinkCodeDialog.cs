using UltimateRemoteAgent.Enrollment;

namespace UltimateRemoteAgent.Runtime;

internal static class RemoteLinkCodeDialog
{
    internal static LinkingReady WaitForLink(
        string code,
        DateTimeOffset expiresAt,
        Func<CancellationToken, Task<LinkingReady?>> poll,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(poll);

        LinkingReady? linked = null;
        Exception? failure = null;
        var uiThread = new Thread(() =>
        {
            try
            {
                linked = WaitForLinkOnSta(
                    code,
                    expiresAt,
                    poll,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
            Name = "UltimateRemoteLinkCode",
        };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        uiThread.Join();

        if (failure is LinkingClientException linkingFailure)
        {
            throw linkingFailure;
        }
        if (failure is OperationCanceledException canceled)
        {
            throw canceled;
        }
        if (failure is not null)
        {
            throw new AgentRuntimeException("LINK_DIALOG_FAILED", failure);
        }
        return linked
            ?? throw new OperationCanceledException("Remote Discord linking was cancelled.");
    }

    private static LinkingReady WaitForLinkOnSta(
        string code,
        DateTimeOffset expiresAt,
        Func<CancellationToken, Task<LinkingReady?>> poll,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        using var form = new Form
        {
            Text = "Link Ultimate Macro to Discord",
            Width = 560,
            Height = 360,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            TopMost = true,
        };
        var title = new Label
        {
            Text = "Your Discord linking code",
            AutoSize = true,
            Font = new Font("Segoe UI", 15, FontStyle.Bold),
            Left = 28,
            Top = 24,
        };
        var instructions = new Label
        {
            Text = "In the official Ultimate Macro Discord server, run:",
            AutoSize = true,
            Left = 28,
            Top = 72,
        };
        var command = new TextBox
        {
            Text = $"/macro link {code}",
            ReadOnly = true,
            Left = 28,
            Top = 102,
            Width = 488,
            Height = 36,
            Font = new Font("Consolas", 14, FontStyle.Bold),
            TextAlign = HorizontalAlignment.Center,
            TabStop = false,
        };
        var codeLabel = new Label
        {
            Text = code,
            AutoSize = false,
            Left = 28,
            Top = 154,
            Width = 488,
            Height = 44,
            Font = new Font("Consolas", 20, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        var expiry = new Label
        {
            Text = $"Code expires at {expiresAt.ToLocalTime():t}. This window closes automatically after Discord confirms the link.",
            AutoSize = false,
            Left = 28,
            Top = 205,
            Width = 488,
            Height = 42,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        var status = new Label
        {
            Text = "Waiting for Discord confirmation…",
            AutoSize = false,
            Left = 28,
            Top = 252,
            Width = 300,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var copy = new Button
        {
            Text = "Copy command",
            Width = 118,
            Height = 34,
            Left = 274,
            Top = 282,
        };
        var cancel = new Button
        {
            Text = "Cancel",
            Width = 118,
            Height = 34,
            Left = 398,
            Top = 282,
            DialogResult = DialogResult.Cancel,
        };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(command.Text);
                status.Text = "Command copied. Waiting for Discord confirmation…";
            }
            catch (Exception)
            {
                status.Text = "Copy failed; select the command above manually.";
            }
        };
        form.Controls.AddRange([
            title,
            instructions,
            command,
            codeLabel,
            expiry,
            status,
            copy,
            cancel,
        ]);
        form.CancelButton = cancel;

        LinkingReady? linked = null;
        Exception? failure = null;
        bool pollInProgress = false;
        using var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += async (_, _) =>
        {
            if (pollInProgress)
            {
                return;
            }
            if (DateTimeOffset.UtcNow >= expiresAt)
            {
                failure = new LinkingClientException("LINK_TIMEOUT");
                form.Close();
                return;
            }

            pollInProgress = true;
            timer.Stop();
            try
            {
                linked = await poll(linkedCancellation.Token);
                if (linked is not null)
                {
                    status.Text = "Discord linked successfully.";
                    form.DialogResult = DialogResult.OK;
                    form.Close();
                    return;
                }
            }
            catch (Exception exception)
            {
                failure = exception;
                form.Close();
                return;
            }
            finally
            {
                pollInProgress = false;
            }
            if (!form.IsDisposed)
            {
                timer.Start();
            }
        };
        form.Shown += (_, _) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                form.Close();
                return;
            }
            timer.Start();
        };

        using CancellationTokenRegistration cancellationRegistration =
            cancellationToken.Register(() =>
            {
                try
                {
                    if (form.IsHandleCreated && !form.IsDisposed)
                    {
                        form.BeginInvoke(form.Close);
                    }
                }
                catch (InvalidOperationException)
                {
                }
            });

        DialogResult result = form.ShowDialog();
        timer.Stop();
        linkedCancellation.Cancel();

        if (failure is LinkingClientException linkingFailure)
        {
            throw linkingFailure;
        }
        if (failure is OperationCanceledException canceled)
        {
            throw canceled;
        }
        if (failure is not null)
        {
            throw new AgentRuntimeException("LINK_DIALOG_FAILED", failure);
        }
        if (linked is not null && result == DialogResult.OK)
        {
            return linked;
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new OperationCanceledException("Remote Discord linking was cancelled.");
    }
}
