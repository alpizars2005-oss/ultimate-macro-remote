# Ultimate Remote architecture and milestone plan

## Inspected baseline

The branch was clean before R2 at `01648e5` on
`feature/ultimate-remote-agent`. R1 is commit `07a751e` and remains based on the
validated watchdog boundary in `5a48792`.

`Main_Remote.ahk` already consumes START during startup and consumes STOP/SWITCH at the
between-match safe gate after its initial disconnect/reconnect check and before the main
restart/join/equip flow. Reconnect recovery can therefore delay a queued safe command.
Its mailbox is a single UTF-16
INI slot and its state/result file is under the interactive user's `%APPDATA%`. This
milestone does not edit `Main.ahk`, `Main_Remote.ahk`, `submacros/watchdog.ahk`, or any
gameplay loop.

## Architecture after R2

```text
Discord bot (central only)
        |
        | in-process API using interaction.user.id
        v
RemoteService ---- SQLite device/owner/command state
        |
        | authenticated WSS /remote/v1/agent
        v
simulated Agent (R1/R2 tests)

/macro pair -> PairingService -> hashed, expiring ticket in SQLite
                                      |
                                      | empty-body HTTPS POST /remote/v1/pair
                                      v
                         one-time R1 device credential

UltimateRemoteAgent -> local AHK bridge -> Ultimate Macro -> Roblox
       (future)          (existing safe gate)
```

R1 supplies the strict protocol, authenticated outbound-Agent endpoint, connection and
heartbeat state, revocable hashed credentials, durable owner/device association,
ownership-safe dispatch, one-mutating-command gate, lifecycle correlation, and a
simulated-Agent integration test. `RemoteService.dispatch_for_user` takes the
authoritative Discord user ID and has no device-ID parameter.

R2 adds a central-only Discord adapter and an isolated temporary development-pairing
API. The five control commands pass `interaction.user.id` to `RemoteService` and accept
no user or device selector. `/macro pair` binds a 256-bit ticket to that same identity;
only the ticket digest is stored. Redemption is rate-limited and atomically consumes
the ticket while creating the existing R1 device bearer. `RemoteStore.provision_device`
remains an internal test primitive and is not exposed by HTTP or Discord.

For local backend development after installing `requirements.txt`, run
`python -m central.server`. It binds `127.0.0.1:8765`, stores state under
`runtime/central`, and exposes `/healthz`, the authenticated Agent WebSocket, and the
ticket-redemption endpoint. It does not run the Discord ticket issuer. Run `bot.py` (or
`python -m central.runtime`) for the single-process R2 Discord + HTTP runtime.
The store holds a process-lifetime `remote.db.lock` and refuses a second backend writer;
the SQLite directory is local to PC A and must not be shared over a network filesystem.
The simulated-Agent integration suite is the supported R1 exercise path.

## R2 security boundary

The Discord client is configured with `AllowedMentions.none()`, every command response
is ephemeral, and Agent strategy names are treated as untrusted display text: control
characters are removed, Discord mentions and markdown are escaped, output is bounded,
and autocomplete submits only opaque strategy IDs. Agent `error_message` values are
never rendered. Stable error codes map to fixed user-facing text; unknown codes get a
generic message. The legacy local bot behavior, `/macro cancel`, local strategy paths,
AHK launch, and manually configured allowed-user ID are absent from the central runtime.

Pairing is outside protocol 1 and can later be replaced by OAuth without changing the
Agent command schema. Tickets use 256 random bits, expire after ten minutes by default,
are single-use and hash-only in SQLite, supersede older live tickets, and are limited
per Discord owner, direct socket peer, and globally. Redemption accepts the ticket only
in `Authorization: Pairing …`; it accepts no body/query identity data and returns the
device bearer once with no-store headers. See `remote-pairing-v1.md`.

## Smallest remaining vertical-slice milestones

### R3 — self-contained Windows Agent transport and read-only bridge

Create `UltimateRemoteAgent/UltimateRemoteAgent.csproj` and source folders for
transport, credential storage, strategy catalog, process/state inspection, and tests.
Target `net10.0-windows` and publish `win-x64` self-contained/single-file so PC B needs
no .NET runtime, Python, editor, or bot token. Use DPAPI CurrentUser or Windows
Credential Manager for the device bearer, one instance per interactive user, normal
certificate validation, outbound WSS, full-jitter reconnect backoff, low-frequency
heartbeat, and no OCR/pixel or busy polling. Prove GET_STATUS and LIST_STRATEGIES first.

### R4 — fixed local START/STOP/SWITCH adapter

Add the Agent's canonical fixed `Main_Remote.ahk` launcher, atomic UTF-16 mailbox adapter,
durable command journal/reconciliation, exact process identity checks, and approved-root
strategy resolver. Translate IDs to validated local paths only after extension,
containment, UNC/device/traversal, duplicate, and reparse checks. Serialize the one-slot
mailbox. Map matching AHK results conservatively as defined in protocol 1. Do not edit
or poll inside `PlayStrategy`, SpawnTower, UpgradeTower, ability/recorded timing, or any
gameplay-sensitive loop.

START is startup-only and `#SingleInstance Force` can replace a live macro. The Agent
must fail START when gameplay is active and direct the user to SWITCH. It may reuse or
restart an exact `Main_Remote.ahk` process only after positive idle confirmation; stale
`state.ini` alone is insufficient, and watchdog PID handoff needs grace/debounce. It
must never overwrite an occupied one-slot mailbox.

The current AHK `start_accepted` result is too early to prove START completion. Until a
later lifecycle marker is validated, the Agent must leave START in `executing`. R4 must
implement and manually validate `strategy_started` before R5: observe a new nonzero
`State.TimeWhenStartedPlaying` written after this START reset, correlate the exact
command/process/strategy, and require Roblox running. Do this externally with a
FileSystemWatcher/debounce or low-rate fallback; do not add polling to gameplay code.

### R5 — consent, logon start, packaging, and two-PC acceptance

Add the optional onboarding wording and controls, a per-user logon-start method that is
easy to disable/uninstall, and packaging. Logon starts only the Agent; it never launches
Ultimate Macro, Roblox, or a strategy until a Remote command is received. Run the full
PC-A/PC-B acceptance checklist, including safe deferred switch/stop.

Replace temporary pairing with Discord OAuth when the infrastructure gate below is
satisfied.

## Dependencies

- R1 adds direct `aiohttp>=3.13,<4` use. SQLite, JSON, hashing, secrets, TLS, and tests use
  the Python standard library.
- R2 reuses `discord.py` and `python-dotenv`, already declared.
- R3/R4 use the installed .NET 10 SDK and Windows framework APIs. The release is
  self-contained; no runtime is installed on PC B. Avoid a WMI package until exact
  process identity requirements justify it.

## TLS, hosting, and OAuth infrastructure gate

The backend defaults to `127.0.0.1` plaintext solely so a local reverse proxy can
terminate TLS. A two-PC test still needs a stable, PC-B-reachable WSS/HTTPS origin with
a trusted certificate. A provider-neutral secure tunnel is acceptable for development,
provided it supports WebSocket upgrades and preserves the Authorization header. Core
logic does not select a tunnel provider.

The safest final identity flow is Discord's authorization-code flow with only the
`identify` scope, a one-time CSRF `state`, exact registered redirect URI validation,
server-side code exchange and `/users/@me` lookup, then immediate token discard/revoke
after the Discord snowflake is persisted. The client secret remains only on PC A.
Discord's official OAuth documentation describes the exact redirect and `state`
requirements: <https://docs.discord.com/developers/topics/oauth2>.

That OAuth implementation is not attempted in R2 because this workspace has no stable
public HTTPS base URL, trusted TLS termination, registered Discord redirect URI, OAuth
client ID/secret configuration, or WebSocket-capable public routing. Do not silently
infer identity from guild membership, usernames, or a manually typed Discord ID.

## Security assumptions

- PC A, its OS account, SQLite file, environment, and TLS terminator are trusted.
- A random device bearer authenticates one installation; server compromise can issue
  only protocol-allowlisted operations, not arbitrary execution.
- Future DPAPI/Credential Manager storage protects against another Windows account or
  offline copying, not malware already running as the same user or a local administrator.
- The current AHK mailbox is unauthenticated local same-user IPC. The Agent must validate
  every network command and path before writing it.
- Discord account compromise grants the attacker the five allowlisted macro operations
  for that account's linked device, but no shell, file, credential, or desktop access.
- When delivery outcome is ambiguous, availability yields to safety: the command enters
  `reconciling`, is not replayed, and blocks conflicting gameplay-changing commands.
