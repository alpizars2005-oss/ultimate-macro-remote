# UltimateRemoteAgent — R5

`UltimateRemoteAgent.exe` is the Windows-side component of the private Remote preview. It is a self-contained `win-x64` .NET application that keeps an outbound connection to the central Remote service and implements only the fixed protocol-1 operations.

## Responsibilities

The Agent is responsible for:

- one-time consent/onboarding bootstrap;
- Discord OAuth setup polling;
- DPAPI CurrentUser enrollment storage;
- optional current-user Windows autostart;
- authenticated outbound WSS connection;
- conservative macro/Roblox state inspection;
- approved strategy catalog generation;
- fixed START launcher;
- safe STOP/SWITCH local mailbox adapter;
- durable local mutation journal and reconnect reconciliation.

It is **not** a general machine-control Agent.

## Explicit non-capabilities

The Agent contains no generic Remote operation for:

- shell/CMD/PowerShell execution;
- arbitrary executable or arbitrary command-line execution;
- remote desktop;
- arbitrary file browsing;
- arbitrary network-supplied local paths;
- download-and-execute;
- changing the linked Discord owner from client/server command input.

## Build

From the repository root on Windows with the .NET 10 SDK:

```powershell
dotnet restore .\UltimateRemoteAgent\UltimateRemoteAgent.slnx
dotnet build .\UltimateRemoteAgent\UltimateRemoteAgent.slnx -c Release --no-restore
dotnet test .\UltimateRemoteAgent\UltimateRemoteAgent.slnx -c Release --no-build
dotnet format .\UltimateRemoteAgent\UltimateRemoteAgent.slnx --verify-no-changes --no-restore
dotnet publish .\UltimateRemoteAgent\src\UltimateRemoteAgent\UltimateRemoteAgent.csproj -c Release -p:PublishProfile=win-x64
```

The self-contained publish output is under:

```text
UltimateRemoteAgent\src\UltimateRemoteAgent\bin\Release\net10.0-windows\win-x64\publish
```

Normal client PCs do not need Python, VS Code, the source tree, or an installed .NET runtime.

## Agent modes

The executable currently exposes:

```text
UltimateRemoteAgent.exe bootstrap <macro-root>
UltimateRemoteAgent.exe run
UltimateRemoteAgent.exe run-background
UltimateRemoteAgent.exe pair <https-origin> <macro-root>
UltimateRemoteAgent.exe inspect <macro-root>
```

### `bootstrap`

Used by the packaged Remote client.

Bootstrap:

1. validates the macro root and `remote_service.url`;
2. loads or displays the current preview consent choice;
3. reuses a valid same-origin DPAPI enrollment when possible;
4. otherwise starts Discord OAuth onboarding;
5. stores the returned enrollment with DPAPI CurrentUser;
6. applies/removes the current-user Windows startup entry;
7. starts the background Agent.

The bootstrap console is hidden in normal use. Safe bootstrap failures are logged by stable code and shown in a small error dialog. A bootstrap failure must not stop the normal AutoHotkey macro UI from opening.

### `run`

Runs the enrolled Agent with a visible console, useful for development diagnostics.

Typical safe logs include:

```text
INFO AGENT_STARTING
INFO AGENT_CONNECTED
WARN AGENT_CONNECTION_LOST
```

Logs use stable codes and should not print device credentials.

### `run-background`

Runs the same enrolled Agent with its console hidden. This is the target of the optional current-user Windows startup registration.

`run-background` does not launch Roblox or Ultimate Macro by itself.

### `pair`

Legacy development fallback. It validates the trusted HTTPS origin and macro root, asks for a `/macro pair` ticket through a hidden prompt, redeems it, and stores the resulting enrollment with DPAPI CurrentUser.

This mode is not the intended R5 end-user onboarding path.

### `inspect`

Performs a path-free one-shot local inspection without connecting to central:

```powershell
.\UltimateRemoteAgent.exe inspect "C:\path\to\Ultimate_Macro_Remote"
```

It prints only protocol snapshot data plus opaque strategy IDs/display names, not local absolute strategy paths.

## Single interactive-user instance

Normal Agent execution is guarded so one interactive Windows user does not run multiple competing background Agents at the same time.

This protects the one enrollment/local bridge from duplicate WSS sessions and duplicate local mutation handling.

## Enrollment storage

Default enrollment path:

```text
%LOCALAPPDATA%\UltimateRemoteAgent\enrollment.v1.bin
```

The envelope is protected using DPAPI CurrentUser and contains the validated service origin/WSS endpoint, local macro root, and device credential.

Do not copy this file between Windows accounts or distribute it with the client package.

The service origin is part of the trust boundary. A different origin requires new authoritative enrollment rather than silently reusing a bearer on another service.

## Consent/preferences storage

Preview preferences are stored per Windows user under LocalAppData. Consent is versioned; a later Terms/Privacy version can intentionally trigger a new review.

The user may decline Remote without disabling normal Ultimate Macro use.

## Windows autostart

If the user enables **Start the Remote Agent with Windows**, bootstrap writes a current-user Run entry for:

```text
"<absolute path>\UltimateRemoteAgent.exe" run-background
```

It does not register a system service, require elevation, or start the macro/Roblox at login.

Declining/disabling Remote removes the Agent startup value.

## Status inspection

The Agent does not trust `state.ini` alone.

It conservatively correlates the exact bundled AutoHotkey executable/process with the expected `Main_Remote.ahk` argument and local state evidence. A stale state file cannot by itself prove that the macro is running.

## Strategy catalog

The Remote catalog includes only top-level `.strat` files in:

```text
<macro root>\Resources\Strats
```

The catalog performs local path-security checks and exposes only deterministic opaque IDs plus display names to central.

Nested folders are intentionally not part of the current milestone.

## START with the macro closed

When the Agent receives an accepted `START_STRATEGY` command and can prove that an active macro strategy is not already running, it:

1. resolves the opaque strategy ID to a validated local path inside the approved catalog;
2. creates the durable local command journal entry;
3. queues the fixed START request;
4. launches only:

   ```text
   <macro root>\submacros\AutoHotkey64.exe <macro root>\Main_Remote.ahk
   ```

5. waits for correlated fresh lifecycle evidence and Roblox running before reporting `strategy_started`.

If the macro is already running, START fails and the user should use SWITCH instead.

## STOP/SWITCH local bridge

The Agent writes STOP/SWITCH through the fixed one-slot local mailbox under the interactive user's Ultimate Macro AppData state.

The mailbox is not treated as permission to interrupt active gameplay. `Main_Remote.ahk` consumes these requests at the validated between-match safe boundary.

The Agent may therefore wait for completion while the macro finishes the current game/recovery path.

## Durable mutation journal

Before a gameplay-changing side effect, the Agent records the command locally. The journal allows reconnect handling to distinguish:

- commands definitely completed;
- commands definitely failed;
- commands that were accepted but never executed;
- commands whose local outcome must be reconciled from AHK/process evidence.

A reconciling command is not blindly replayed.

## Transport/reconnect

The Agent uses the DPAPI enrollment bearer to open `/remote/v1/agent` by WSS, sends HELLO with supported operations/current snapshot, then maintains heartbeats and command processing.

Transient network failures use reconnect backoff/jitter. A terminal server policy/protocol rejection fails rather than looping forever as if it were a transient outage.

## Client packaging

The recommended user-facing distribution is produced by:

```powershell
.\tools\package_remote_preview.ps1
```

The client package should contain the published `UltimateRemoteAgent.exe` and public `remote_service.url`, not source code/server secrets.

## Testing expectations

Automated .NET coverage includes protocol parsing, transport behavior, enrollment/pairing/onboarding helpers, path handling, local state/catalog behavior, mutation bridge/journal behavior, and runtime components.

A real acceptance pass should still test:

- first-run consent;
- Discord OAuth onboarding;
- DPAPI enrollment creation/reuse;
- background Agent connection;
- `/macro status` and `/macro strategies`;
- START from a closed macro;
- safe deferred SWITCH;
- safe deferred STOP;
- reconnect/reconciliation;
- Windows-login Agent startup without automatic Roblox/macro launch.

## Known preview considerations

The Agent side can persist the enrollment, but the current central OAuth setup session is process-local. A central crash in the post-callback/pre-completion setup window can require central operator cleanup before the Agent can enroll again. See `remote-server-r5.md` and `remote-architecture.md`.
