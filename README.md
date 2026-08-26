# Ultimate Macro Remote

Remote-control extension for **Ultimate Macro / Tower Defense Simulator** that keeps the original macro available as a fallback and adds an opt-in Discord command bridge.

This repository contains a rebased `Main_Remote.ahk` plus a small Python Discord bot. The remote layer is deliberately separated from the stock `Main.ahk` so testing or rolling back the extension does not require replacing the original runtime.

> Start with [`START_HERE.md`](START_HERE.md) for the tested installation flow and compatibility baseline.

## What this repository adds

- `/macro status`
- `/macro start <strategy>`
- `/macro switch <strategy>`
- `/macro stop`
- `/macro cancel`
- `/macro strategies`
- private Discord user authorization through `ALLOWED_USER_ID`
- optional guild-scoped slash-command registration
- atomic local command-file handoff
- safe consumption of gameplay-changing commands at the between-match boundary
- stock `Main.ahk` kept beside `Main_Remote.ahk` as a rollback path

## Architecture

```text
Phone / Discord
      |
      v
Discord slash command
      |
      v
    bot.py
      |
      v
%APPDATA%/Ultimate_Macro/remote_command.ini
      |
      v
Main_Remote.ahk
      |
      v
safe between-match gate
      |
      v
Ultimate Macro / Roblox TDS
```

No inbound web server or router port forwarding is required by this design. See [`docs/REMOTE_ARCHITECTURE.md`](docs/REMOTE_ARCHITECTURE.md) for the trust boundary and rollback model.

## Requirements

- Windows 10/11
- Ultimate Macro compatibility baseline documented in `START_HERE.md`
- AutoHotkey v2 runtime used by the macro distribution
- Python 3.11+ for the Discord bridge
- a private Discord application/bot

Python dependencies are intentionally small:

```text
discord.py >=2.6,<3
python-dotenv >=1,<2
```

## Setup

1. Keep the original `Main.ahk` unchanged.
2. Place the remote files beside the extracted Ultimate Macro files as described in `START_HERE.md`.
3. Run `setup_bot.bat`.
4. Copy `.env.example` to `.env` and fill in your own `DISCORD_TOKEN`, `ALLOWED_USER_ID`, and `GUILD_ID`.
5. Run `run_bot.bat`.
6. Verify `/macro status` and `/macro strategies` before testing gameplay-changing commands.
7. Use a short/safe strategy for the first live remote test.

Never commit `.env` or share the bot token. See [`SECURITY.md`](SECURITY.md).

## Safety model

The remote bridge is not a general-purpose remote shell. Strategy discovery is restricted to the configured `Resources/Strats` directory, and gameplay-changing requests are queued for the macro's safe command boundary rather than injected into `PlayStrategy()` mid-run.

The Discord account, authorized user account, bot token, local PC, and strategy files remain trust dependencies. Use a private test guild and rotate any exposed bot token immediately.

## Automated checks

GitHub Actions now validates the remote layer on supported Python versions by:

- installing the declared dependencies;
- running `pip check`;
- compiling `bot.py` without connecting to Discord;
- verifying `.env.example` contains the required placeholder configuration;
- confirming both the stock and remote AHK entrypoints remain present.

The CI intentionally does **not** execute Roblox, AutoHotkey gameplay, or live Discord commands.

## Upstream project

Ultimate Macro is an existing community project. The base project and its original documentation/support links belong to their respective maintainers. This repository focuses on the remote-control extension and keeps upstream attribution/compatibility concerns separate from the local bridge.

For upstream Ultimate Macro information, see the project referenced in the original distribution: **DarksenDev/tds-macro**.

## License

See [`LICENSE`](LICENSE) and the licensing terms inherited from the repository's upstream base. Changes in this repository do not remove upstream notices or attribution obligations.
