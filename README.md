# Ultimate Macro Remote — private development extension

This repository contains a private development extension for **Ultimate Macro**, the Tower Defense Simulator macro originally created by **DarksenDev**. The branch adds an optional Discord Remote layer without turning the macro into a general-purpose remote-access tool and without putting network work into timing-sensitive gameplay paths.

> **Status:** private R5 development preview. This is not an official public release and must not be presented as an upstream 1.3.4 Remote build until the AutoHotkey runtime rebase is separately verified.

## Current Remote flow

The Remote system consists of a central Discord/backend service, a self-contained Windows Agent, and a Remote-aware AutoHotkey entry point.

Supported Remote controls are currently designed around:

- `/macro link <code>` — bot integration handoff for one-time device linking
- `/macro status`
- `/macro strategies`
- `/macro start <strategy>`
- `/macro stop`
- `/macro switch <strategy>`

The **default packaged enrollment is now macro-first and does not use a website**:

1. Run `Main_Remote.ahk` from a packaged client.
2. Review and explicitly accept the Remote preview notice.
3. Choose **Generate Link Code**.
4. Ultimate Macro shows a top-most code such as `ULT-7KQ3M-P9R2X` plus the exact `/macro link ...` command.
5. Run that command in the official Ultimate Macro Discord server.
6. The bot uses the authenticated `interaction.user.id` as the owner and claims the code through the shared central `LinkingService`.
7. The Agent receives the device credential, stores it with Windows DPAPI CurrentUser protection, closes the popup, and can optionally start with that Windows account.

The client never asks the user to type a Discord user ID, bot token, OAuth client secret, Agent credential, `.env` value, or backend database information.

See [`docs/discord-link-code-contract.md`](docs/discord-link-code-contract.md) for the exact bot handoff Yoshi can implement.

## Link-code formula

Display codes use:

```text
alphabet = 23456789ABCDEFGHJKLMNPQRSTUVWXYZ
symbols  = 10
format   = ULT-XXXXX-XXXXX
```

The reference service selects all ten symbols with Python's cryptographic `secrets` source. A 32-symbol alphabet with 10 independent symbols gives 50 bits of entropy. `0`, `1`, `I`, and `O` are intentionally excluded.

Codes are short-lived (10 minutes by default), single-use for ownership, and rate-limited before lookup. The short code is **not** the long-term device credential. Discord identity comes only from the authenticated bot interaction; the macro cannot submit or choose an owner ID.

## Existing official Discord bot can be reused

Remote does **not** require a second public Discord bot.

The preferred upstream integration is to reuse the existing official Ultimate Macro bot/application and add the Remote command handlers to that deployment. The new link handler only needs to pass the authenticated Discord user ID and the submitted code to the shared central linking service.

Reference integration:

```python
await linking_service.claim(interaction.user.id, code)
```

If the official bot runs outside the Remote central process, expose that operation through an authenticated private service-to-service adapter. Do not create a public unauthenticated claim endpoint that accepts arbitrary Discord IDs.

The previous OAuth onboarding code and the legacy `/macro pair` development ticket remain in-tree only as rollback/reference paths; they are no longer the normal packaged setup UX.

## Safety model

Remote is deliberately allowlisted. The Agent does **not** expose arbitrary shell/CMD/PowerShell execution, remote desktop, a general file browser, arbitrary process launch, arbitrary executable paths, or download-and-execute behavior.

`START_STRATEGY` can launch only the bundled `submacros\AutoHotkey64.exe` with the fixed local `Main_Remote.ahk` script and a strategy resolved from the approved `Resources\Strats` catalog.

`STOP_SAFE` and `SWITCH_STRATEGY` do not interrupt `PlayStrategy()`, tower placement, upgrades, abilities, recorded Click/Send/Sleep steps, or other timing-sensitive gameplay operations. They are queued through the existing local Remote mailbox and applied only when the macro reaches its validated between-match safe boundary.

Gameplay-changing commands use a durable local journal and a fail-closed reconciliation model so a lost connection does not cause an ambiguous mutation to be replayed automatically.

## Architecture

```text
Ultimate Macro startup
        |
        v
Windows Agent ---- HTTPS ----> LinkingService
        |                           ^
        | shows ULT code            |
        v                           |
User runs /macro link CODE          |
        |                           |
        v                           |
Official Discord bot ---------------+
  owner = interaction.user.id

After enrollment:

Discord Remote command
        |
        v
Central RemoteService + SQLite
        |
        | authenticated WSS
        v
UltimateRemoteAgent.exe
        |
        +---- status + approved strategy catalog
        |
        +---- fixed local mutation bridge
                        |
                        v
                 Main_Remote.ahk
                        |
                        v
                    Roblox / TDS
```

The current milestone intentionally supports one active linked device per Discord account. Multi-device selection is not implemented yet.

## Repository layout

- `Main.ahk` — upstream/stock fallback entry point retained in this development repository.
- `Main_Remote.ahk` — Remote-aware macro entry point and safe-boundary mailbox consumer.
- `UltimateRemoteAgent/` — C#/.NET Windows Agent, link client, transport, local bridge, DPAPI enrollment, and tests.
- `central/linking.py` — short-code session generation, Discord claim binding, expiry, and rate limiting.
- `central/` — WebSocket backend, Remote command service, legacy bot reference, legacy OAuth/pairing code, and SQLite store.
- `docs/discord-link-code-contract.md` — exact official-bot integration contract.
- `tools/` — local client staging and preview packaging scripts.
- `.env.example` — server-only configuration template. The real `.env` remains private and untracked.

## Development verification

Central tests:

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

GitHub Actions runs the Python suite, .NET build/tests/formatting, self-contained Windows publish, and a Windows PowerShell 5.1 package smoke test.

## Packaging

A normal client package contains the macro files, published `UltimateRemoteAgent.exe`, the public `remote_service.url`, `REMOTE_README.txt`, and the bot linking contract. It must never contain the real `.env`, Discord bot token, OAuth client secret, backend database, setup secret, device credential, or development Python environment.

After publishing the Agent:

```powershell
.\tools\package_remote_preview.ps1
```

The packager reads `ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN` from the local server `.env` unless an explicit test origin is provided.

## Compatibility boundary

The **link-code protocol and Windows Agent integration** are the deliverable for the current bot handoff. The large `Main_Remote.ahk` gameplay entry point on this private branch has its own compatibility baseline. Do not silently label the entire package as Ultimate Macro `1.3.4` until that AutoHotkey runtime is rebased against the official 1.3.4 source and its gameplay/safe-boundary behavior is retested.

That separation is intentional: Yoshi can implement the official bot linking command against a stable contract without pretending the unrelated gameplay-runtime rebase is already complete.

## Current preview limitations

- Private development preview; formal public Terms/Privacy text is not finished.
- One linked device per Discord account; no device selector yet.
- Link sessions are currently process-local. A central restart during the short enrollment window requires generating a new code.
- A claimed but unacknowledged session is revoked on expiry rather than leaving an orphan credential.
- Remote strategy discovery is intentionally limited to approved local `.strat` files.
- A production deployment needs a stable trusted HTTPS/WSS hostname and normal operational hardening.
- Public redistribution requires upstream approval and a separate review of bundled third-party assets/binaries and licensing.

## Upstream project and credit

Ultimate Macro was created by **DarksenDev**. This Remote work is an experimental contribution built around the existing macro and its safety boundaries, not a replacement project. Original-creator attribution and GPL-3.0 obligations must be preserved in any integration or redistribution.

- Upstream GitHub: [DarksenDev/tds-macro](https://github.com/DarksenDev/tds-macro)
- Discord: [Ultimate Macro community](https://discord.gg/DQnc2JDJtr)
- YouTube: [@darksenn](https://www.youtube.com/@darksenn)

See [`LICENSE`](LICENSE) for the repository license text.
