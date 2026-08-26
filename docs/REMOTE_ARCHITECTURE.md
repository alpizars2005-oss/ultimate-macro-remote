# Remote architecture and trust boundary

## Scope

This repository layers an opt-in Discord control bridge on top of an Ultimate Macro runtime while keeping a stock `Main.ahk` fallback beside `Main_Remote.ahk`.

The remote path is intentionally narrow:

```text
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
safe between-match command gate
```

There is no inbound HTTP server and no port-forwarding requirement in this design.

## Authorization layers

`bot.py` reads configuration from a local `.env` file and checks the requesting Discord user against `ALLOWED_USER_ID`. A private `GUILD_ID` can also scope slash-command synchronization to one Discord server.

These controls reduce accidental exposure but do not turn the Discord account, bot token, or local PC into trusted hardware. Anyone who obtains the bot token or an authorized Discord account may be able to submit commands with that identity.

## Command-file boundary

The Python bridge writes a complete command to a temporary file and then uses `os.replace()` to publish it as `remote_command.ini`. This keeps the handoff atomic from the Python process's point of view.

Gameplay-changing commands are consumed only by the safe command gate documented in `START_HERE.md`; they are not intended to interrupt `PlayStrategy()` mid-run.

## Strategy selection

The bot lists `.strat` files only from the configured `Resources/Strats` directory. Strategy resolution accepts an exact file/stem match or one unambiguous partial stem match.

Keep strategy files under the expected directory and treat strategies from untrusted sources as untrusted input. The remote bridge should not be used as a general-purpose path launcher.

## Secrets

- `.env` is local-only and must never be committed.
- `.env.example` contains placeholders only.
- `DISCORD_TOKEN` should be rotated immediately if exposed.
- Do not put tokens into screenshots, logs, issues, strategy files, or Discord messages.

## Fallback and rollback

`Main.ahk` remains the clean fallback. Remote work lives in `Main_Remote.ahk` and the Python bridge so a remote-layer problem can be isolated without overwriting the stock runtime.

Before live testing changes to remote command handling, use a short strategy and verify `/macro status` and `/macro strategies` before issuing gameplay-changing commands.
