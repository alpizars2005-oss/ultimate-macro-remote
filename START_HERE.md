# Ultimate Macro Remote — V2 for 1.3.2a

This patch is rebased on the actual packaged Ultimate Macro **1.3.2a** `Main.ahk`.

## Important

Use these files with the extracted **TDS_Macro (1).zip** release folder.

Do **not** replace your original `Main.ahk`.
Keep it as a clean fallback and add `Main_Remote.ahk` beside it.

## Included commands

- `/macro status`
- `/macro start <strategy>`
- `/macro switch <strategy>`
- `/macro stop`
- `/macro cancel`
- `/macro strategies`

`switch` and `stop` are consumed only at the safe between-match boundary, before the macro
runs Restart / Play Again / rejoin logic. They are not consumed in the middle of `PlayStrategy()`.

## Install

1. Extract your normal Ultimate Macro 1.3.2a release.
2. Copy every file from this V2 ZIP into that folder.
3. Run `setup_bot.bat`.
4. Edit `.env`:
   - `DISCORD_TOKEN`
   - `ALLOWED_USER_ID`
   - `GUILD_ID`
5. Run `run_bot.bat`.
6. Test `/macro status`.
7. Test `/macro strategies`.
8. For the first live test, use a short/safe strategy and then try `/macro switch`.
9. Keep `Main.ahk` untouched so you can immediately fall back to the stock macro.

## Discord bot setup

Create a private Discord application/bot in the Discord Developer Portal.
Invite it to your private test server with the `bot` and `applications.commands` scopes.

The bot is additionally locked to `ALLOWED_USER_ID`, so commands from other Discord users
are rejected.

Never share the bot token or commit `.env` to GitHub.

## Architecture

Phone -> Discord slash command -> bot.py -> local `remote_command.ini` ->
Main_Remote.ahk -> safe between-match command gate -> Roblox/TDS

No inbound port forwarding is required.
