# Ultimate Macro Remote — R5 Development Preview

This document describes the intended client experience and the developer/operator setup for the private R5 preview. Remote remains optional and the pull request remains a development preview until physical two-PC acceptance is complete.

## Client experience

A normal Remote user should not need Python, VS Code, PowerShell, a bot token, a Discord user ID, a guild ID, a pairing ticket, an `.env` file, or a backend URL to type manually.

The packaged preview contains the fixed Windows Agent and a `remote_service.url` chosen by the operator. On the first launch of `Main_Remote.ahk`:

1. Ultimate Macro starts normally.
2. If the packaged Remote Agent and service configuration are present, the macro starts the Agent bootstrap independently. A bootstrap failure never prevents the macro UI from opening.
3. The Agent shows the Remote consent window once for the current Terms/Privacy version.
4. The user may decline Remote and continue using Ultimate Macro normally, or explicitly accept and choose **Connect Discord**.
5. Connect Discord opens only the validated Discord OAuth authorization page. The central service uses the authorization-code flow with the `identify` scope to learn the authoritative Discord account. The client cannot submit a Discord owner ID.
6. After authorization, the Agent obtains a one-time device credential, stores the complete enrollment envelope with Windows DPAPI CurrentUser protection, acknowledges setup, and starts the background Agent.
7. When **Start the Remote Agent with Windows** is enabled, the Agent registers only for the current Windows user. Starting the Agent does not start Roblox or a strategy.

After the first successful setup, the user normally interacts only through Discord `/macro` commands. Moving or re-extracting the macro to another valid local folder on the same Windows account and the same trusted Remote service origin refreshes the local macro root without asking the user to authorize Discord again. Changing the service origin is a new trust boundary and requires fresh enrollment.

## Supported protocol-1 operations

The R5 Agent advertises exactly these five operations:

- `GET_STATUS`
- `LIST_STRATEGIES`
- `START_STRATEGY`
- `STOP_SAFE`
- `SWITCH_STRATEGY`

There is no generic process execution, shell, CMD/PowerShell command, arbitrary file browser, download-and-execute operation, remote desktop, OCR loop, or network-supplied executable path.

`START_STRATEGY` resolves the opaque strategy ID inside the approved local `Resources\Strats` root and launches only the bundled `submacros\AutoHotkey64.exe` with the fixed absolute `Main_Remote.ahk` entry point.

`STOP_SAFE` and `SWITCH_STRATEGY` are written to the existing one-slot UTF-16 local mailbox and are consumed only by the validated between-match gate in `RunStrategy()`. They never interrupt `PlayStrategy()`, placements, upgrades, abilities, recorded Click/Send/Sleep steps, or other timing-sensitive gameplay execution.

## Fail-closed mutation lifecycle

Gameplay-changing commands use a durable local journal. The Agent records acceptance before reporting `accepted`, records `executing` before any local side effect can be placed, and records typed completion only after local evidence proves the requested result.

After a mailbox or launch side effect may exist, a wall-clock timeout is not allowed to turn uncertainty into a failure. If the connection is lost, the command remains reconciling. On reconnect the Agent reads durable local evidence and never replays the mutation. A queued safe STOP/SWITCH may therefore remain pending until the macro reaches its next validated between-match boundary.

START completion requires fresh lifecycle evidence for the requested strategy and a running Roblox process. A stale `state.ini` or an early `start_accepted` marker alone is not sufficient.

## Central operator configuration

The public client package never contains central secrets. The server keeps the Discord bot token and OAuth client secret in its untracked `.env`.

For OAuth onboarding configure the central server with:

```env
DISCORD_TOKEN=SERVER_ONLY_BOT_TOKEN
DISCORD_CLIENT_ID=YOUR_DISCORD_APPLICATION_CLIENT_ID
DISCORD_CLIENT_SECRET=SERVER_ONLY_OAUTH_CLIENT_SECRET
ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN=https://remote.example
```

Register this exact redirect URI for the Discord application:

```text
https://remote.example/remote/v1/onboarding/discord/callback
```

The origin must use normally trusted HTTPS/WSS. A Cloudflare Quick Tunnel is suitable for temporary development testing, but its random hostname changes when the tunnel restarts. Production should use a stable trusted hostname/service.

The old `/macro pair` ticket flow remains only as a development fallback. It is not part of the intended end-user onboarding experience.

## Packaging

After publishing the Windows Agent, build a client preview from the repository root:

```powershell
.\tools\package_remote_preview.ps1 -PublicOrigin https://remote.example
```

The packager copies the user-facing macro files, published `UltimateRemoteAgent.exe`, and the public service origin into the client ZIP. It refuses known server-only files such as `.env`, `bot.py`, and `requirements.txt` and checks for suspicious secret-like files.

## Automated verification

The pull-request workflow runs:

- central Python unit/integration tests on Ubuntu;
- .NET restore/build with warnings treated as errors on Windows;
- Windows Agent tests;
- `dotnet format --verify-no-changes`;
- self-contained `win-x64` publish;
- zero-config client ZIP smoke packaging.

Automated verification does not replace physical acceptance. Before the preview is treated as accepted, run a real PC-A/server + PC-B/client test for consent/OAuth, restart persistence, `/macro status`, strategy catalog, START, safe SWITCH, safe STOP, Agent reconnect/reconciliation, and Windows-login Agent startup without automatically launching Roblox.

## Distribution status

This branch and PR are private development work. Public redistribution still requires the project owner's approval and a separate audit of third-party binaries/assets and their licenses. Formal Terms and Privacy documents must replace the provisional preview text before a public release.
