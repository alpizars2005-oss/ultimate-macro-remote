# Ultimate Macro Remote — R2 development setup

This branch keeps the packaged Ultimate Macro **1.3.2a** gameplay implementation and
adds a central Remote backend. Keep `Main.ahk` as the stock fallback. R2 does not edit
`Main.ahk`, `Main_Remote.ahk`, the watchdog, or any timing-sensitive gameplay code.

## What R2 contains

The central Discord bot exposes five protocol-1 controls:

- `/macro status`
- `/macro strategies`
- `/macro start <strategy>`
- `/macro stop`
- `/macro switch <strategy>`

`/macro pair` is a separate, temporary development-enrollment command. It creates a
256-bit, short-lived, single-use ticket bound to the invoking Discord account. There is
no `/macro cancel`, arbitrary command execution, filename/path input, manually entered
Discord user ID, or device-ID selection.

R2 does **not** contain the Windows Agent. The control commands are exercised with the
simulated Agent integration tests for now; they do not yet control a second PC. Do not
copy `.env`, the Discord bot token, or central backend state to a client PC.

## Central development setup (PC A)

1. Run `setup_bot.bat` to create `.venv` and install `requirements.txt`.
2. Edit the untracked `.env` on PC A:
   - set `DISCORD_TOKEN`;
   - optionally set `DISCORD_GUILD_ID` for immediate development-guild command sync;
   - set `ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN` only when a trusted HTTPS/WSS origin is
     available.
3. Create/invite a private Discord bot with the `bot` and `applications.commands`
   scopes.
4. Run `run_bot.bat`. This starts the aiohttp backend and Discord client in one process,
   sharing one SQLite store and `RemoteService`.
5. Run the test suite with
   `.venv\Scripts\python.exe -B -m unittest discover -s tests -v`.

The backend defaults to literal loopback. For traffic from another PC, terminate TLS
with a trusted certificate, preserve both the `Authorization` and WebSocket Upgrade
headers, and redact authorization headers and pairing response bodies from proxy logs.
Do not expose raw loopback HTTP directly to a network.

## Development pairing flow

1. The user invokes `/macro pair`; `interaction.user.id` is the only identity input.
2. Discord returns the ticket ephemerally and with all mentions disabled.
3. A simulator sends an empty-body `POST /remote/v1/pair` with
   `Authorization: Pairing <ticket>` over trusted HTTPS. Literal loopback HTTP is
   permitted only for a simulator running on the central PC.
4. Central atomically consumes the hashed ticket and creates the existing R1 device
   credential. The credential is returned once and SQLite stores only its hash.
5. The simulator uses that credential as `Authorization: Bearer …` on the unchanged
   `/remote/v1/agent` WebSocket.

Unknown, expired, redeemed, superseded, and owner-conflicting tickets share the same
public redemption failure. A lost successful HTTP response cannot be replayed; the
orphan device must be explicitly revoked before pairing again.

## Current architecture

```text
Discord interaction.user.id ----> central Discord commands
             |                              |
             +---- /macro pair              v
                       |                RemoteService ---- SQLite
                       v                     |
                hashed one-use ticket        | authenticated WSS
                       |                     v
                 HTTPS redemption       simulated Agent (R2)

Windows Agent -> local AHK bridge -> Ultimate Macro -> Roblox
    (future R3/R4; not implemented in R2)
```

For the exact security and wire contracts, see
[`docs/remote-pairing-v1.md`](docs/remote-pairing-v1.md) and
[`docs/remote-protocol-v1.md`](docs/remote-protocol-v1.md).
