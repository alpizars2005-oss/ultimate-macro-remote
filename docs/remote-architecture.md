# Ultimate Macro Remote — current architecture

This document describes the current private R5 development architecture. Historical R1/R2/R3 milestone notes have been removed from the main description so a reviewer can see the system as it exists now.

## Design goals

Remote should provide useful Discord control without becoming a generic remote-access product and without weakening Ultimate Macro's timing-sensitive gameplay execution.

The design therefore prefers:

- fixed allowlisted operations over arbitrary execution;
- authoritative Discord identity over client-supplied owner/device IDs;
- opaque strategy IDs over local paths;
- outbound Agent transport over inbound client listeners;
- durable evidence/reconciliation over blind retries;
- safe between-match STOP/SWITCH over mid-match interruption;
- explicit user consent and per-user enrollment over silent installation.

## System overview

```text
                            Discord
                    OAuth2 identify + slash commands
                              |
                              v
+----------------------------------------------------------------+
|                     Central Remote runtime                      |
|                                                                |
|  RemoteDiscordClient / MacroCommandController                  |
|                 |                                              |
|                 v                                              |
|           RemoteService  <---->  RemoteStore / SQLite          |
|                 |                                              |
|                 +---- OAuth onboarding / legacy pairing        |
|                 |                                              |
|                 +---- authenticated WebSocket endpoint         |
+-----------------|----------------------------------------------+
                  |
                  | WSS /remote/v1/agent
                  | Authorization: Bearer <device credential>
                  v
+----------------------------------------------------------------+
|                 UltimateRemoteAgent.exe (Windows)              |
|                                                                |
|  DPAPI enrollment                                               |
|  exact process/state inspection                                 |
|  approved strategy catalog                                      |
|  durable command journal                                        |
|  fixed START launcher                                           |
|  safe STOP/SWITCH mailbox                                       |
+-----------------|----------------------------------------------+
                  |
                  v
             Main_Remote.ahk
                  |
          startup START consumer
          between-match safe gate
                  |
                  v
              Roblox / TDS
```

## Identity and ownership

For normal onboarding, the Windows Agent creates a random one-time setup secret and opens the central Discord OAuth authorization URL. Central uses only Discord's authorization-code flow with the `identify` scope to determine the owner account.

The client does not submit a Discord user ID. The callback uses an unpredictable OAuth `state`, and the setup secret is transported separately in the Agent's authorization header.

For ongoing Discord commands, `interaction.user.id` is passed directly to `RemoteService.dispatch_for_user`. The command API intentionally has no user-supplied owner or device selector.

The current milestone supports exactly one active linked device per Discord account. If more than one exists, central fails closed because device selection is not implemented.

## Device enrollment and authentication

Central creates a random device bearer credential for the linked installation. SQLite stores a digest rather than the plaintext bearer.

The Agent stores its enrollment envelope in the interactive Windows user's LocalAppData with DPAPI CurrentUser protection. The envelope binds:

- the trusted HTTPS service origin;
- the derived WSS endpoint;
- the validated local macro root;
- the device credential.

Re-extracting/moving the macro to another valid local path on the same Windows account and same service origin refreshes only the trusted local macro root. A different service origin is treated as a different trust boundary and requires fresh enrollment.

The background Agent connects outbound; the client machine exposes no inbound Remote listener.

## Transport

Protocol 1 uses authenticated JSON-over-WebSocket at `/remote/v1/agent`.

The Agent sends HELLO, receives WELCOME, then exchanges commands, updates, and periodic snapshot heartbeats. Central tracks online/offline state and stores durable command lifecycle state.

Cross-machine transport requires normally trusted HTTPS/WSS. Plaintext is reserved for literal loopback development. A reverse proxy/tunnel must preserve the `Authorization` header and WebSocket Upgrade semantics.

## Operation allowlist

Protocol 1 supports exactly:

- `GET_STATUS`
- `LIST_STRATEGIES`
- `START_STRATEGY`
- `STOP_SAFE`
- `SWITCH_STRATEGY`

There is no protocol operation for generic EXEC, shell, CMD, PowerShell, remote desktop, arbitrary executable path, arbitrary file browser, arbitrary upload/download, or download-and-execute behavior.

## Local status and strategy catalog

Status is based on conservative process/state inspection rather than trusting a stale `state.ini` flag alone. The Agent identifies the fixed bundled AutoHotkey executable and exact `Main_Remote.ahk` command line before treating the Remote-capable macro as live.

Remote strategy discovery is intentionally restricted to top-level `.strat` files in the approved `Resources\Strats` directory. The Agent:

- rejects traversal/rooted/UNC/device escapes;
- rejects reparse-point escapes;
- resolves the final local path under the approved root;
- generates deterministic opaque IDs;
- returns only `{strategy_id, name}` to central/Discord.

A local strategy path never crosses the network protocol.

## START architecture

START is permitted only when the Agent can safely establish that an active strategy is not already running.

The selected strategy ID is resolved inside the approved strategy catalog. The Agent writes the fixed local START request and launches only:

```text
<macro root>\submacros\AutoHotkey64.exe <macro root>\Main_Remote.ahk
```

No network-supplied executable path or arbitrary command-line argument is accepted.

`Main_Remote.ahk` consumes the START request during startup. START is not considered complete merely because the AHK script accepted the mailbox request; the Agent waits for fresh lifecycle evidence for the requested strategy and a running Roblox process before reporting `strategy_started`.

## STOP/SWITCH safe boundary

STOP and SWITCH are intentionally different from START.

The Agent validates the command and local target, durably journals the mutation, then places a matching command in the one-slot UTF-16 local mailbox. `Main_Remote.ahk` consumes these mutations only at the existing validated between-match gate in `RunStrategy()`.

The Remote system does **not** inject command checks into:

- `PlayStrategy()`;
- tower placements;
- upgrades;
- abilities;
- recorded Click/Send/Sleep actions;
- other timing-sensitive gameplay loops.

Reconnect/recovery work can delay a queued STOP/SWITCH. That delay is intentional because preserving a match is preferred to immediate remote interruption.

## Mutation lifecycle and reconciliation

Central stores the command lifecycle. The Agent also keeps a durable local mutation journal before performing side effects.

Conceptually:

```text
queued -> accepted -> executing -> completed
   \          \           \
    \          \           -> failed (definite outcome)
     \          -> connection loss / ambiguity -> reconciling
      -> delivery failure
```

For gameplay mutations, loss of transport after a local side effect may create an ambiguous outcome. Central moves the command to `reconciling` rather than replaying it. WELCOME carries reconciliation metadata on reconnect; the Agent compares that metadata with its durable local journal and AHK evidence.

Availability deliberately yields to safety: another conflicting gameplay mutation is blocked while the previous one has an unresolved outcome.

## Consent and Windows startup

When a packaged Agent and `remote_service.url` are present beside the macro, `Main_Remote.ahk` can start the Agent bootstrap independently. A Remote bootstrap failure must not prevent normal macro UI startup.

The preview consent dialog explains the allowlisted capabilities and lets the user decline Remote. If enabled, the user may also choose current-user Windows startup.

The startup registry entry launches only:

```text
UltimateRemoteAgent.exe run-background
```

It does not start Roblox, Ultimate Macro, or a strategy on login. A later authenticated START command is still required.

## Central runtime and storage

The normal development/operator process is `run_bot.bat`, which starts the aiohttp backend and Discord client in one process sharing one `RemoteStore` and SQLite database.

The backend normally binds to literal loopback (`127.0.0.1:8765`) behind trusted TLS termination. The SQLite directory is local server state and should not be shared over a network filesystem or copied into client packages.

## Security assumptions

- The central server OS account, environment, SQLite database, and TLS termination are trusted operator infrastructure.
- DPAPI CurrentUser protects against casual offline copying/another Windows user, not malware already running with the same user privileges or a local administrator.
- The local AHK mailbox is same-user local IPC, not a separate cryptographic trust boundary; the Agent must validate network inputs before writing it.
- Discord account compromise grants only the five allowlisted Remote operations for that account's linked device, not generic machine access.
- A central compromise could issue only operations implemented by the Agent/protocol; it does not magically create shell/desktop capabilities that are absent locally.
- When execution outcome is uncertain, commands are not automatically replayed.

## Current preview limitations

### One-device milestone

Only one active linked device per Discord account is supported. Multi-device naming/selection requires a later protocol/product decision.

### Development hosting

A Cloudflare Quick Tunnel can satisfy trusted TLS/WSS for private testing, but its random hostname is not stable infrastructure. Production needs a stable hostname, service supervision, backups/retention decisions, monitoring, and normal secret rotation.

### OAuth onboarding crash window

Current OAuth setup session/state data is process-local memory. After authoritative Discord identity is verified, the callback provisions a durable device row before reporting browser success. The Agent then polls the setup session, stores the returned credential with DPAPI, and completes setup.

If the central process crashes/restarts in the narrow interval after device provisioning but before Agent completion, the in-memory setup session is lost while the durable device row can remain active/offline. Current operator recovery is to revoke/clean that orphan before retrying. A production design should persist an explicit pending-enrollment state or otherwise make this crash path automatically recoverable.

### Distribution

Formal public Terms/Privacy and an upstream-controlled distribution/licensing review are still required before public release.

## Related documents

- `remote-protocol-v1.md` — exact central/Agent wire contract.
- `windows-agent-r5.md` — Windows Agent build/runtime behavior.
- `remote-server-r5.md` — central configuration and operation.
- `remote-preview-r5.md` — intended end-user/private-preview experience.
- `remote-pairing-v1.md` — legacy development fallback, not normal onboarding.
