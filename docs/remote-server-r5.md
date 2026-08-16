# Ultimate Macro Remote — R5 central server setup

This document is for the central Remote operator/developer. It is **not** copied to client PCs.

## Compatibility model

`ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN` is shared by the central backend, development pairing, Discord OAuth onboarding, Agent WSS, and the zero-config client package.

- Public origin only: R2/R3 development `/macro pair` remains usable and browser OAuth is disabled.
- `DISCORD_CLIENT_ID` + `DISCORD_CLIENT_SECRET` + public origin: R5 **Connect Discord** onboarding is enabled.
- Only one OAuth credential: configuration fails closed.

The client ZIP should always be built from the same current public origin as the central `.env`.

## Central `.env`

Keep the real `.env` private and untracked. Minimum R5 fields:

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

Never copy the bot token, OAuth client secret, `.env`, database, or central Python environment into the client package.

## Discord OAuth redirect

Register exactly this Redirect URI in the same Discord application:

```text
https://YOUR_REMOTE_HOST/remote/v1/onboarding/discord/callback
```

The central preflight prints the exact URI without printing any secret values.

## Safe preflight

```powershell
.\.venv\Scripts\python.exe -m central.preflight
```

To require the R5 browser onboarding path:

```powershell
.\.venv\Scripts\python.exe -m central.preflight --require-oauth
```

`run_bot.bat` automatically performs the normal preflight before starting the backend and Discord bot.

## Start central runtime

```powershell
.\run_bot.bat
```

A successful R5 startup reports that Discord OAuth onboarding is enabled and prints the configured callback URI.

## Build the matching zero-config client

After publishing the Windows Agent, run:

```powershell
.\tools\package_remote_preview.ps1
```

When `-PublicOrigin` is omitted, the packager reads only `ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN` from the central `.env`. The resulting ZIP embeds that exact origin in `remote_service.url`; server-only files remain excluded.

An explicit origin remains available for CI/testing:

```powershell
.\tools\package_remote_preview.ps1 -PublicOrigin https://remote.example
```

## Cloudflare Quick Tunnel development

Quick Tunnel URLs are temporary. If `cloudflared` restarts and gives a new `*.trycloudflare.com` hostname, all three development surfaces must move together:

1. Update `ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN` in `.env`.
2. Replace the Discord OAuth Redirect URI with the callback under that new hostname.
3. Restart `run_bot.bat` and rebuild the client ZIP from the same `.env` origin.

A stable production hostname removes this Quick Tunnel maintenance entirely.
