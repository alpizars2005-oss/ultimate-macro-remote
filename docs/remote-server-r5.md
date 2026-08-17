# Ultimate Macro Remote — R5 central server setup

This document is for the central Remote operator/developer. It is **not** copied to normal client PCs.

## Runtime model

The normal R5 development runtime is one process started by `run_bot.bat` containing:

- the Discord client/slash-command adapter;
- the aiohttp HTTP/WebSocket server;
- Discord OAuth onboarding;
- the legacy development pairing fallback;
- one shared `RemoteService`;
- one shared `RemoteStore` backed by SQLite.

Do not start multiple writers against the same SQLite state directory.

### Reusing the existing official Ultimate Macro bot

`run_bot.bat` is the **standalone private-preview composition**. It is not a requirement for an upstream integration.

If Ultimate Macro already has an official Discord bot/application, the preferred production/upstream shape is to keep that bot and integrate the Remote components into its existing runtime instead of creating a second public bot.

Reuse the same Discord application values:

```env
DISCORD_TOKEN=<existing official bot token>
DISCORD_CLIENT_ID=<same Discord application's Application/Client ID>
DISCORD_CLIENT_SECRET=<same application's OAuth2 Client Secret>
```

Keep the bot token and OAuth Client Secret private and server-side. They are different credentials.

For the official bot, do **not** launch another `RemoteDiscordClient` with the same token while the existing bot is already online. Instead:

- create one `RemoteStore` and one `RemoteService`;
- create the Remote `PairingService`/`OnboardingService` around that same store;
- start the aiohttp app created by `central.server.create_app(...)` in the existing process/event loop;
- instantiate `MacroCommandController` using the shared service/pairing instances;
- add the Remote `/macro` commands to the official bot's existing `CommandTree`/cog/extension system;
- use the official bot's existing command sync/deployment policy.

The standalone preview client's `setup_hook()` synchronizes the command tree it owns. That is useful for a dedicated test Discord application, but it should not be allowed to replace/omit unrelated commands from an existing official bot application.

Detailed steps and a code-integration outline are in [`existing-bot-integration.md`](existing-bot-integration.md).

## Compatibility/configuration model

`ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN` is the shared public origin used by:

- Discord OAuth onboarding callback;
- Agent WSS derivation;
- legacy `/macro pair` development redemption;
- zero-config client packaging.

Configuration behavior:

- Public origin only -> legacy development pairing can use it; browser OAuth remains disabled if OAuth credentials are absent.
- `DISCORD_CLIENT_ID` + `DISCORD_CLIENT_SECRET` + public origin -> R5 **Connect Discord** onboarding is enabled.
- Only one OAuth credential or a partial OAuth configuration -> startup/preflight fails closed.

The client ZIP should be built from the same current public origin as the central `.env`.

## Central `.env`

Keep the real `.env` private, untracked, and server-only.

Minimum OAuth preview fields:

```env
DISCORD_TOKEN=<server-only bot token>
DISCORD_GUILD_ID=<optional development guild id>

DISCORD_CLIENT_ID=<Discord application id>
DISCORD_CLIENT_SECRET=<server-only OAuth client secret>

ULTIMATE_REMOTE_BIND_HOST=127.0.0.1
ULTIMATE_REMOTE_BIND_PORT=8765
ULTIMATE_REMOTE_DATABASE_PATH=runtime/central/remote.db
ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN=https://YOUR_REMOTE_HOST
```

Rate-limit/TTL fields and optional direct-TLS certificate paths are documented in `.env.example`.

Never copy any of the following into the client package:

- real `.env`;
- Discord bot token;
- Discord OAuth client secret;
- SQLite database;
- backend logs containing credentials;
- Python virtual environment;
- device enrollment/credentials.

## Discord OAuth redirect

Register exactly one callback matching the configured public origin on the same Discord application used by the official bot:

```text
https://YOUR_REMOTE_HOST/remote/v1/onboarding/discord/callback
```

The central preflight prints the exact callback URI without printing secret values.

If the public hostname changes, the Discord Developer Portal redirect URI must change with it.

The Remote browser onboarding asks only for OAuth `identify`; it does not require a second bot user or a second public bot application.

## Safe preflight

Normal configuration validation:

```powershell
.\.venv\Scripts\python.exe -m central.preflight
```

Require working OAuth configuration:

```powershell
.\.venv\Scripts\python.exe -m central.preflight --require-oauth
```

`run_bot.bat` performs the normal preflight before launching the standalone preview runtime.

## Start central runtime

For isolated development with the repository's own Discord client:

```powershell
.\run_bot.bat
```

For integration into the existing official bot, start the aiohttp Remote app and shared Remote services using the official bot's lifecycle instead; see `existing-bot-integration.md`.

The development listener normally remains on:

```text
127.0.0.1:8765
```

Health endpoint:

```text
/healthz
```

The raw loopback HTTP listener should not be exposed directly to untrusted networks. Use normally trusted HTTPS/WSS termination for cross-machine access.

## Reverse proxy/tunnel requirements

A development tunnel or production reverse proxy must:

- terminate or pass through normally trusted TLS as appropriate;
- preserve WebSocket Upgrade semantics;
- preserve the `Authorization` header for Agent/pairing traffic;
- avoid logging bearer credentials or pairing response bodies;
- forward requests to the configured central listener without rewriting identity from untrusted client headers.

The backend does not trust arbitrary `X-Forwarded-For` as authoritative client identity.

## Cloudflare Quick Tunnel development

A Quick Tunnel is acceptable for temporary private development because it provides a trusted HTTPS/WSS hostname without opening an inbound listener directly on the client PC.

Its hostname is ephemeral. If `cloudflared` restarts and returns a new `*.trycloudflare.com` origin:

1. update `ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN` in `.env`;
2. replace the Discord OAuth Redirect URI with the callback under that hostname;
3. restart the Remote backend/runtime;
4. rebuild/re-stage the client so `remote_service.url` contains the same new origin;
5. expect existing client enrollments bound to the previous origin to require fresh onboarding.

A stable production hostname removes this development-only re-enrollment churn.

## Build the matching zero-config client

Publish the Agent first:

```powershell
dotnet publish .\UltimateRemoteAgent\src\UltimateRemoteAgent\UltimateRemoteAgent.csproj -c Release -p:PublishProfile=win-x64
```

Then:

```powershell
.\tools\package_remote_preview.ps1
```

When `-PublicOrigin` is omitted, the packager reads `ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN` from `.env` and embeds only that public origin into `remote_service.url`.

Explicit origin remains available for CI/testing:

```powershell
.\tools\package_remote_preview.ps1 -PublicOrigin https://remote.example
```

The package script stages user-facing macro files plus the published Agent and rejects known server-only files.

## Local development client staging

For testing from the repository root without extracting a ZIP:

```powershell
.\tools\prepare_local_remote_client.ps1
```

This copies the current published Agent to the repository root and writes `remote_service.url` from `.env`.

Use:

```powershell
.\tools\prepare_local_remote_client.ps1 -ResetRemoteChoice
```

only when a developer intentionally wants to reset the local preview consent choice for testing. This helper is not an end-user workflow.

## OAuth onboarding lifecycle

Normal onboarding is:

```text
Agent creates random setup secret
        |
        v
POST begin (setup secret in Authorization header)
        |
        v
central creates OAuth state + authorization URL
        |
        v
browser -> Discord OAuth identify
        |
        v
callback verifies state and Discord identity
        |
        v
central provisions linked device + one-time credential
        |
        v
Agent polls setup session
        |
        v
Agent stores DPAPI enrollment
        |
        v
Agent completes setup
        |
        v
background Agent connects by authenticated WSS
```

The browser callback must not accept a client-supplied Discord ID.

## Current one-device behavior

The current milestone supports one active device per Discord owner.

If a Discord account already has an active device row, new OAuth onboarding or legacy pairing for that owner is rejected. Discord command dispatch also fails closed if the store contains an unsupported multiple-device situation.

A future multi-device feature should add an explicit selection/naming design instead of choosing a device implicitly.

## Current onboarding crash limitation

OAuth setup sessions currently live in process memory.

The callback provisions the durable device row before returning browser success. This prevents the browser from saying "connected" while no device exists, but creates a known preview crash window:

- if the central process remains alive and setup expires before Agent completion, normal session cleanup revokes the unacknowledged device;
- if the central process crashes/restarts after provisioning but before Agent completion, the in-memory setup session is lost while the device row can remain active/offline.

Current recovery for that development edge case is operator cleanup/revocation before retrying enrollment.

Before production, persist an explicit pending-enrollment state or implement another durable recovery mechanism so this path repairs itself across central restarts.

## Database and operational notes

The SQLite database is central server state. Keep it local to the server filesystem.

For private development it is acceptable to reset the development database when deliberately starting a clean test. That is **not** a production migration strategy.

A production integration should define:

- schema migration policy;
- backup/restore expectations;
- device revocation/unlink UX;
- secret rotation;
- service supervision/restart behavior;
- logging retention and redaction;
- monitoring/health checks;
- stable TLS/hostname ownership.

## Legacy `/macro pair`

`/macro pair` is retained only as a development fallback. It is not the intended R5 user flow and can be omitted from the official bot's public command tree.

See `remote-pairing-v1.md` for the isolated ticket contract.

## Production gate

Do not call the current development stack production-ready until at least:

- the upstream owner approves integration/distribution;
- the Remote command group has been integrated without breaking the official bot's existing commands/features;
- stable hosting replaces Quick Tunnel development;
- OAuth pending-enrollment crash recovery is durable;
- formal Terms/Privacy are reviewed;
- operator unlink/revoke and lifecycle policy are defined;
- real multi-machine acceptance covers START, safe SWITCH, safe STOP, reconnect, and Windows-login Agent startup;
- third-party bundled assets/binaries and licensing are reviewed.
