# Ultimate Macro Remote — private development extension

This repository contains a private development extension for **Ultimate Macro**, the Tower Defense Simulator macro by DarksenDev. The goal of this branch is to add an optional Discord-based Remote layer without turning the macro into a general-purpose remote-access tool and without interrupting timing-sensitive gameplay logic.

> **Status:** private R5 development preview. This is not an official public release, not production-hardened infrastructure, and not intended for redistribution without the upstream project owner's approval.

## What this branch adds

The Remote system consists of a central Discord/backend service, a self-contained Windows Agent, and a Remote-aware AutoHotkey entry point.

Supported Discord controls are:

- `/macro status`
- `/macro strategies`
- `/macro start <strategy>`
- `/macro stop`
- `/macro switch <strategy>`

The intended end-user setup is one-time:

1. Run `Main_Remote.ahk` from a packaged client.
2. Review and explicitly accept the Remote preview notice.
3. Choose **Connect Discord**.
4. Authorize the Discord application with the `identify` scope.
5. The Agent stores its device enrollment with Windows DPAPI CurrentUser protection and can optionally start with that Windows account.

After enrollment, users normally control the macro from Discord. They do not need Python, VS Code, a bot token, a Discord user ID, a pairing ticket, an `.env` file, or a backend URL to type manually.

## Safety model

Remote is deliberately allowlisted. The Agent does **not** expose arbitrary shell/CMD/PowerShell execution, remote desktop, a general file browser, arbitrary process launch, arbitrary executable paths, or download-and-execute behavior.

`START_STRATEGY` can launch only the bundled `submacros\AutoHotkey64.exe` with the fixed local `Main_Remote.ahk` script and a strategy resolved from the approved `Resources\Strats` catalog.

`STOP_SAFE` and `SWITCH_STRATEGY` do not interrupt `PlayStrategy()`, tower placement, upgrades, abilities, recorded Click/Send/Sleep steps, or other timing-sensitive gameplay operations. They are queued through the existing local Remote mailbox and are applied only when the macro reaches its validated between-match safe boundary.

Gameplay-changing commands use a durable local journal and a fail-closed reconciliation model so a lost connection does not cause an ambiguous mutation to be replayed automatically.

## Architecture

```text
Discord slash command
        |
        v
Central Discord bot / RemoteService
        |
        +---- SQLite owner/device/command state
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

Discord identity is authoritative from `interaction.user.id` for commands and from Discord OAuth `identify` during normal onboarding. The client cannot submit an owner ID or select an arbitrary device.

The current milestone intentionally supports one active linked device per Discord account. Multi-device selection is not implemented yet.

## Repository layout

- `Main.ahk` — upstream/stock fallback entry point.
- `Main_Remote.ahk` — Remote-aware macro entry point and safe-boundary mailbox consumer.
- `UltimateRemoteAgent/` — C#/.NET Windows Agent, transport, local bridge, enrollment, and tests.
- `central/` — Discord bot, OAuth onboarding, WebSocket backend, command service, and SQLite store.
- `docs/` — protocol, architecture, server, Agent, onboarding/fallback, and preview documentation.
- `tools/` — local client staging and zero-config preview packaging scripts.
- `.env.example` — server-only configuration template. The real `.env` must remain private and untracked.

## Reviewer quick start

Start with [`REVIEW_NOTES.md`](REVIEW_NOTES.md), then [`START_HERE.md`](START_HERE.md).

The most useful technical references are:

- [`docs/remote-architecture.md`](docs/remote-architecture.md)
- [`docs/remote-protocol-v1.md`](docs/remote-protocol-v1.md)
- [`docs/remote-preview-r5.md`](docs/remote-preview-r5.md)
- [`docs/remote-server-r5.md`](docs/remote-server-r5.md)
- [`docs/windows-agent-r5.md`](docs/windows-agent-r5.md)
- [`docs/remote-pairing-v1.md`](docs/remote-pairing-v1.md) — legacy development fallback only

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

The GitHub Actions workflow runs the central suite, .NET build/tests/formatting, self-contained publish, and a Windows PowerShell 5.1 client-package smoke test.

## Packaging

A normal client package contains the macro files, the published `UltimateRemoteAgent.exe`, and a public `remote_service.url`. It must never contain the real `.env`, Discord bot token, OAuth client secret, backend database, device credential, or development Python environment.

After publishing the Agent:

```powershell
.\tools\package_remote_preview.ps1
```

The packager reads `ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN` from the local server `.env` unless an explicit test origin is provided.

## Current preview limitations

- Private development preview; formal public Terms/Privacy text is not finished.
- One linked device per Discord account; no device selector yet.
- Remote strategy discovery is intentionally limited to top-level `.strat` files in `Resources\Strats`.
- A temporary Cloudflare Quick Tunnel is suitable only for development. A production deployment needs a stable trusted HTTPS/WSS hostname and normal operational hardening.
- OAuth onboarding session state is currently process-local. A central-process failure during the narrow post-OAuth/pre-completion window may require operator cleanup of a newly provisioned offline device before retrying. This must be hardened before public production deployment.
- Public redistribution requires upstream approval and a separate review of bundled third-party assets/binaries and licensing.

## Upstream project and credit

Ultimate Macro is developed by DarksenDev. This Remote work is an experimental contribution built around the existing macro and its safety boundaries, not a replacement project.

- Upstream GitHub: [DarksenDev/tds-macro](https://github.com/DarksenDev/tds-macro)
- Discord: [Ultimate Macro community](https://discord.gg/DQnc2JDJtr)
- YouTube: [@darksenn](https://www.youtube.com/@darksenn)

See [`LICENSE`](LICENSE) for the repository license text. Any integration or redistribution should be reviewed by the upstream project owner first.
