from __future__ import annotations

import hashlib
import hmac
import json
import os
import re
import secrets
import sqlite3
import threading
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from typing import Mapping

from .protocol import (
    MUTATING_OPERATIONS,
    PROTOCOL_VERSION,
    CommandStatus,
    MacroSnapshot,
    MacroState,
    Operation,
    TERMINAL_COMMAND_STATUSES,
    format_utc,
    parse_utc,
    utc_now,
    validate_command_arguments,
    validate_discord_user_id,
)


_CREDENTIAL_RE = re.compile(
    r"urad_v1\.([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\.([A-Za-z0-9_-]{43})\Z"
)
class StoreError(RuntimeError):
    pass


class StoreNotFound(StoreError):
    pass


class StoreConflict(StoreError):
    pass


class StoreAuthorizationError(StoreError):
    pass


class InvalidTransition(StoreError):
    pass


class StorePairingInvalid(StoreError):
    """Raised for every unusable development pairing ticket."""


class StorePairingAlreadyLinked(StoreConflict):
    """Raised when development pairing cannot create a second active device."""


class StoreRateLimited(StoreError):
    def __init__(self, retry_after_seconds: int) -> None:
        super().__init__("Pairing rate limit exceeded.")
        self.retry_after_seconds = max(1, retry_after_seconds)


@dataclass(frozen=True, slots=True)
class DeviceRecord:
    device_id: str
    owner_discord_user_id: str
    revoked: bool
    connected: bool
    created_at: str
    last_seen_at: str | None
    agent_version: str | None
    protocol_version: int | None
    supported_operations: tuple[Operation, ...]
    macro_state: MacroState
    roblox_running: bool
    current_strategy_id: str | None


@dataclass(frozen=True, slots=True)
class ProvisionedDevice:
    device: DeviceRecord
    credential: str = field(repr=False)


@dataclass(frozen=True, slots=True)
class CommandRecord:
    command_id: str
    device_id: str
    owner_discord_user_id: str
    operation: Operation
    arguments: dict[str, str]
    status: CommandStatus
    created_at: str
    updated_at: str
    expires_at: str
    result: dict[str, object] | None
    error_code: str | None
    error_message: str | None


@dataclass(frozen=True, slots=True)
class PairingRateRule:
    scope: str
    subject_hash: bytes
    window_started_at: str
    window_seconds: int
    limit: int


class RemoteStore:
    """Small synchronous SQLite store for central Remote state.

    Operations are deliberately short and guarded by a process-local lock. The server
    runs at development scale; an async database layer can be introduced if profiling
    later shows a need.
    """

    def __init__(self, database_path: str | Path) -> None:
        self._path = str(database_path)
        self._instance_lock: _DatabaseFileLock | None = None
        if self._path != ":memory:":
            resolved_path = Path(self._path).resolve()
            resolved_path.parent.mkdir(parents=True, exist_ok=True)
            self._path = str(resolved_path)
            self._instance_lock = _DatabaseFileLock(
                resolved_path.with_name(resolved_path.name + ".lock")
            )
        self._lock = threading.RLock()
        connection: sqlite3.Connection | None = None
        try:
            connection = sqlite3.connect(
                self._path,
                isolation_level="IMMEDIATE",
                check_same_thread=False,
            )
            self._connection = connection
            self._connection.row_factory = sqlite3.Row
            self._initialize()
        except Exception:
            try:
                if connection is not None:
                    connection.close()
            finally:
                if self._instance_lock is not None:
                    self._instance_lock.close()
                    self._instance_lock = None
            raise

    def close(self) -> None:
        with self._lock:
            try:
                self._connection.close()
            finally:
                if self._instance_lock is not None:
                    self._instance_lock.close()
                    self._instance_lock = None

    def _initialize(self) -> None:
        now = format_utc(utc_now())
        with self._lock, self._connection:
            self._connection.execute("PRAGMA foreign_keys = ON")
            if self._path != ":memory:":
                self._connection.execute("PRAGMA journal_mode = WAL")
            self._connection.executescript(
                """
                CREATE TABLE IF NOT EXISTS devices (
                    device_id TEXT PRIMARY KEY,
                    owner_discord_user_id TEXT NOT NULL,
                    credential_hash BLOB NOT NULL UNIQUE,
                    created_at TEXT NOT NULL,
                    revoked_at TEXT,
                    connected INTEGER NOT NULL DEFAULT 0 CHECK (connected IN (0, 1)),
                    last_seen_at TEXT,
                    agent_version TEXT,
                    protocol_version INTEGER,
                    supported_operations_json TEXT NOT NULL DEFAULT '[]',
                    macro_state TEXT NOT NULL DEFAULT 'unknown',
                    roblox_running INTEGER NOT NULL DEFAULT 0 CHECK (roblox_running IN (0, 1)),
                    current_strategy_id TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_devices_owner
                    ON devices(owner_discord_user_id);

                CREATE TABLE IF NOT EXISTS pairing_tickets (
                    ticket_id TEXT PRIMARY KEY,
                    owner_discord_user_id TEXT NOT NULL,
                    token_hash BLOB NOT NULL UNIQUE CHECK (length(token_hash) = 32),
                    created_at TEXT NOT NULL,
                    expires_at TEXT NOT NULL,
                    consumed_at TEXT,
                    invalidated_at TEXT
                );

                CREATE UNIQUE INDEX IF NOT EXISTS idx_one_live_pairing_ticket_per_owner
                    ON pairing_tickets(owner_discord_user_id)
                    WHERE consumed_at IS NULL AND invalidated_at IS NULL;

                CREATE TABLE IF NOT EXISTS pairing_rate_events (
                    event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    scope TEXT NOT NULL,
                    subject_hash BLOB NOT NULL CHECK (length(subject_hash) = 32),
                    occurred_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_pairing_rate_events
                    ON pairing_rate_events(scope, subject_hash, occurred_at);

                CREATE TABLE IF NOT EXISTS commands (
                    command_id TEXT PRIMARY KEY,
                    device_id TEXT NOT NULL REFERENCES devices(device_id),
                    owner_discord_user_id TEXT NOT NULL,
                    operation TEXT NOT NULL,
                    arguments_json TEXT NOT NULL,
                    status TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    expires_at TEXT NOT NULL,
                    result_json TEXT,
                    error_code TEXT,
                    error_message TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_commands_owner
                    ON commands(owner_discord_user_id, created_at);

                DROP INDEX IF EXISTS idx_one_mutation_per_device;

                CREATE UNIQUE INDEX idx_one_mutation_per_device
                    ON commands(device_id)
                    WHERE status IN ('queued', 'accepted', 'executing', 'reconciling')
                      AND operation IN ('START_STRATEGY', 'STOP_SAFE', 'SWITCH_STRATEGY');
                """
            )
            self._connection.execute("UPDATE devices SET connected = 0")
            self._connection.execute(
                """
                UPDATE commands
                   SET status = ?, updated_at = ?, error_code = ?, error_message = ?
                 WHERE status IN (?, ?, ?)
                   AND operation IN ('START_STRATEGY', 'STOP_SAFE', 'SWITCH_STRATEGY')
                """,
                (
                    CommandStatus.RECONCILING.value,
                    now,
                    "SERVER_RESTART_OUTCOME_UNKNOWN",
                    "Central backend restarted; the Agent must reconcile this command.",
                    CommandStatus.QUEUED.value,
                    CommandStatus.ACCEPTED.value,
                    CommandStatus.EXECUTING.value,
                ),
            )
            self._connection.execute(
                """
                UPDATE commands
                   SET status = ?, updated_at = ?, error_code = ?, error_message = ?
                 WHERE status IN (?, ?, ?)
                   AND operation IN ('GET_STATUS', 'LIST_STRATEGIES')
                """,
                (
                    CommandStatus.FAILED.value,
                    now,
                    "SERVER_RESTART",
                    "Central backend restarted before the read completed.",
                    CommandStatus.QUEUED.value,
                    CommandStatus.ACCEPTED.value,
                    CommandStatus.EXECUTING.value,
                ),
            )

    def provision_device(self, owner_discord_user_id: str | int) -> ProvisionedDevice:
        owner = validate_discord_user_id(owner_discord_user_id)
        for _ in range(3):
            device_id = str(uuid.uuid4())
            secret = secrets.token_urlsafe(32)
            credential = f"urad_v1.{device_id}.{secret}"
            digest = _credential_digest(credential)
            created_at = format_utc(utc_now())
            try:
                with self._lock, self._connection:
                    self._connection.execute(
                        """
                        INSERT INTO devices (
                            device_id, owner_discord_user_id, credential_hash, created_at
                        ) VALUES (?, ?, ?, ?)
                        """,
                        (device_id, owner, digest, created_at),
                    )
            except sqlite3.IntegrityError:
                continue
            device = self.get_device(device_id)
            return ProvisionedDevice(device=device, credential=credential)
        raise StoreConflict("Could not allocate a unique device credential.")

    def issue_pairing_ticket(
        self,
        *,
        ticket_id: str,
        owner_discord_user_id: str | int,
        token_digest: bytes,
        created_at: str,
        expires_at: str,
    ) -> None:
        """Persist one hashed ticket issued for an authoritative Discord owner."""

        owner = validate_discord_user_id(owner_discord_user_id)
        if len(token_digest) != 32:
            raise ValueError("Pairing token digests must be 32 bytes.")
        with self._lock, self._connection:
            linked = self._connection.execute(
                """
                SELECT 1 FROM devices
                 WHERE owner_discord_user_id = ? AND revoked_at IS NULL
                 LIMIT 1
                """,
                (owner,),
            ).fetchone()
            if linked is not None:
                raise StorePairingAlreadyLinked("A Remote device is already linked.")
            self._connection.execute(
                """
                UPDATE pairing_tickets
                   SET invalidated_at = ?
                 WHERE owner_discord_user_id = ?
                   AND consumed_at IS NULL AND invalidated_at IS NULL
                """,
                (created_at, owner),
            )
            try:
                self._connection.execute(
                    """
                    INSERT INTO pairing_tickets (
                        ticket_id, owner_discord_user_id, token_hash,
                        created_at, expires_at
                    ) VALUES (?, ?, ?, ?, ?)
                    """,
                    (ticket_id, owner, token_digest, created_at, expires_at),
                )
            except sqlite3.IntegrityError as exc:
                raise StoreConflict("Could not allocate a pairing ticket.") from exc

    def record_pairing_rate_attempt(
        self,
        *,
        occurred_at: str,
        rate_rules: tuple[PairingRateRule, ...],
    ) -> None:
        """Persist rate-limit events before parsing, lookup, or rejection."""

        with self._lock, self._connection:
            self._enforce_pairing_rate_rules(rate_rules, occurred_at)

    def redeem_pairing_ticket(
        self, *, token_digest: bytes, consumed_at: str
    ) -> ProvisionedDevice:
        """Atomically consume a ticket and create exactly one device credential."""

        if len(token_digest) != 32:
            raise ValueError("Pairing token digests must be 32 bytes.")
        device_id = str(uuid.uuid4())
        secret = secrets.token_urlsafe(32)
        credential = f"urad_v1.{device_id}.{secret}"
        credential_digest = _credential_digest(credential)
        invalidated_for_owner_conflict = False
        with self._lock, self._connection:
            conflict = self._connection.execute(
                """
                UPDATE pairing_tickets
                   SET invalidated_at = ?
                 WHERE token_hash = ?
                   AND consumed_at IS NULL
                   AND invalidated_at IS NULL
                   AND expires_at > ?
                   AND EXISTS (
                       SELECT 1 FROM devices
                        WHERE devices.owner_discord_user_id =
                              pairing_tickets.owner_discord_user_id
                          AND devices.revoked_at IS NULL
                   )
                RETURNING ticket_id
                """,
                (consumed_at, token_digest, consumed_at),
            ).fetchone()
            invalidated_for_owner_conflict = conflict is not None
            row = None
            if not invalidated_for_owner_conflict:
                row = self._connection.execute(
                    """
                    UPDATE pairing_tickets
                       SET consumed_at = ?
                     WHERE token_hash = ?
                       AND consumed_at IS NULL
                       AND invalidated_at IS NULL
                       AND expires_at > ?
                       AND NOT EXISTS (
                           SELECT 1 FROM devices
                            WHERE devices.owner_discord_user_id =
                                  pairing_tickets.owner_discord_user_id
                              AND devices.revoked_at IS NULL
                       )
                    RETURNING owner_discord_user_id
                    """,
                    (consumed_at, token_digest, consumed_at),
                ).fetchone()
                if row is None:
                    raise StorePairingInvalid("Pairing ticket is not usable.")
                try:
                    self._connection.execute(
                        """
                        INSERT INTO devices (
                            device_id, owner_discord_user_id, credential_hash, created_at
                        ) VALUES (?, ?, ?, ?)
                        """,
                        (
                            device_id,
                            row["owner_discord_user_id"],
                            credential_digest,
                            consumed_at,
                        ),
                    )
                except sqlite3.IntegrityError as exc:
                    raise StoreConflict(
                        "Could not allocate a device credential."
                    ) from exc
        if invalidated_for_owner_conflict:
            raise StorePairingInvalid("Pairing ticket is not usable.")
        return ProvisionedDevice(
            device=self.get_device(device_id), credential=credential
        )

    def _enforce_pairing_rate_rules(
        self, rate_rules: tuple[PairingRateRule, ...], occurred_at: str
    ) -> None:
        for rule in rate_rules:
            if (
                not rule.scope
                or len(rule.subject_hash) != 32
                or rule.window_seconds <= 0
                or rule.limit <= 0
            ):
                raise ValueError("Invalid pairing rate rule.")
            self._connection.execute(
                """
                DELETE FROM pairing_rate_events
                 WHERE scope = ? AND occurred_at <= ?
                """,
                (rule.scope, rule.window_started_at),
            )
            rows = self._connection.execute(
                """
                SELECT occurred_at FROM pairing_rate_events
                 WHERE scope = ? AND subject_hash = ? AND occurred_at > ?
                 ORDER BY occurred_at, event_id
                """,
                (rule.scope, rule.subject_hash, rule.window_started_at),
            ).fetchall()
            if len(rows) >= rule.limit:
                retry_at = parse_utc(rows[0]["occurred_at"]).timestamp()
                retry_at += rule.window_seconds
                now = parse_utc(occurred_at).timestamp()
                retry_seconds = int(max(1, retry_at - now + 0.999))
                raise StoreRateLimited(min(rule.window_seconds, retry_seconds))
        for rule in rate_rules:
            self._connection.execute(
                """
                INSERT INTO pairing_rate_events (scope, subject_hash, occurred_at)
                VALUES (?, ?, ?)
                """,
                (rule.scope, rule.subject_hash, occurred_at),
            )

    def authenticate(self, credential: str) -> DeviceRecord | None:
        parsed = _parse_credential(credential)
        if parsed is None:
            return None
        device_id = parsed
        supplied_digest = _credential_digest(credential)
        with self._lock:
            row = self._connection.execute(
                "SELECT * FROM devices WHERE device_id = ?", (device_id,)
            ).fetchone()
        expected_digest = (
            bytes(row["credential_hash"]) if row is not None else bytes(32)
        )
        valid = hmac.compare_digest(supplied_digest, expected_digest)
        if not valid or row is None or row["revoked_at"] is not None:
            return None
        return _device_from_row(row)

    def revoke_device(self, device_id: str) -> tuple[CommandRecord, ...]:
        now = format_utc(utc_now())
        with self._lock, self._connection:
            cursor = self._connection.execute(
                """
                UPDATE devices
                   SET revoked_at = ?, connected = 0
                 WHERE device_id = ? AND revoked_at IS NULL
                """,
                (now, device_id),
            )
            if cursor.rowcount == 1:
                rows = self._connection.execute(
                    """
                    SELECT command_id FROM commands
                     WHERE device_id = ? AND status IN (?, ?, ?, ?)
                    """,
                    (
                        device_id,
                        CommandStatus.QUEUED.value,
                        CommandStatus.ACCEPTED.value,
                        CommandStatus.EXECUTING.value,
                        CommandStatus.RECONCILING.value,
                    ),
                ).fetchall()
                self._connection.execute(
                    """
                    UPDATE commands
                       SET status = ?, updated_at = ?, error_code = ?, error_message = ?
                     WHERE device_id = ? AND status IN (?, ?, ?, ?)
                    """,
                    (
                        CommandStatus.FAILED.value,
                        now,
                        "DEVICE_REVOKED_OUTCOME_UNKNOWN",
                        "Device was revoked; any local side effect has unknown outcome.",
                        device_id,
                        CommandStatus.QUEUED.value,
                        CommandStatus.ACCEPTED.value,
                        CommandStatus.EXECUTING.value,
                        CommandStatus.RECONCILING.value,
                    ),
                )
        if cursor.rowcount != 1:
            raise StoreNotFound("Active device was not found.")
        return tuple(self.get_command(row["command_id"]) for row in rows)

    def get_device(self, device_id: str) -> DeviceRecord:
        with self._lock:
            row = self._connection.execute(
                "SELECT * FROM devices WHERE device_id = ?", (device_id,)
            ).fetchone()
        if row is None:
            raise StoreNotFound("Device was not found.")
        return _device_from_row(row)

    def list_devices_for_owner(
        self, owner_discord_user_id: str | int
    ) -> tuple[DeviceRecord, ...]:
        owner = validate_discord_user_id(owner_discord_user_id)
        with self._lock:
            rows = self._connection.execute(
                """
                SELECT * FROM devices
                 WHERE owner_discord_user_id = ? AND revoked_at IS NULL
                 ORDER BY created_at, device_id
                """,
                (owner,),
            ).fetchall()
        return tuple(_device_from_row(row) for row in rows)

    def update_presence(
        self,
        *,
        device_id: str,
        snapshot: MacroSnapshot,
        connected: bool,
        agent_version: str | None = None,
        supported_operations: tuple[Operation, ...] | None = None,
    ) -> DeviceRecord:
        now = format_utc(utc_now())
        assignments = [
            "connected = ?",
            "last_seen_at = ?",
            "macro_state = ?",
            "roblox_running = ?",
            "current_strategy_id = ?",
        ]
        values: list[object] = [
            int(connected),
            now,
            snapshot.macro_state.value,
            int(snapshot.roblox_running),
            snapshot.current_strategy_id,
        ]
        if agent_version is not None:
            assignments.extend(["agent_version = ?", "protocol_version = ?"])
            values.extend([agent_version, PROTOCOL_VERSION])
        if supported_operations is not None:
            assignments.append("supported_operations_json = ?")
            values.append(_json([operation.value for operation in supported_operations]))
        values.append(device_id)
        with self._lock, self._connection:
            cursor = self._connection.execute(
                f"UPDATE devices SET {', '.join(assignments)} "
                "WHERE device_id = ? AND revoked_at IS NULL",
                values,
            )
        if cursor.rowcount != 1:
            raise StoreNotFound("Active device was not found.")
        return self.get_device(device_id)

    def mark_offline(self, device_id: str) -> None:
        with self._lock, self._connection:
            self._connection.execute(
                "UPDATE devices SET connected = 0 WHERE device_id = ?", (device_id,)
            )

    def touch_device(self, device_id: str) -> None:
        with self._lock, self._connection:
            cursor = self._connection.execute(
                """
                UPDATE devices SET connected = 1, last_seen_at = ?
                 WHERE device_id = ? AND revoked_at IS NULL
                """,
                (format_utc(utc_now()), device_id),
            )
        if cursor.rowcount != 1:
            raise StoreNotFound("Active device was not found.")

    def resolve_device_disconnect(
        self, *, device_id: str, code: str, message: str
    ) -> tuple[CommandRecord, ...]:
        now = format_utc(utc_now())
        with self._lock, self._connection:
            rows = self._connection.execute(
                """
                SELECT command_id FROM commands
                 WHERE device_id = ? AND status IN (?, ?, ?)
                """,
                (
                    device_id,
                    CommandStatus.QUEUED.value,
                    CommandStatus.ACCEPTED.value,
                    CommandStatus.EXECUTING.value,
                ),
            ).fetchall()
            self._connection.execute(
                """
                UPDATE commands
                   SET status = ?, updated_at = ?, error_code = ?, error_message = ?
                 WHERE device_id = ? AND status IN (?, ?, ?)
                   AND operation IN ('START_STRATEGY', 'STOP_SAFE', 'SWITCH_STRATEGY')
                """,
                (
                    CommandStatus.RECONCILING.value,
                    now,
                    code,
                    message,
                    device_id,
                    CommandStatus.QUEUED.value,
                    CommandStatus.ACCEPTED.value,
                    CommandStatus.EXECUTING.value,
                ),
            )
            self._connection.execute(
                """
                UPDATE commands
                   SET status = ?, updated_at = ?, error_code = ?, error_message = ?
                 WHERE device_id = ? AND status IN (?, ?, ?)
                   AND operation IN ('GET_STATUS', 'LIST_STRATEGIES')
                """,
                (
                    CommandStatus.FAILED.value,
                    now,
                    code.replace("OUTCOME_UNKNOWN", "READ_FAILED"),
                    "Agent disconnected before the read completed.",
                    device_id,
                    CommandStatus.QUEUED.value,
                    CommandStatus.ACCEPTED.value,
                    CommandStatus.EXECUTING.value,
                ),
            )
        return tuple(self.get_command(row["command_id"]) for row in rows)

    def mark_command_reconciling(
        self, *, command_id: str, device_id: str, code: str, message: str
    ) -> CommandRecord:
        return self.transition_command(
            command_id=command_id,
            device_id=device_id,
            status=CommandStatus.RECONCILING,
            error_code=code,
            error_message=message,
        )

    def list_reconciling_commands(
        self, device_id: str
    ) -> tuple[CommandRecord, ...]:
        with self._lock:
            rows = self._connection.execute(
                """
                SELECT * FROM commands
                 WHERE device_id = ? AND status = ?
                 ORDER BY created_at, command_id
                """,
                (device_id, CommandStatus.RECONCILING.value),
            ).fetchall()
        return tuple(_command_from_row(row) for row in rows)

    def create_command(
        self,
        *,
        device_id: str,
        owner_discord_user_id: str | int,
        operation: Operation,
        arguments: Mapping[str, object],
        expires_at: str,
    ) -> CommandRecord:
        owner = validate_discord_user_id(owner_discord_user_id)
        normalized_arguments = validate_command_arguments(operation, arguments)
        command_id = str(uuid.uuid4())
        now = format_utc(utc_now())
        with self._lock, self._connection:
            try:
                cursor = self._connection.execute(
                    """
                    INSERT INTO commands (
                        command_id, device_id, owner_discord_user_id, operation,
                        arguments_json, status, created_at, updated_at, expires_at
                    )
                    SELECT ?, device_id, ?, ?, ?, ?, ?, ?, ?
                      FROM devices
                     WHERE device_id = ? AND owner_discord_user_id = ?
                       AND revoked_at IS NULL AND connected = 1
                    """,
                    (
                        command_id,
                        owner,
                        operation.value,
                        _json(normalized_arguments),
                        CommandStatus.QUEUED.value,
                        now,
                        now,
                        expires_at,
                        device_id,
                        owner,
                    ),
                )
            except sqlite3.IntegrityError as exc:
                if operation in MUTATING_OPERATIONS:
                    raise StoreConflict(
                        "Another gameplay-changing command is already active."
                    ) from exc
                raise
            if cursor.rowcount != 1:
                raise StoreAuthorizationError(
                    "Device is offline, revoked, or belongs to another user."
                )
        return self.get_command(command_id)

    def get_command(self, command_id: str) -> CommandRecord:
        with self._lock:
            row = self._connection.execute(
                "SELECT * FROM commands WHERE command_id = ?", (command_id,)
            ).fetchone()
        if row is None:
            raise StoreNotFound("Command was not found.")
        return _command_from_row(row)

    def transition_command(
        self,
        *,
        command_id: str,
        device_id: str,
        status: CommandStatus,
        result: Mapping[str, object] | None = None,
        error_code: str | None = None,
        error_message: str | None = None,
    ) -> CommandRecord:
        command = self.get_command(command_id)
        if command.device_id != device_id:
            raise StoreAuthorizationError("Command belongs to another device.")
        normalized_result = dict(result) if result is not None else None
        if status is command.status:
            if (
                command.result == normalized_result
                and command.error_code == error_code
                and command.error_message == error_message
            ):
                return command
            raise InvalidTransition("Conflicting replay for the current command status.")
        if status not in _allowed_next_statuses(command.status):
            raise InvalidTransition(
                f"Cannot transition {command.status.value} to {status.value}."
            )
        if status in {CommandStatus.FAILED, CommandStatus.RECONCILING}:
            if not error_code or not error_message:
                raise InvalidTransition(
                    "Failed or reconciling commands require a sanitized reason."
                )
        elif error_code is not None or error_message is not None:
            raise InvalidTransition(
                "Only failed or reconciling commands may store an error."
            )
        if status is not CommandStatus.COMPLETED and result is not None:
            raise InvalidTransition("Only completed commands may store a result.")

        now = format_utc(utc_now())
        with self._lock, self._connection:
            cursor = self._connection.execute(
                """
                UPDATE commands
                   SET status = ?, updated_at = ?, result_json = ?,
                       error_code = ?, error_message = ?
                 WHERE command_id = ? AND status = ?
                """,
                (
                    status.value,
                    now,
                    _json(normalized_result) if normalized_result is not None else None,
                    error_code,
                    error_message,
                    command_id,
                    command.status.value,
                ),
            )
        if cursor.rowcount != 1:
            raise InvalidTransition("Command changed concurrently.")
        return self.get_command(command_id)

    def get_command_for_owner(
        self, command_id: str, owner_discord_user_id: str | int
    ) -> CommandRecord:
        owner = validate_discord_user_id(owner_discord_user_id)
        command = self.get_command(command_id)
        if command.owner_discord_user_id != owner:
            raise StoreAuthorizationError("Command belongs to another user.")
        return command

    def raw_connection_for_tests(self) -> sqlite3.Connection:
        """Return the connection for assertions; application code must not use it."""

        return self._connection


def _allowed_next_statuses(status: CommandStatus) -> frozenset[CommandStatus]:
    if status is CommandStatus.QUEUED:
        return frozenset(
            {
                CommandStatus.ACCEPTED,
                CommandStatus.RECONCILING,
                CommandStatus.FAILED,
            }
        )
    if status is CommandStatus.ACCEPTED:
        return frozenset(
            {
                CommandStatus.EXECUTING,
                CommandStatus.RECONCILING,
                CommandStatus.FAILED,
            }
        )
    if status is CommandStatus.EXECUTING:
        return TERMINAL_COMMAND_STATUSES | {CommandStatus.RECONCILING}
    if status is CommandStatus.RECONCILING:
        return frozenset(
            {
                CommandStatus.ACCEPTED,
                CommandStatus.EXECUTING,
                CommandStatus.COMPLETED,
                CommandStatus.FAILED,
            }
        )
    return frozenset()


def _parse_credential(credential: object) -> str | None:
    if not isinstance(credential, str):
        return None
    match = _CREDENTIAL_RE.fullmatch(credential)
    if match is None:
        return None
    try:
        return str(uuid.UUID(match.group(1)))
    except ValueError:
        return None


def _credential_digest(credential: str) -> bytes:
    return hashlib.sha256(credential.encode("utf-8")).digest()


def _device_from_row(row: sqlite3.Row) -> DeviceRecord:
    raw_operations = json.loads(row["supported_operations_json"])
    return DeviceRecord(
        device_id=row["device_id"],
        owner_discord_user_id=row["owner_discord_user_id"],
        revoked=row["revoked_at"] is not None,
        connected=bool(row["connected"]),
        created_at=row["created_at"],
        last_seen_at=row["last_seen_at"],
        agent_version=row["agent_version"],
        protocol_version=row["protocol_version"],
        supported_operations=tuple(Operation(value) for value in raw_operations),
        macro_state=MacroState(row["macro_state"]),
        roblox_running=bool(row["roblox_running"]),
        current_strategy_id=row["current_strategy_id"],
    )


def _command_from_row(row: sqlite3.Row) -> CommandRecord:
    return CommandRecord(
        command_id=row["command_id"],
        device_id=row["device_id"],
        owner_discord_user_id=row["owner_discord_user_id"],
        operation=Operation(row["operation"]),
        arguments=json.loads(row["arguments_json"]),
        status=CommandStatus(row["status"]),
        created_at=row["created_at"],
        updated_at=row["updated_at"],
        expires_at=row["expires_at"],
        result=json.loads(row["result_json"]) if row["result_json"] else None,
        error_code=row["error_code"],
        error_message=row["error_message"],
    )


def _json(value: object) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True)


class _DatabaseFileLock:
    """Process-lifetime advisory lock enforcing one RemoteStore per database."""

    def __init__(self, path: Path) -> None:
        self._file = path.open("a+b")
        self._locked = False
        try:
            self._file.seek(0, os.SEEK_END)
            if self._file.tell() == 0:
                self._file.write(b"\0")
                self._file.flush()
            self._file.seek(0)
            if os.name == "nt":
                import msvcrt

                msvcrt.locking(self._file.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl

                fcntl.flock(self._file.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            self._locked = True
        except OSError as exc:
            self._file.close()
            raise StoreConflict(
                "The Remote database is already open by another backend instance."
            ) from exc

    def close(self) -> None:
        if self._file.closed:
            return
        try:
            if self._locked:
                self._file.seek(0)
                if os.name == "nt":
                    import msvcrt

                    msvcrt.locking(self._file.fileno(), msvcrt.LK_UNLCK, 1)
                else:
                    import fcntl

                    fcntl.flock(self._file.fileno(), fcntl.LOCK_UN)
        finally:
            self._locked = False
            self._file.close()
