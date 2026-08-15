# UltimateRemoteAgent R3

R3 is the self-contained Windows transport and read-only local bridge. It implements
only protocol-1 `GET_STATUS` and `LIST_STRATEGIES`. It cannot launch AutoHotkey or
Roblox, write the AHK mailbox, start/stop/switch a strategy, browse arbitrary files,
run shell commands, persist itself at logon, or control the desktop.

## Build and automated simulation

From the repository root on Windows with the .NET 10 SDK:

```powershell
dotnet restore .\UltimateRemoteAgent\UltimateRemoteAgent.slnx
dotnet build .\UltimateRemoteAgent\UltimateRemoteAgent.slnx -c Release --no-restore
dotnet test .\UltimateRemoteAgent\UltimateRemoteAgent.slnx -c Release --no-build
dotnet publish .\UltimateRemoteAgent\src\UltimateRemoteAgent\UltimateRemoteAgent.csproj -c Release -p:PublishProfile=win-x64
```

The self-contained output is under
`UltimateRemoteAgent\src\UltimateRemoteAgent\bin\Release\net10.0-windows\win-x64\publish`.
Copy that published output—not the source tree—to the client PC. The client does not
need Python, VS Code, `bot.py`, or an installed .NET runtime.

The .NET suite is the exact local simulated transport test. It uses fake sockets and
HTTP handlers to verify fragmented WSS frames, strict protocol schemas, serialized
writes, heartbeat/reconnect components, pairing-header isolation, read-command
lifecycle, and non-execution of mutating commands without weakening production TLS.
Run the existing central suite separately:

```powershell
.\.venv\Scripts\python.exe -B -m unittest discover -s tests -v
```

## Path-free local inspection

Before enrollment, run the published executable against the extracted macro root:

```powershell
.\UltimateRemoteAgent.exe inspect "C:\path\to\TDS_Macro"
```

The command validates the fixed installation and approved `Resources\Strats` root,
performs one conservative process/state sample, and prints only the protocol snapshot
plus opaque strategy IDs and display names. It prints no absolute strategy path. When
the exact bundled AHK/Remote script is absent, stale `state.ini` cannot change the
result from `not_running`.

## Trusted-transport development test

This test keeps the existing R2 Discord pairing design. A normally trusted HTTPS/WSS
origin is a prerequisite; R3 intentionally cannot connect to raw loopback `http/ws`, a
self-signed certificate that Windows does not trust, or a certificate-bypass mode.

1. On PC A, install `requirements.txt`, configure the untracked central `.env`, and
   set `ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN` to the trusted origin. Keep the backend on
   loopback behind the trusted proxy/tunnel, or configure both direct TLS certificate
   paths. The proxy must preserve `Authorization`, POST, and WebSocket Upgrade headers
   and redact authorization headers and pairing response bodies.
2. Start `run_bot.bat` and invoke `/macro pair` from the Discord account that will own
   the device. Do not post or log the ephemeral ticket.
3. On the interactive Windows user account on PC B, extract the macro and published
   Agent, then run:

   ```powershell
   .\UltimateRemoteAgent.exe pair https://remote.example "C:\path\to\TDS_Macro"
   ```

   Paste the ticket only into the Agent's hidden prompt. The Agent sends an empty HTTPS
   POST with `Authorization: Pairing`, derives only the fixed WSS path from the response,
   and writes an encrypted DPAPI CurrentUser envelope to
   `%LOCALAPPDATA%\UltimateRemoteAgent\enrollment.v1.bin`.
4. Start it manually; R3 installs no startup entry:

   ```powershell
   .\UltimateRemoteAgent.exe run
   ```

5. From Discord, run `/macro status` and `/macro strategies`. Strategy choices contain
   opaque IDs, never client paths. `/macro start`, `/macro stop`, and `/macro switch`
   must report that the connected Agent does not support those operations; they must
   not create a local mailbox or launch anything.
6. Stop the Agent with Ctrl+C. Do not copy the enrollment file to another Windows user;
   DPAPI CurrentUser intentionally binds decryption to the enrolling account.

If a successful pairing response is lost, do not retry the same ticket. Follow the R2
operator recovery rule: revoke the orphan device, issue a new short-lived ticket, and
pair again. R4 mutation work and R5 onboarding/autostart are explicitly out of scope.
