# Ultimate Macro Remote — R5 reviewer/developer setup

This branch contains the current private Remote development preview for Ultimate Macro. It includes the Discord/HTTP central service, Discord OAuth onboarding, the mutation-capable Windows Agent, `Main_Remote.ahk`, safe START/STOP/SWITCH integration, Windows-user autostart, packaging, and automated verification.

`Main.ahk` remains the stock fallback. Remote is optional.

## What is implemented now

The Remote command surface exposes the protocol-1 controls:

- `/macro status`
- `/macro strategies`
- `/macro start <strategy>`
- `/macro stop`
- `/macro switch <strategy>`

Normal enrollment is **Connect Discord** through OAuth2 authorization code + `identify`. `/macro pair` still exists only as a development fallback and is not the intended user experience.

The Windows Agent is self-contained for `win-x64`. It stores its enrollment using DPAPI CurrentUser, opens an outbound authenticated WSS connection, reads only the approved local macro state/strategy catalog, and implements the three gameplay mutations through fixed local adapters.

## Existing official bot: reuse it

If Ultimate Macro already has an official Discord bot/application, **do not create a second public bot just for Remote**.

Preferred upstream integration:

- keep the existing official bot process and token;
- use that same Discord application's Application/Client ID and OAuth2 Client Secret for Connect Discord;
- add the Remote callback URI to that same application;
- attach Remote `/macro` commands to the existing bot's command tree/cog/extension system using `MacroCommandController`;
- start the Remote aiohttp/WSS backend with one shared `RemoteService`/`RemoteStore`;
- keep the official bot's current intents, permissions, sync policy, logging, and unrelated commands;
- do not run the standalone `RemoteDiscordClient` at the same time with the same production bot token.

The detailed path is [`docs/existing-bot-integration.md`](docs/existing-bot-integration.md).

For isolated testing of this repository before upstream integration, use a separate private test Discord application with `run_bot.bat`.

## Current architecture

```text
Discord account
    |
    | OAuth identify (one-time onboarding)
    | slash commands (ongoing control)
    v
Central Remote components
  existing bot OR standalone preview bot
  + aiohttp backend + RemoteService + SQLite
    |
    | authenticated WSS /remote/v1/agent
    v
UltimateRemoteAgent.exe
    |
    +-- exact macro/process/state inspection
    +-- approved top-level Resources\Strats catalog
    +-- durable local mutation journal
    +-- fixed START launcher
    +-- one-slot safe STOP/SWITCH mailbox
    v
Main_Remote.ahk
    |
    +-- startup START consumption
    +-- between-match safe STOP/SWITCH gate
    v
Roblox / TDS
```

## Standalone central development setup

This section is for isolated testing of the repository's own Remote Discord client. For integration into the official bot, use `docs/existing-bot-integration.md` instead.

From the repository root on the server/development PC:

1. Run `setup_bot.bat` once to create `.venv` and install Python dependencies.
2. Copy `.env.example` to `.env` and fill the real values locally. Never commit or share the real `.env`.
3. Required OAuth preview configuration:
   - `DISCORD_TOKEN`
   - `DISCORD_CLIENT_ID`
   - `DISCORD_CLIENT_SECRET`
   - `ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN`
4. Keep the backend bound to `127.0.0.1` when using a trusted TLS reverse proxy/tunnel.
5. Register the exact Discord redirect URI:

   ```text
   https://YOUR_REMOTE_HOST/remote/v1/onboarding/discord/callback
   ```

6. Validate configuration without exposing secrets:

   ```powershell
   .\.venv\Scripts\python.exe -m central.preflight --require-oauth
   ```

7. Start the single standalone development runtime:

   ```powershell
   .\run_bot.bat
   ```

8. Verify local health at `/healthz` and the public HTTPS route before enrolling a client.

The backend and standalone Discord bot share the same `RemoteStore` and SQLite database in one process. Do not run a second writer against the same database directory.

## Build the Windows Agent

```powershell
dotnet restore .\UltimateRemoteAgent\UltimateRemoteAgent.slnx
dotnet build .\UltimateRemoteAgent\UltimateRemoteAgent.slnx -c Release --no-restore
dotnet test .\UltimateRemoteAgent\UltimateRemoteAgent.slnx -c Release --no-build
dotnet publish .\UltimateRemoteAgent\src\UltimateRemoteAgent\UltimateRemoteAgent.csproj -c Release -p:PublishProfile=win-x64
```

The published self-contained executable is under the `win-x64\publish` directory. End-user clients do not need an installed .NET runtime.

## Build a zero-config client preview

With the same current public origin configured in `.env`:

```powershell
.\tools\package_remote_preview.ps1
```

The ZIP contains the user-facing macro, the published Agent, and `remote_service.url`. It intentionally excludes server-only files and secrets.

For development from the repository root without packaging:

```powershell
.\tools\prepare_local_remote_client.ps1
```

This stages the published Agent and public service origin next to `Main_Remote.ahk` so the repository root can behave like a client install. Use the packaged ZIP for a cleaner end-user acceptance test.

## Expected client flow

On first launch of a valid packaged `Main_Remote.ahk` installation:

1. The macro UI remains usable even if Remote bootstrap fails.
2. The Agent displays the Remote preview consent dialog once per Terms version.
3. **Connect Discord** opens the validated Discord OAuth URL.
4. Central learns the owner only from Discord `identify`.
5. The Agent polls the one-time setup session, receives the device credential, stores the enrollment with DPAPI CurrentUser, acknowledges completion, and starts the background Agent.
6. Optional Windows autostart registers only `UltimateRemoteAgent.exe run-background` for the current user. It does not start Roblox or a strategy on login.

After successful enrollment, reopening or moving the macro within the same Windows account and same service origin reuses the local enrollment. Changing the service origin intentionally requires fresh enrollment.

## START behavior with the macro closed

`START_STRATEGY` is allowed when the Agent can prove the macro is not already running. The Agent resolves the selected opaque strategy ID locally, writes the fixed START request, and launches only:

```text
<macro root>\submacros\AutoHotkey64.exe <macro root>\Main_Remote.ahk
```

No network-supplied executable path or arbitrary command line is accepted.

## Safe STOP/SWITCH behavior

STOP and SWITCH are never injected into active gameplay timing. They enter the local mailbox and wait for the existing validated between-match gate in `Main_Remote.ahk`. Recovery/reconnect handling may therefore delay them. This is intentional.

Do not move Remote command polling into `PlayStrategy()`, placement, upgrades, abilities, recorded actions, Click/Send/Sleep timing, or other gameplay-sensitive loops.

## Verification

Central:

```powershell
.\.venv\Scripts\python.exe -B -m unittest discover -s tests -v
```

Windows:

```powershell
dotnet test .\UltimateRemoteAgent\UltimateRemoteAgent.slnx -c Release
dotnet format .\UltimateRemoteAgent\UltimateRemoteAgent.slnx --verify-no-changes
```

The pull-request CI additionally publishes the self-contained Agent and smoke-tests the client package under Windows PowerShell 5.1.

## Important preview limits

- Exactly one active linked device per Discord account in this milestone.
- Strategy discovery is top-level `.strat` only under `Resources\Strats`.
- Cloudflare Quick Tunnel hostnames are development-only and may change; the server `.env`, Discord redirect URI, and packaged client origin must stay in sync.
- Formal public Terms/Privacy text and production operations are not finished.
- OAuth setup sessions are process-local. If the central process dies after durable device provisioning but before the Agent completes setup, an offline row can require operator cleanup before retrying. Do not represent this preview as production-ready until onboarding persistence/cleanup is hardened.
- The standalone Remote Discord client is for isolated preview testing; official integration should preserve the existing bot and command tree.
- Do not publicly distribute this branch or client package without upstream approval.

## Recommended reading order

1. [`REVIEW_NOTES.md`](REVIEW_NOTES.md)
2. [`docs/existing-bot-integration.md`](docs/existing-bot-integration.md) — especially for the official Ultimate Macro bot
3. [`docs/remote-architecture.md`](docs/remote-architecture.md)
4. [`docs/remote-protocol-v1.md`](docs/remote-protocol-v1.md)
5. [`docs/windows-agent-r5.md`](docs/windows-agent-r5.md)
6. [`docs/remote-server-r5.md`](docs/remote-server-r5.md)
7. [`docs/remote-preview-r5.md`](docs/remote-preview-r5.md)
8. [`docs/remote-pairing-v1.md`](docs/remote-pairing-v1.md) only if the legacy fallback is relevant
