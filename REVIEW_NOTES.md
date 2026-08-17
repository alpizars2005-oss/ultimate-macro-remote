# Reviewer notes — Ultimate Macro Remote

This file is the shortest path for a developer reviewing the Remote contribution.

## Scope

The project adds an **optional** Discord Remote layer around Ultimate Macro. It is designed to preserve the existing gameplay/timing model rather than replace it.

The current private preview includes:

- Discord slash-command control.
- Discord OAuth2 `identify` onboarding.
- A self-contained Windows Agent.
- DPAPI CurrentUser enrollment storage.
- Optional current-user Windows autostart for the Agent only.
- Remote status and strategy catalog.
- START from a closed macro installation through a fixed launcher.
- Safe-boundary STOP and SWITCH through the existing Remote-aware AHK path.
- Durable mutation journal/reconciliation.
- Central SQLite owner/device/command state.
- Packaging and CI coverage.

## Fast code-review map

Start with these files:

- `Main_Remote.ahk` — Remote mailbox/start integration and safe boundary.
- `UltimateRemoteAgent/src/UltimateRemoteAgent/Program.cs` — Agent modes and bootstrap entry points.
- `UltimateRemoteAgent/src/UltimateRemoteAgent/Runtime/RemoteBootstrap.cs` / runtime support — consent, OAuth bootstrap, DPAPI enrollment reuse, Windows autostart, background launch.
- `UltimateRemoteAgent/src/UltimateRemoteAgent/Local/RemoteMutationBridge.cs` — fixed local START/STOP/SWITCH adapter and command evidence handling.
- `UltimateRemoteAgent/src/UltimateRemoteAgent/Local/StrategyCatalog.cs` — approved `.strat` catalog and opaque IDs.
- `central/onboarding.py` — Discord OAuth onboarding.
- `central/service.py` — owner-safe dispatch and lifecycle.
- `central/store.py` — SQLite persistence and credential/device state.
- `central/discord_bot.py` — safe Discord presentation and slash commands.
- `docs/remote-protocol-v1.md` — closed wire contract.

## Security properties intended by design

The Remote Agent is not a generic remote administration tool.

There is no protocol operation for:

- shell, CMD, or PowerShell execution;
- arbitrary process execution;
- remote desktop;
- arbitrary file browsing;
- arbitrary local paths supplied by Discord/the server;
- download-and-execute;
- changing the linked Discord owner from client input.

The network protocol carries opaque strategy IDs, not local strategy paths. The Agent resolves them only inside the approved local strategy root.

Discord commands use the invoking `interaction.user.id` as the authoritative command owner. Normal client onboarding learns identity only from Discord OAuth `identify`; the client does not submit a Discord user ID.

Device credentials are random bearer secrets returned to the Agent and stored server-side only as digests. The local enrollment envelope is protected by DPAPI CurrentUser.

## Gameplay safety property

The most important integration constraint is that Remote STOP/SWITCH must **not** break deterministic strategy timing.

Remote commands are deliberately not polled/executed inside `PlayStrategy()`, placements, upgrades, abilities, recorded Click/Send/Sleep sequences, or similar timing-sensitive gameplay loops.

STOP/SWITCH wait for the validated between-match boundary already exposed by `Main_Remote.ahk`. A user can therefore see a Remote request remain pending until the current match reaches that safe point. That delay is expected behavior, not a latency bug.

START is handled separately: if the Agent proves the macro is not running, it resolves the approved strategy locally and launches only the bundled AutoHotkey executable with the fixed `Main_Remote.ahk` script.

## Current preview limitations / items to review before production

1. **Single device per Discord account.** No multi-device selector exists yet.
2. **Top-level strategy catalog only.** Nested strategy folders are intentionally not enumerated.
3. **Stable hosting is not implemented here.** Development can use a trusted HTTPS/WSS tunnel, but production needs a stable hostname and normal service operations.
4. **Formal public Terms/Privacy are not final.** Current consent text is preview wording.
5. **OAuth setup session persistence needs hardening.** OAuth session state is in process memory. The callback currently provisions the device row before returning success so the browser cannot claim a link that does not exist. If the central process crashes after that provisioning but before the Agent completes enrollment, the in-memory session is lost and the newly provisioned offline device can require operator cleanup. A production version should persist a pending-enrollment state or otherwise make this crash window recoverable automatically.
6. **Distribution/licensing review remains upstream-owned.** This branch is intentionally private and should not be redistributed without project-owner approval.

## Verification commands

Central Python tests:

```powershell
.\.venv\Scripts\python.exe -B -m unittest discover -s tests -v
```

Windows Agent:

```powershell
dotnet restore .\UltimateRemoteAgent\UltimateRemoteAgent.slnx
dotnet build .\UltimateRemoteAgent\UltimateRemoteAgent.slnx -c Release --no-restore
dotnet test .\UltimateRemoteAgent\UltimateRemoteAgent.slnx -c Release --no-build
dotnet format .\UltimateRemoteAgent\UltimateRemoteAgent.slnx --verify-no-changes --no-restore
dotnet publish .\UltimateRemoteAgent\src\UltimateRemoteAgent\UltimateRemoteAgent.csproj -c Release -p:PublishProfile=win-x64
```

Package:

```powershell
.\tools\package_remote_preview.ps1
```

## Suggested manual acceptance

For a real integration review, use one central server and one Windows client installation and verify:

- consent decline does not block normal macro use;
- Connect Discord creates a DPAPI enrollment without client-side secrets;
- Agent reconnects after restart;
- `/macro status` works;
- `/macro strategies` returns only approved strategies;
- `/macro start` can start the fixed Remote macro when it is closed;
- `/macro switch` remains pending during gameplay and applies only at the safe boundary;
- `/macro stop` remains pending during gameplay and applies only at the safe boundary;
- Windows Agent autostart does not start Roblox/the macro by itself;
- central/Agent connection loss does not cause a gameplay mutation to be blindly replayed.

## Intent

This is a contribution intended to help the Ultimate Macro community and to be reviewable by the upstream developer. The branch is deliberately conservative about remote capabilities and availability whenever those goals conflict with gameplay integrity or local-machine safety.
