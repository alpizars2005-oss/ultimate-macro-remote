from __future__ import annotations

import asyncio
from dataclasses import dataclass, field
from datetime import timedelta
from typing import Any, Protocol

from .protocol import (
    ActionResult,
    CommandStatus,
    CommandUpdateMessage,
    HeartbeatMessage,
    HelloMessage,
    MacroSnapshot,
    MacroState,
    Operation,
    ProtocolError,
    TERMINAL_COMMAND_STATUSES,
    encode_command,
    format_utc,
    parse_utc,
    utc_now,
    validate_command_arguments,
    validate_discord_user_id,
)
from .store import (
    CommandRecord,
    DeviceRecord,
    InvalidTransition,
    RemoteStore,
    StoreAuthorizationError,
    StoreConflict,
    StoreError,
    StoreNotFound,
)


class TextSocket(Protocol):
    @property
    def closed(self) -> bool: ...

    async def send_str(self, data: str) -> None: ...

    async def close(self, *, code: int = 1000, message: bytes = b"") -> Any: ...


class RemoteServiceError(RuntimeError):
    def __init__(
        self, code: str, user_message: str, *, command_id: str | None = None
    ) -> None:
        super().__init__(user_message)
        self.code = code
        self.user_message = user_message
        self.command_id = command_id


@dataclass(slots=True)
class AgentSession:
    device_id: str
    socket: TextSocket
    agent_version: str
    supported_operations: frozenset[Operation]
    initial_snapshot: MacroSnapshot
    send_lock: asyncio.Lock = field(default_factory=asyncio.Lock)

    async def send(self, payload: str) -> None:
        async with self.send_lock:
            await self.socket.send_str(payload)


class RemoteService:
    """In-process API shared by the WebSocket server and central Discord bot."""

    def __init__(self, store: RemoteStore, *, command_delivery_ttl_seconds: int) -> None:
        self.store = store
        self.command_delivery_ttl_seconds = command_delivery_ttl_seconds
        self._sessions: dict[str, AgentSession] = {}
        self._pending_sessions: dict[str, AgentSession] = {}
        self._session_lock = asyncio.Lock()
        self._status_events: dict[str, asyncio.Event] = {}
        self._delivery_tasks: dict[str, asyncio.Task[None]] = {}

    async def prepare_agent(
        self, device: DeviceRecord, hello: HelloMessage, socket: TextSocket
    ) -> AgentSession:
        """Validate a HELLO without making the session dispatch-visible."""

        async with self._session_lock:
            if (
                device.device_id in self._sessions
                or device.device_id in self._pending_sessions
            ):
                raise RemoteServiceError(
                    "DEVICE_ALREADY_CONNECTED",
                    "This device already has an active Agent connection.",
                )
            self.store.update_presence(
                device_id=device.device_id,
                snapshot=hello.snapshot,
                connected=False,
                agent_version=hello.agent_version,
                supported_operations=hello.supported_operations,
            )
            session = AgentSession(
                device_id=device.device_id,
                socket=socket,
                agent_version=hello.agent_version,
                supported_operations=frozenset(hello.supported_operations),
                initial_snapshot=hello.snapshot,
            )
            self._pending_sessions[device.device_id] = session
            return session

    async def activate_agent(self, session: AgentSession) -> None:
        """Publish only after WELCOME has been serialized to the socket."""

        async with self._session_lock:
            if self._pending_sessions.get(session.device_id) is not session:
                raise RemoteServiceError(
                    "STALE_SESSION", "Agent handshake is no longer current."
                )
            if session.device_id in self._sessions:
                raise RemoteServiceError(
                    "DEVICE_ALREADY_CONNECTED",
                    "This device already has an active Agent connection.",
                )
            try:
                self.store.update_presence(
                    device_id=session.device_id,
                    snapshot=session.initial_snapshot,
                    connected=True,
                    agent_version=session.agent_version,
                    supported_operations=tuple(
                        operation
                        for operation in Operation
                        if operation in session.supported_operations
                    ),
                )
            except Exception:
                self._pending_sessions.pop(session.device_id, None)
                raise
            self._pending_sessions.pop(session.device_id, None)
            self._sessions[session.device_id] = session

    def reconciliation_commands(
        self, device_id: str
    ) -> tuple[tuple[str, Operation], ...]:
        return tuple(
            (command.command_id, command.operation)
            for command in self.store.list_reconciling_commands(device_id)
        )

    async def unregister_agent(
        self,
        session: AgentSession,
        *,
        reason_code: str = "CONNECTION_LOST_OUTCOME_UNKNOWN",
        reason_message: str = (
            "Agent disconnected; local command outcome requires reconciliation."
        ),
    ) -> None:
        removed = False
        async with self._session_lock:
            if self._sessions.get(session.device_id) is session:
                del self._sessions[session.device_id]
                self.store.mark_offline(session.device_id)
                removed = True
            elif self._pending_sessions.get(session.device_id) is session:
                del self._pending_sessions[session.device_id]
                self.store.mark_offline(session.device_id)
                return
        if not removed:
            return
        commands = self.store.resolve_device_disconnect(
            device_id=session.device_id,
            code=reason_code,
            message=reason_message,
        )
        for command in commands:
            self._cancel_delivery_timer(command.command_id)
            self._notify_status_change(command.command_id)

    async def revoke_device(self, device_id: str) -> None:
        async with self._session_lock:
            commands = self.store.revoke_device(device_id)
            session = self._sessions.pop(device_id, None)
            if session is None:
                session = self._pending_sessions.pop(device_id, None)
        for command in commands:
            self._cancel_delivery_timer(command.command_id)
            self._notify_status_change(command.command_id)
        if session is not None:
            await session.socket.close(code=1008, message=b"device_revoked")

    async def handle_agent_message(
        self, session: AgentSession, message: HeartbeatMessage | CommandUpdateMessage
    ) -> CommandRecord | None:
        async with self._session_lock:
            if self._sessions.get(session.device_id) is not session:
                raise ProtocolError("STALE_SESSION", "Agent session is no longer active.")
            if isinstance(message, HeartbeatMessage):
                self.store.update_presence(
                    device_id=session.device_id,
                    snapshot=message.snapshot,
                    connected=True,
                )
                return None
            command = self._apply_command_update(session.device_id, message)
            self.store.touch_device(session.device_id)
            return command

    async def dispatch_for_user(
        self,
        *,
        discord_user_id: str | int,
        operation: Operation,
        strategy_id: str | None = None,
    ) -> CommandRecord:
        """Dispatch without accepting any caller-supplied device identifier."""

        if not isinstance(operation, Operation):
            raise RemoteServiceError(
                "OPERATION_UNSUPPORTED", "That Remote operation is not allowed."
            )
        owner = validate_discord_user_id(discord_user_id)
        devices = self.store.list_devices_for_owner(owner)
        if not devices:
            raise RemoteServiceError(
                "DEVICE_NOT_LINKED", "No Remote device is linked to this Discord account."
            )
        if len(devices) != 1:
            raise RemoteServiceError(
                "MULTIPLE_DEVICES",
                "More than one device is linked; device selection is not available yet.",
            )
        device = devices[0]

        async with self._session_lock:
            session = self._sessions.get(device.device_id)
        if session is None or session.socket.closed or not device.connected:
            raise RemoteServiceError(
                "DEVICE_OFFLINE", "Device offline / not connected."
            )
        if operation not in session.supported_operations:
            raise RemoteServiceError(
                "OPERATION_UNSUPPORTED", "The connected Agent does not support that operation."
            )

        raw_arguments: dict[str, object] = {}
        if strategy_id is not None:
            raw_arguments["strategy_id"] = strategy_id
        arguments = validate_command_arguments(operation, raw_arguments)

        issued_at = utc_now()
        expires_at = issued_at + timedelta(
            seconds=self.command_delivery_ttl_seconds
        )
        try:
            command = self.store.create_command(
                device_id=device.device_id,
                owner_discord_user_id=owner,
                operation=operation,
                arguments=arguments,
                expires_at=format_utc(expires_at),
            )
        except StoreConflict as exc:
            raise RemoteServiceError(
                "COMMAND_IN_PROGRESS",
                "Another gameplay-changing command is already in progress or reconciling.",
            ) from exc
        except StoreAuthorizationError as exc:
            raise RemoteServiceError(
                "DEVICE_OFFLINE", "Device offline / not connected."
            ) from exc

        payload = encode_command(
            command_id=command.command_id,
            operation=operation,
            arguments=arguments,
            issued_at=issued_at,
            expires_at=expires_at,
        )
        self._delivery_tasks[command.command_id] = asyncio.create_task(
            self._watch_delivery_expiry(
                command.command_id,
                command.device_id,
                self.command_delivery_ttl_seconds,
            )
        )
        try:
            await session.send(payload)
        except asyncio.CancelledError:
            await self._make_delivery_unknown(command, session, "DISPATCH_CANCELLED")
            raise
        except Exception as exc:
            await self._make_delivery_unknown(command, session, "DISPATCH_FAILED")
            raise RemoteServiceError(
                "DEVICE_OFFLINE",
                "Device offline / not connected; command outcome is reconciling.",
                command_id=command.command_id,
            ) from exc
        return self.store.get_command(command.command_id)

    def get_command_for_user(
        self, *, discord_user_id: str | int, command_id: str
    ) -> CommandRecord:
        try:
            return self.store.get_command_for_owner(command_id, discord_user_id)
        except (StoreNotFound, StoreAuthorizationError) as exc:
            raise RemoteServiceError("COMMAND_NOT_FOUND", "Command was not found.") from exc

    async def wait_for_terminal(
        self,
        *,
        discord_user_id: str | int,
        command_id: str,
        timeout_seconds: float,
    ) -> CommandRecord:
        loop = asyncio.get_running_loop()
        deadline = loop.time() + timeout_seconds
        while True:
            event = self._status_events.setdefault(command_id, asyncio.Event())
            command = self.get_command_for_user(
                discord_user_id=discord_user_id, command_id=command_id
            )
            if command.status in TERMINAL_COMMAND_STATUSES:
                if self._status_events.get(command_id) is event:
                    self._status_events.pop(command_id, None)
                return command
            remaining = deadline - loop.time()
            if remaining <= 0:
                if self._status_events.get(command_id) is event:
                    self._status_events.pop(command_id, None)
                return command
            try:
                await asyncio.wait_for(event.wait(), remaining)
            except TimeoutError:
                if self._status_events.get(command_id) is event:
                    self._status_events.pop(command_id, None)
                return self.get_command_for_user(
                    discord_user_id=discord_user_id, command_id=command_id
                )

    async def close(self) -> None:
        tasks = tuple(self._delivery_tasks.values())
        self._delivery_tasks.clear()
        for task in tasks:
            task.cancel()
        if tasks:
            await asyncio.gather(*tasks, return_exceptions=True)

        async with self._session_lock:
            sessions = tuple(self._sessions.values()) + tuple(
                self._pending_sessions.values()
            )
        for session in sessions:
            await self.unregister_agent(
                session,
                reason_code="SERVER_SHUTDOWN_OUTCOME_UNKNOWN",
                reason_message=(
                    "Central backend stopped; local command outcome requires reconciliation."
                ),
            )
            await session.socket.close(code=1001, message=b"server_shutdown")

    async def _watch_delivery_expiry(
        self, command_id: str, device_id: str, delay_seconds: float
    ) -> None:
        try:
            await asyncio.sleep(delay_seconds)
            command = self.store.get_command(command_id)
            if command.status is not CommandStatus.QUEUED:
                return
            async with self._session_lock:
                session = self._sessions.get(device_id)
            if session is not None:
                await self.unregister_agent(
                    session,
                    reason_code="DELIVERY_TIMEOUT_OUTCOME_UNKNOWN",
                    reason_message=(
                        "Agent did not accept before delivery expiry; reconciliation is required."
                    ),
                )
                await session.socket.close(code=1008, message=b"delivery_timeout")
            else:
                reconciled = self._resolve_delivery_unknown(
                    command,
                    code="DELIVERY_TIMEOUT_OUTCOME_UNKNOWN",
                    message=(
                        "Agent did not accept before delivery expiry; reconciliation is required."
                    ),
                )
                self._notify_status_change(reconciled.command_id)
        except (asyncio.CancelledError, StoreError):
            return
        finally:
            self._delivery_tasks.pop(command_id, None)

    async def _make_delivery_unknown(
        self, command: CommandRecord, session: AgentSession, reason: str
    ) -> None:
        await self.unregister_agent(
            session,
            reason_code=f"{reason}_OUTCOME_UNKNOWN",
            reason_message=(
                "Command delivery was interrupted; local outcome requires reconciliation."
            ),
        )
        current = self.store.get_command(command.command_id)
        if current.status in {
            CommandStatus.QUEUED,
            CommandStatus.ACCEPTED,
            CommandStatus.EXECUTING,
        }:
            current = self._resolve_delivery_unknown(
                current,
                code=f"{reason}_OUTCOME_UNKNOWN",
                message=(
                    "Command delivery was interrupted; local outcome requires reconciliation."
                ),
            )
            self._notify_status_change(current.command_id)
        await session.socket.close(code=1001, message=b"delivery_interrupted")
        self._cancel_delivery_timer(command.command_id)

    def _apply_command_update(
        self, device_id: str, update: CommandUpdateMessage
    ) -> CommandRecord:
        try:
            command = self.store.get_command(update.command_id)
        except StoreNotFound as exc:
            raise ProtocolError("UNKNOWN_COMMAND", "Command is not known.") from exc
        if command.device_id != device_id:
            raise ProtocolError(
                "COMMAND_DEVICE_MISMATCH", "Command belongs to another device."
            )
        if (
            command.status is CommandStatus.QUEUED
            and update.status is CommandStatus.ACCEPTED
            and utc_now() > parse_utc(command.expires_at)
        ):
            reconciled = self._resolve_delivery_unknown(
                command,
                code="DELIVERY_TIMEOUT_OUTCOME_UNKNOWN",
                message="Agent acceptance arrived after delivery expiry.",
            )
            self._cancel_delivery_timer(command.command_id)
            self._notify_status_change(reconciled.command_id)
            raise ProtocolError(
                "COMMAND_DELIVERY_EXPIRED", "Command acceptance arrived after expiry."
            )

        result: dict[str, object] | None = None
        error_code: str | None = None
        error_message: str | None = None
        if update.status is CommandStatus.COMPLETED:
            result = self._validate_completed_result(command, update)
        elif update.status is CommandStatus.FAILED:
            if update.error is None:
                raise ProtocolError(
                    "INVALID_COMMAND_RESULT", "Failed command has no error."
                )
            error_code = update.error.code
            error_message = update.error.message

        was_same_status = command.status is update.status
        try:
            transitioned = self.store.transition_command(
                command_id=command.command_id,
                device_id=device_id,
                status=update.status,
                result=result,
                error_code=error_code,
                error_message=error_message,
            )
        except (InvalidTransition, StoreAuthorizationError) as exc:
            raise ProtocolError(
                "INVALID_COMMAND_TRANSITION", "Command status transition is invalid."
            ) from exc

        if update.status is CommandStatus.ACCEPTED:
            self._cancel_delivery_timer(command.command_id)
        if (
            update.status is CommandStatus.COMPLETED
            and update.snapshot is not None
            and not was_same_status
        ):
            try:
                self.store.update_presence(
                    device_id=device_id,
                    snapshot=update.snapshot,
                    connected=True,
                )
            except StoreNotFound:
                pass
        if transitioned.status in TERMINAL_COMMAND_STATUSES:
            self._cancel_delivery_timer(command.command_id)
            self._notify_status_change(command.command_id)
        return transitioned

    @staticmethod
    def _validate_completed_result(
        command: CommandRecord, update: CommandUpdateMessage
    ) -> dict[str, object]:
        operation = command.operation
        if operation is Operation.LIST_STRATEGIES:
            if (
                update.strategies is None
                or update.snapshot is not None
                or update.action_result is not None
            ):
                raise ProtocolError(
                    "INVALID_COMMAND_RESULT", "LIST_STRATEGIES requires a strategy list."
                )
            return {
                "strategies": [strategy.to_wire() for strategy in update.strategies]
            }
        if update.snapshot is None or update.strategies is not None:
            raise ProtocolError(
                "INVALID_COMMAND_RESULT",
                f"{operation.value} requires a macro snapshot.",
            )

        snapshot = update.snapshot
        if operation is Operation.GET_STATUS:
            if update.action_result is not None:
                raise ProtocolError(
                    "INVALID_COMMAND_RESULT", "GET_STATUS cannot carry an action result."
                )
            return {"snapshot": snapshot.to_wire()}

        expected_action_result = {
            Operation.START_STRATEGY: ActionResult.STRATEGY_STARTED,
            Operation.STOP_SAFE: ActionResult.STOPPED_SAFE,
            Operation.SWITCH_STRATEGY: ActionResult.SWITCHED_SAFE,
        }[operation]
        if update.action_result is not expected_action_result:
            raise ProtocolError(
                "INVALID_COMMAND_RESULT",
                f"{operation.value} has missing or mismatched bridge evidence.",
            )

        if operation in {Operation.START_STRATEGY, Operation.SWITCH_STRATEGY}:
            target = command.arguments["strategy_id"]
            if (
                snapshot.macro_state is not MacroState.RUNNING
                or snapshot.current_strategy_id != target
            ):
                raise ProtocolError(
                    "INVALID_COMMAND_RESULT",
                    f"{operation.value} did not report the requested strategy running.",
                )
            if operation is Operation.START_STRATEGY and not snapshot.roblox_running:
                raise ProtocolError(
                    "INVALID_COMMAND_RESULT",
                    "START_STRATEGY did not report Roblox running.",
                )
        elif operation is Operation.STOP_SAFE and snapshot.macro_state not in {
            MacroState.NOT_RUNNING,
            MacroState.IDLE,
        }:
            raise ProtocolError(
                "INVALID_COMMAND_RESULT", "STOP_SAFE did not report a stopped macro."
            )
        return {
            "snapshot": snapshot.to_wire(),
            "action_result": update.action_result.value,
        }

    def _cancel_delivery_timer(self, command_id: str) -> None:
        task = self._delivery_tasks.pop(command_id, None)
        if task is not None and task is not asyncio.current_task():
            task.cancel()

    def _resolve_delivery_unknown(
        self, command: CommandRecord, *, code: str, message: str
    ) -> CommandRecord:
        if command.operation in {
            Operation.START_STRATEGY,
            Operation.STOP_SAFE,
            Operation.SWITCH_STRATEGY,
        }:
            status = CommandStatus.RECONCILING
        else:
            status = CommandStatus.FAILED
            code = code.replace("OUTCOME_UNKNOWN", "READ_FAILED")
            message = "Agent disconnected before the read completed."
        return self.store.transition_command(
            command_id=command.command_id,
            device_id=command.device_id,
            status=status,
            error_code=code,
            error_message=message,
        )

    def _notify_status_change(self, command_id: str) -> None:
        event = self._status_events.pop(command_id, None)
        if event is not None:
            event.set()
