# Integrating Remote into an existing Ultimate Macro Discord bot

The Remote project does **not** require a second Discord bot or a second Discord application.

If Ultimate Macro already has an official Discord bot/application, the preferred upstream integration is to reuse that application, keep its existing bot identity and commands, and attach the Remote `/macro` group to the bot's existing command tree.

This is preferable to creating another public bot just for Remote.

## Important: do not run two competing bot clients with the same token

The private preview includes `RemoteDiscordClient` and `run_bot.bat` so the Remote stack can be tested in isolation.

That standalone client owns its own `discord.py` `CommandTree` and synchronizes its commands during startup. It is appropriate for a dedicated development application, but it is **not** the recommended integration path for an existing production/official bot that already has its own commands.

For the official Ultimate Macro bot:

- keep the existing bot process/client;
- keep its current bot token;
- keep its current command tree and sync policy;
- add the Remote command group to that existing tree;
- run the Remote HTTP/WSS backend components alongside the existing bot, sharing one `RemoteService`/`RemoteStore`;
- do not start a second `RemoteDiscordClient` with the same bot token at the same time.

This avoids duplicate Gateway sessions and avoids an isolated Remote command-tree sync accidentally replacing/omitting commands that belong to the existing bot.

## Discord application values to reuse

Use the **same Discord application** that owns the existing Ultimate Macro bot.

Server-side configuration maps as follows:

```env
# Existing official bot token — keep server-side only.
DISCORD_TOKEN=<existing Ultimate Macro bot token>

# Application/Client ID of that same Discord application.
DISCORD_CLIENT_ID=<existing application id>

# OAuth2 Client Secret of that same Discord application.
# This is NOT the bot token. Keep it server-side only.
DISCORD_CLIENT_SECRET=<OAuth2 client secret for the same application>

ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN=https://remote.example
```

The bot token and OAuth client secret are two different secrets. Neither belongs in the Windows client, Git, screenshots, or a public config file.

Discord's current developer documentation treats the Client ID as public application metadata and the Client Secret/bot token as sensitive server credentials:

- https://docs.discord.com/developers/activities/building-an-activity
- https://docs.discord.com/developers/quick-start/getting-started

## Add the OAuth callback to the existing application

In the Discord Developer Portal, open the **existing Ultimate Macro application** and add this redirect URI under OAuth2:

```text
https://YOUR_REMOTE_HOST/remote/v1/onboarding/discord/callback
```

It must exactly match `ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN` plus the callback path.

Remote onboarding requests only the Discord OAuth `identify` scope. It uses that response to bind a Windows Remote installation to the authoritative Discord account.

No separate bot invitation is required merely for the OAuth callback.

If the existing bot already uses application/slash commands, keep its existing installation. Discord currently documents `applications.commands` as the application-command authorization scope and notes that it is included when using the `bot` scope for a bot installation:

- https://docs.discord.com/developers/interactions/application-commands

## Preferred architecture with an existing bot

```text
Existing Ultimate Macro bot process
        |
        +-- existing commands/features
        |
        +-- existing discord.Client/Bot + existing CommandTree
        |        |
        |        +-- attach Remote /macro group here
        |              |
        |              v
        |        MacroCommandController
        |              |
        +--------------+---------- RemoteService
                                      |
                                      +-- RemoteStore / SQLite
                                      |
                                      +-- Agent sessions
                                      |
                                      v
                              aiohttp Remote app
                                      |
                          HTTPS/WSS reverse proxy
                                      |
                                      v
                             UltimateRemoteAgent
```

The key rule is that the Discord command layer and HTTP/WSS layer must use the **same `RemoteService` and `RemoteStore` instances** for a running process.

## Components to reuse from this repository

The existing bot does not need `RemoteDiscordClient` itself. Reuse the lower-level pieces:

```python
from central.config import RemoteConfig
from central.discord_bot import MacroCommandController, DiscordBotOptions
from central.onboarding import OnboardingService
from central.pairing import PairingService
from central.server import create_app
from central.service import RemoteService
from central.store import RemoteStore
```

`MacroCommandController` contains the safe Discord-facing behavior for:

- status;
- strategy listing/cache/autocomplete data;
- start;
- safe stop;
- safe switch;
- sanitized user-facing errors.

The existing bot can call that controller from its own slash-command callbacks while preserving its existing client, tree, logging, permissions, checks, cogs/extensions, and deployment model.

## Backend initialization inside the existing bot process

The upstream bot should create the Remote backend objects once during startup.

Conceptually:

```python
remote_config = RemoteConfig.from_environment()
store = RemoteStore(remote_config.database_path)
service = RemoteService(
    store,
    command_delivery_ttl_seconds=remote_config.command_delivery_ttl_seconds,
)

pairing = PairingService(store, pairing_options)
onboarding = OnboardingService(store, onboarding_options)

remote_app = create_app(
    remote_config,
    store=store,
    service=service,
    pairing_service=pairing,
    onboarding_service=onboarding,
)

controller = MacroCommandController(
    service,
    pairing,
    DiscordBotOptions(
        token="",  # existing bot already owns Gateway authentication
        guild_id=development_guild_id,
    ),
)
```

The exact option construction can follow `central/runtime.py`; the important part is that all components share one `store` and one `service`.

Start the returned aiohttp app using an `AppRunner`/`TCPSite` task in the existing bot's asyncio event loop, or place the HTTP backend in a separate service **only if** the Discord command process can still communicate with the same authoritative central service safely. The current code is simplest as one process.

## Adding `/macro` to the existing command tree

The preview implementation in `central/discord_bot.py` is the reference for the command surface.

The existing bot should add one application-command group:

```text
/macro status
/macro strategies
/macro start <strategy>
/macro stop
/macro switch <strategy>
```

`/macro pair` may be omitted from the official bot because it is only a development fallback. Normal users should use **Connect Discord** OAuth onboarding.

Each handler must pass the real invoking account directly:

```python
interaction.user.id
```

to `MacroCommandController`. Do not accept a user ID, owner ID, or device ID as a slash-command option.

Responses should remain ephemeral for Remote operations, and arbitrary Agent exception text should not be rendered to Discord. The existing controller already returns sanitized `SafeReply` text.

### Example handler shape

If the official bot uses `discord.py` application commands, the integration can follow this pattern inside its existing tree/cog/module:

```python
macro = app_commands.Group(
    name="macro",
    description="Control a linked Ultimate Macro device",
)

@macro.command(name="status", description="Show Remote macro status")
async def remote_status(interaction: discord.Interaction):
    await interaction.response.defer(ephemeral=True, thinking=True)
    reply = await remote_controller.status(interaction.user.id)
    await interaction.edit_original_response(content=reply.content)
```

Use the matching controller method for the remaining commands:

```text
controller.strategies(user_id)
controller.start(user_id, strategy_id)
controller.stop(user_id)
controller.switch(user_id, strategy_id)
```

For START/SWITCH autocomplete, reuse:

```python
controller.autocomplete(interaction.user.id, current)
```

The complete tested reference callbacks are in `RemoteDiscordClient._register_commands()`.

## If the existing bot already has a `/macro` group

Do **not** create a second group with the same name.

Add the Remote subcommands to the existing `/macro` group, or choose non-conflicting subcommand names after an upstream UX decision.

Preserve any existing Ultimate Macro commands/features rather than replacing the whole group.

## Command synchronization

Use the official bot's existing command synchronization flow.

For a development guild, Discord recommends guild-scoped commands for fast iteration. For release, use whatever global/guild policy the official bot already follows.

Do not run the standalone `RemoteDiscordClient.setup_hook()` command sync against the official application unless the developer intentionally wants that isolated tree to define the whole command scope.

Discord application-command reference:

- https://docs.discord.com/developers/interactions/application-commands

## Existing bot permissions/intents

The Remote slash-command handlers themselves do not require privileged Gateway intents. The standalone preview client deliberately uses `discord.Intents.none()`.

When integrated upstream, keep the official bot's existing intents because its other features may require them. Remote does not require reducing or expanding those intents.

The Remote command group uses Discord interactions and does not need permission to read arbitrary message history.

## OAuth does not require a second bot user

The OAuth **Connect Discord** browser flow is user authentication against the same Discord application. It is separate from the bot user's Gateway session.

The same application can therefore provide both:

```text
Official Ultimate Macro bot
        +
/macro application commands
        +
Discord OAuth identify callback
```

while keeping one visible bot identity in the community server.

## Keep server secrets out of the macro/client

The official bot/backend server keeps:

```text
DISCORD_TOKEN
DISCORD_CLIENT_SECRET
SQLite central state
```

The packaged Windows client receives only:

```text
UltimateRemoteAgent.exe
remote_service.url   # public HTTPS origin only
macro client files
```

The device credential is generated during onboarding and stored locally with DPAPI CurrentUser; it is never baked into the ZIP.

## Recommended upstream integration sequence

1. Review `REVIEW_NOTES.md` and the Remote safety boundary.
2. Create a private development branch in the official bot/macro repository.
3. Reuse the official Discord application; add the Remote OAuth callback URI.
4. Add Remote environment settings to the official bot's secret/config system.
5. Initialize `RemoteStore`, `RemoteService`, `PairingService`, and `OnboardingService` once.
6. Start the Remote aiohttp routes alongside the existing bot runtime.
7. Instantiate `MacroCommandController` from the shared service.
8. Add the five Remote subcommands to the existing bot command tree.
9. Keep `/macro pair` development-only or remove it from the official user-facing tree.
10. Sync commands using the official bot's existing deployment workflow.
11. Build the Windows Agent/client package with the stable official Remote origin.
12. Test OAuth, status, strategies, START with the macro closed, safe SWITCH, safe STOP, reconnect/reconciliation, and Windows Agent autostart.
13. Only after acceptance, decide how to merge `Main_Remote.ahk` behavior into the normal upstream macro entry point/package.

## Temporary isolated testing

If the developer wants to test this repository exactly as-is before integrating it into the official bot code, the safest option is a **separate private development Discord application/token** and `run_bot.bat`.

Using the official bot's token in the standalone preview while the official bot is already online is not recommended.

## Files to review

- `central/discord_bot.py` — reference command group/controller and safe Discord rendering.
- `central/runtime.py` — current one-process composition.
- `central/server.py` — HTTP/WSS/OAuth routes and dependency injection.
- `central/service.py` — owner-safe dispatch and lifecycle.
- `central/store.py` — SQLite device/command state.
- `central/onboarding.py` — OAuth account linking.
- `docs/remote-server-r5.md` — central server setup.
- `docs/remote-protocol-v1.md` — Agent wire protocol.

The desired upstream result is **one Ultimate Macro bot, one official Discord application, one Remote backend, and one optional Windows Agent per linked installation** — not an extra public bot just for this feature.
