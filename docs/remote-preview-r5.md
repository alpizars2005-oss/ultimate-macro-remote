# Ultimate Macro Remote — R5 private development preview

This document describes the intended user experience and acceptance expectations for the private R5 preview. Remote remains optional and this branch should not be treated as an official/public release until the upstream project owner approves it and production hardening is complete.

## Intended client experience

A normal Remote user should not need Python, VS Code, PowerShell, a bot token, a Discord user ID, a guild ID, a pairing ticket, an `.env` file, or a backend URL to type manually.

The packaged preview contains:

- `Main_Remote.ahk` and the normal macro client files;
- self-contained `UltimateRemoteAgent.exe`;
- `remote_service.url` containing only the operator-selected public HTTPS origin;
- a short client README.

## First-run flow

On the first launch of `Main_Remote.ahk` from a valid packaged installation:

1. Ultimate Macro starts normally.
2. If the packaged Agent and service configuration are present, the macro starts Agent bootstrap independently. A Remote bootstrap failure must not prevent the normal macro UI from opening.
3. The Agent displays the Remote preview consent dialog once for the current Terms/Privacy version.
4. The user can decline Remote and keep using Ultimate Macro normally, or explicitly accept and choose **Connect Discord**.
5. Connect Discord opens the validated Discord OAuth authorization URL. Central uses authorization-code OAuth with only the `identify` scope to learn the authoritative Discord account.
6. The client cannot submit a Discord owner ID and does not receive the Discord bot token/OAuth client secret.
7. After OAuth authorization, the Agent polls its one-time setup session, receives the generated device credential, stores the enrollment envelope with DPAPI CurrentUser, acknowledges setup completion, and starts the background Agent.
8. If **Start the Remote Agent with Windows** is enabled, only the Agent is registered for the current Windows user.

Agent startup by itself does **not** start Roblox, Ultimate Macro, or a strategy.

## Subsequent launches

After a successful enrollment, the Agent normally reuses the DPAPI-protected enrollment without asking the user to authenticate Discord again.

Moving/re-extracting the macro to another valid local folder under the same Windows account and same trusted Remote service origin updates the trusted local macro root without requiring a new Discord authorization.

A different service origin is intentionally a new trust boundary and requires fresh enrollment. This matters during development because a Cloudflare Quick Tunnel hostname can change when the tunnel restarts.

## Discord controls

The R5 Agent supports exactly:

- `GET_STATUS` -> `/macro status`
- `LIST_STRATEGIES` -> `/macro strategies`
- `START_STRATEGY` -> `/macro start <strategy>`
- `STOP_SAFE` -> `/macro stop`
- `SWITCH_STRATEGY` -> `/macro switch <strategy>`

The slash-command owner comes from `interaction.user.id`. The current milestone supports one active linked device per Discord account and does not expose a device selector.

## Remote strategy catalog

The Agent enumerates only top-level `.strat` files under the approved local `Resources\Strats` root.

Discord receives path-free strategy display names plus opaque strategy IDs. It never receives the local absolute strategy path.

Nested strategy folders are not part of the current Remote catalog.

## Starting with the macro closed

`/macro start` can work while `Main_Remote.ahk` is closed, provided the enrolled background Agent is online and it can safely prove that a macro strategy is not already active.

The Agent resolves the requested opaque strategy ID locally, creates the fixed START request, and launches only:

```text
<macro root>\submacros\AutoHotkey64.exe <macro root>\Main_Remote.ahk
```

The protocol/server cannot supply an arbitrary executable path or arbitrary command line.

START is confirmed only after fresh evidence proves that the requested strategy lifecycle is running and Roblox is running. An early AHK `start_accepted` signal alone is not completion.

## Safe STOP and SWITCH

`/macro stop` and `/macro switch` intentionally do not interrupt an active strategy execution loop.

The Agent durably records the mutation and writes it to the fixed local one-slot mailbox. `Main_Remote.ahk` consumes STOP/SWITCH only at the validated between-match safe boundary.

Therefore a command may stay pending while the current game finishes or recovery work completes. That delay is expected.

Remote command handling must not be moved into `PlayStrategy()`, tower placement, upgrades, abilities, recorded Click/Send/Sleep sequences, or similar timing-sensitive code.

## Fail-closed mutation lifecycle

Gameplay-changing commands use a durable local journal.

The Agent records a command before performing a local mutation. If connection loss makes the local outcome ambiguous, central moves the command to reconciliation instead of replaying it.

On reconnect the server sends reconciliation metadata and the Agent compares it with its durable journal/local AHK evidence. Another conflicting gameplay-changing command is blocked while the previous outcome remains unresolved.

This is intentional: avoiding duplicate/unsafe mutation is more important than instant availability.

## What the Agent does not provide

There is no generic:

- shell/CMD/PowerShell execution;
- arbitrary process execution;
- remote desktop;
- arbitrary file browser;
- arbitrary network-supplied local path;
- download-and-execute capability;
- client-provided Discord owner/device ID.

The project is intentionally much narrower than a remote administration tool.

## Central operator configuration

Server secrets remain in the server's private untracked `.env`.

Minimum OAuth preview configuration:

```env
DISCORD_TOKEN=SERVER_ONLY_BOT_TOKEN
DISCORD_CLIENT_ID=YOUR_DISCORD_APPLICATION_CLIENT_ID
DISCORD_CLIENT_SECRET=SERVER_ONLY_OAUTH_CLIENT_SECRET
ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN=https://remote.example
```

Register exactly:

```text
https://remote.example/remote/v1/onboarding/discord/callback
```

The client package must never include the real `.env`, bot token, OAuth client secret, SQLite database, or device enrollment state.

## Packaging

Publish the self-contained Agent first:

```powershell
dotnet publish .\UltimateRemoteAgent\src\UltimateRemoteAgent\UltimateRemoteAgent.csproj -c Release -p:PublishProfile=win-x64
```

Then build the client ZIP:

```powershell
.\tools\package_remote_preview.ps1
```

With no explicit `-PublicOrigin`, the packager reads only `ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN` from the server `.env` and writes that public value into `remote_service.url`.

The packaging script refuses known server-only files and checks the staging tree for suspicious secret-like files.

## Automated verification

The pull-request workflow verifies:

- central Python unit/integration tests;
- .NET restore/build;
- Windows Agent unit/integration tests;
- `dotnet format --verify-no-changes`;
- self-contained `win-x64` publish;
- Windows PowerShell 5.1 local-client staging and preview-package smoke validation;
- upload of the generated client ZIP as a short-retention CI artifact when checks pass.

Automated tests do not replace real gameplay acceptance.

## Manual acceptance checklist

Before an upstream/public integration is considered ready, verify on real hardware:

- declining Remote leaves normal macro usage unaffected;
- OAuth onboarding completes without exposing server secrets to the client;
- `%LOCALAPPDATA%\UltimateRemoteAgent\enrollment.v1.bin` is created after successful onboarding;
- Agent reconnect survives normal restarts;
- `/macro status` works;
- `/macro strategies` exposes only approved strategies;
- `/macro start` can launch a closed `Main_Remote.ahk` installation;
- START reports completion only after real lifecycle evidence;
- `/macro switch` waits for and applies at the between-match safe boundary;
- `/macro stop` waits for and applies at the between-match safe boundary;
- Agent autostart alone does not launch Roblox/the macro;
- transport loss does not blindly replay a gameplay mutation.

## Known preview limits

- One active linked device per Discord account.
- Top-level `.strat` Remote catalog only.
- Quick Tunnel hostnames are development-only; stable production hosting is still required.
- Consent text is provisional, not final public Terms/Privacy.
- OAuth setup session state is process-local. A central crash after callback device provisioning but before Agent completion can leave an offline linked row requiring operator cleanup. This must be hardened before production.
- Public redistribution and bundled-asset/license decisions belong to the upstream project owner.

## Distribution status

This branch is private development work intended for upstream review. Do not publish the source/client package or distribute it to the wider community without the upstream project owner's approval.
