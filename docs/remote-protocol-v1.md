# Ultimate Macro Remote — protocol 1

Protocol 1 is a closed JSON protocol between the central Remote backend and one authenticated `UltimateRemoteAgent` installation. It is intentionally **not** a general remote-control protocol.

## Transport and authentication

- The Agent makes an outbound WebSocket connection to `/remote/v1/agent`.
- Cross-machine use requires normally trusted `wss://` transport.
- `ws://` is permitted only on a literal loopback address for development.
- The HTTP upgrade carries `Authorization: Bearer <device credential>`.
- The device credential never appears in a URL or JSON protocol message.
- A server-generated device credential is unique to an installation, random, revocable, and returned to the Agent once. SQLite stores its digest rather than the plaintext bearer.
- The credential identifies the device. Protocol messages cannot override that identity with a caller-supplied device ID.
- Every protocol envelope contains `"protocol": 1`.
- Messages are UTF-8 JSON text and bounded to 64 KiB. Binary messages, duplicate JSON keys, missing required fields, unknown fields, and invalid enum values are rejected.

Normal R5 enrollment obtains the device credential through the separate Discord OAuth onboarding flow. The legacy `/macro pair` path is also outside protocol 1 and exists only as a development fallback. Neither enrollment mechanism changes HELLO/WELCOME/COMMAND schemas.

## Identity model

Discord ownership is not transported in protocol-1 messages.

For normal slash commands, central already knows the authoritative caller from `interaction.user.id` and selects the linked device server-side.

For normal enrollment, central learns the owner through Discord OAuth `identify`; the Windows client does not submit a Discord owner ID.

The current milestone expects one active linked device per Discord account. Device selection is not a protocol-1 feature yet.

## Operation allowlist

Protocol 1 supports exactly:

- `GET_STATUS`
- `LIST_STRATEGIES`
- `START_STRATEGY`
- `STOP_SAFE`
- `SWITCH_STRATEGY`

The Agent version used by the current R5 implementation reports `0.5.0` and advertises all five operations.

There is no protocol operation for:

- generic `EXEC`;
- shell, CMD, or PowerShell;
- arbitrary executable path;
- remote desktop;
- arbitrary file browsing;
- arbitrary file upload/download;
- download-and-execute;
- changing the linked Discord owner.

Adding a new operation requires an explicit protocol and implementation change on both sides.

## Strategy identifiers

The wire carries an opaque `strategy_id`, never a local filename/path argument from Discord.

Current strategy IDs:

1. are derived only from the approved top-level `Resources\Strats` catalog;
2. require `.strat` files;
3. use a deterministic normalized catalog key;
4. are SHA-256-based Base64url identifiers prefixed with `s_`;
5. are resolved back to a canonical local path only inside the Agent.

The current catalog key is:

```text
builtin + NUL + filename.Normalize(FormC).ToUpperInvariant()
```

Golden vector:

```text
builtin + NUL + EXAMPLE.STRAT
-> s_kjr-1a5HJUSQFEg2FqPYT2mO4PCYETBQQIUyI-rvxC8
```

The Agent independently rejects rooted, UNC, device, traversal, wrong-extension, outside-root, duplicate, and reparse-point escapes before a strategy can be used locally.

If a running local strategy is outside the approved Remote catalog, `current_strategy_id` is `null`. The local absolute path is not uploaded.

## Handshake

Agent -> server:

```json
{
  "protocol": 1,
  "type": "HELLO",
  "agent_version": "0.5.0",
  "supported_operations": [
    "GET_STATUS",
    "LIST_STRATEGIES",
    "START_STRATEGY",
    "STOP_SAFE",
    "SWITCH_STRATEGY"
  ],
  "snapshot": {
    "macro_state": "idle",
    "roblox_running": false,
    "current_strategy_id": null
  }
}
```

Server -> Agent:

```json
{
  "protocol": 1,
  "type": "WELCOME",
  "heartbeat_interval_seconds": 30,
  "server_time": "2026-08-17T09:00:00.000Z",
  "reconcile_commands": []
}
```

The first Agent application message must be HELLO. Central sends WELCOME before the connection becomes eligible for command dispatch.

`reconcile_commands` is metadata, not a replay request. A mutation-capable Agent compares those entries with its durable local journal/AHK evidence and reports the known state; it must never perform a command merely because the command ID appears in WELCOME.

## Heartbeats and snapshots

Example heartbeat:

```json
{
  "protocol": 1,
  "type": "HEARTBEAT",
  "snapshot": {
    "macro_state": "running",
    "roblox_running": true,
    "current_strategy_id": "s_kjr-1a5HJUSQFEg2FqPYT2mO4PCYETBQQIUyI-rvxC8"
  }
}
```

`macro_state` is one of:

- `not_running`
- `idle`
- `running`
- `unknown`

Online/offline state, Agent version, protocol version, and last-seen timestamps are server-owned connection metadata.

`running` requires positive local evidence for the exact Remote-capable macro process and an active strategy lifecycle. A stale `state.ini` `Running=1` value alone is not sufficient.

## Commands

START and SWITCH contain one opaque strategy ID. Operations without arguments use an empty object.

Example START:

```json
{
  "protocol": 1,
  "type": "COMMAND",
  "command_id": "11111111-1111-4111-8111-111111111111",
  "operation": "START_STRATEGY",
  "issued_at": "2026-08-17T09:00:00.000Z",
  "expires_at": "2026-08-17T09:00:30.000Z",
  "arguments": {
    "strategy_id": "s_kjr-1a5HJUSQFEg2FqPYT2mO4PCYETBQQIUyI-rvxC8"
  }
}
```

`expires_at` is the deadline for **new acceptance/delivery**, not an execution deadline. STOP/SWITCH may remain executing while the current match finishes and the macro approaches the validated safe boundary.

The Agent must not newly accept an already-expired command.

Wire timestamps use canonical UTC with millisecond precision and `Z` suffix.

## Local mutation boundaries

### START_STRATEGY

The Agent resolves the strategy ID locally, journals the command, writes the fixed START request, and launches only:

```text
<macro root>\submacros\AutoHotkey64.exe <macro root>\Main_Remote.ahk
```

No executable/path comes from network command arguments.

START completion requires fresh lifecycle evidence for the requested strategy and Roblox running. A local `start_accepted` marker is intermediate evidence, not completion.

### STOP_SAFE

The Agent journals the command and writes a matching `stop` request to the fixed local mailbox. `Main_Remote.ahk` consumes it only at the validated between-match safe gate.

Completion requires matching AHK evidence and a resulting macro state of `idle` or `not_running`.

### SWITCH_STRATEGY

The Agent resolves the target strategy locally, journals the command, and writes a matching `switch` request to the fixed local mailbox. The AHK script consumes it only at the same safe boundary.

Completion requires matching AHK evidence and a running macro snapshot with the requested strategy ID.

STOP/SWITCH must not be moved into `PlayStrategy()`, placements, upgrades, abilities, recorded Click/Send/Sleep operations, or other timing-sensitive loops.

## Command states

The durable central lifecycle is:

```text
queued -> accepted -> executing -> completed
   \          \           \
    \          \           -> failed (definite outcome)
     \          -> connection ambiguity -> reconciling
      -> failed if no side effect could have occurred
```

- `queued` — persisted centrally and sent/awaiting Agent acceptance.
- `accepted` — Agent validated the command. For gameplay mutations the current Agent has already durably created the local journal entry before acknowledging this stage.
- `executing` — read operation is in progress or a local gameplay mutation has begun/waits for the safe boundary.
- `completed` — operation-specific postcondition has been proven.
- `failed` — a definite sanitized failure is known.
- `reconciling` — transport loss/ambiguity occurred after a gameplay-changing request may have produced a local side effect; central must not blindly replay it.

Only gameplay-changing operations need the ambiguity/reconciliation path. Interrupted read-only status/list requests can fail safely and be retried by the user.

## Command updates

Intermediate update:

```json
{
  "protocol": 1,
  "type": "COMMAND_UPDATE",
  "command_id": "11111111-1111-4111-8111-111111111111",
  "status": "accepted"
}
```

Completed START example:

```json
{
  "protocol": 1,
  "type": "COMMAND_UPDATE",
  "command_id": "11111111-1111-4111-8111-111111111111",
  "status": "completed",
  "snapshot": {
    "macro_state": "running",
    "roblox_running": true,
    "current_strategy_id": "s_kjr-1a5HJUSQFEg2FqPYT2mO4PCYETBQQIUyI-rvxC8"
  },
  "action_result": "strategy_started"
}
```

Failed example:

```json
{
  "protocol": 1,
  "type": "COMMAND_UPDATE",
  "command_id": "11111111-1111-4111-8111-111111111111",
  "status": "failed",
  "error": {
    "code": "STRATEGY_NOT_FOUND",
    "message": "The selected strategy is no longer installed."
  }
}
```

The public error message is sanitized. Central/Discord should render stable safe messages instead of raw exception details.

## Completion contracts

- `GET_STATUS` — a valid snapshot.
- `LIST_STRATEGIES` — a path-free list of unique `{strategy_id, name}` objects.
- `START_STRATEGY` — `action_result=strategy_started`, `macro_state=running`, Roblox running, requested strategy ID, and correlated local lifecycle evidence.
- `STOP_SAFE` — `action_result=stopped_safe`, macro state `idle` or `not_running`, and correlated safe-boundary result.
- `SWITCH_STRATEGY` — `action_result=switched_safe`, macro state `running`, requested strategy ID, and correlated safe-boundary result.

Central correlates updates to both the authenticated device and command ID, enforces monotonic lifecycle transitions, validates result shapes, and does not resend a gameplay command merely because the Agent reconnects.

## Reconciliation principle

When a connection fails after a local mutation may have occurred, the system does not guess.

Central records the command as reconciling, blocks conflicting gameplay mutations, and includes its ID in a later WELCOME. The Agent examines the durable local journal and fixed AHK evidence to report what can actually be proven.

This fail-closed behavior is one of the core safety properties of protocol 1.
