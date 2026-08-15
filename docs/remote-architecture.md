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

## Architecture after R3

```text
Discord bot (central only)
        |
        | in-process API using interaction.user.id
        v
RemoteService ---- SQLite device/owner/command state
        |
        | authenticated WSS /remote/v1/agent
        v
UltimateRemoteAgent (R3)
        |
        +-- exact process/state inspection (read-only)
        +-- approved Resources\Strats catalog (read-only)

/macro pair -> PairingService -> hashed, expiring ticket in SQLite
                                      |
                                      | empty-body HTTPS POST /remote/v1/pair
                                      v
                         one-time R1 device credential

UltimateRemoteAgent -> local mutation bridge -> Ultimate Macro -> Roblox
     (R3 transport)         (future R4)          (existing safe gate)
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

R3 adds the real Windows transport and a read-only local bridge. The self-contained
`win-x64` Agent stores its entire enrollment envelope under the interactive user's
LocalAppData using DPAPI CurrentUser, opens only an outbound authenticated WSS
connection with normal Windows certificate validation, advertises only GET_STATUS and
LIST_STRATEGIES, sends low-frequency protocol heartbeats, and reconnects with capped
exponential full jitter. It parses reconciliation IDs as metadata and never treats them
as commands.

Status requires a successful WMI census of the fixed bundled AutoHotkey executable and
exact `Main_Remote.ahk` argument, then rechecks PID/creation identity after reading the
bounded UTF-16 state file. Stale `Running=1` cannot prove a live macro. Strategy listing
is top-level `.strat` only under the fixed approved root, with handle-resolved local
containment, reparse/network/traversal/duplicate rejection, and path-free opaque IDs.
The Agent contains no launcher, mailbox writer, command journal, OCR, image scanner,
startup persistence, or local START/STOP/SWITCH implementation.

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
- R3 targets `net10.0-windows`. Microsoft `System.Management` supplies the bounded WMI
  command-line census needed to distinguish the exact AHK script, and
  `System.Security.Cryptography.ProtectedData` supplies DPAPI CurrentUser. The
  `win-x64` single-file release is self-contained; no runtime is installed on PC B.

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
- DPAPI CurrentUser storage protects against another Windows account or
  offline copying, not malware already running as the same user or a local administrator.
- The current AHK mailbox is unauthenticated local same-user IPC. The Agent must validate
  every network command and path before writing it.
- Discord account compromise grants the attacker the five allowlisted macro operations
  for that account's linked device, but no shell, file, credential, or desktop access.
- When delivery outcome is ambiguous, availability yields to safety: the command enters
  `reconciling`, is not replayed, and blocks conflicting gameplay-changing commands.
