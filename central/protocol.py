from __future__ import annotations

import json
import re
import unicodedata
import uuid
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from enum import Enum
from typing import Any, Mapping, Sequence


PROTOCOL_VERSION = 1
MAX_MESSAGE_BYTES = 64 * 1024
MAX_STRATEGIES = 500


class Operation(str, Enum):
    GET_STATUS = "GET_STATUS"
    LIST_STRATEGIES = "LIST_STRATEGIES"
    START_STRATEGY = "START_STRATEGY"
    STOP_SAFE = "STOP_SAFE"
    SWITCH_STRATEGY = "SWITCH_STRATEGY"


RESERVED_OPERATIONS = frozenset(
    {
        "RESTART_MACRO",
        "GET_AUTO_EQUIP",
        "SET_AUTO_EQUIP",
        "GET_DIAGNOSTICS",
    }
)
FORBIDDEN_OPERATION_NAMES = frozenset(
    {
        "EXEC",
        "SHELL",
        "CMD",
        "POWERSHELL",
        "RUN_ARBITRARY",
        "DOWNLOAD_AND_EXECUTE",
        "FILE_BROWSER",
        "REMOTE_DESKTOP",
    }
)
MUTATING_OPERATIONS = frozenset(
    {Operation.START_STRATEGY, Operation.STOP_SAFE, Operation.SWITCH_STRATEGY}
)


class CommandStatus(str, Enum):
    QUEUED = "queued"
    ACCEPTED = "accepted"
    EXECUTING = "executing"
    RECONCILING = "reconciling"
    COMPLETED = "completed"
    FAILED = "failed"


AGENT_COMMAND_STATUSES = frozenset(
    {
        CommandStatus.ACCEPTED,
        CommandStatus.EXECUTING,
        CommandStatus.COMPLETED,
        CommandStatus.FAILED,
    }
)
TERMINAL_COMMAND_STATUSES = frozenset(
    {CommandStatus.COMPLETED, CommandStatus.FAILED}
)
ACTIVE_COMMAND_STATUSES = frozenset(
    {
        CommandStatus.QUEUED,
        CommandStatus.ACCEPTED,
        CommandStatus.EXECUTING,
        CommandStatus.RECONCILING,
    }
)


class MacroState(str, Enum):
    NOT_RUNNING = "not_running"
    IDLE = "idle"
    RUNNING = "running"
    UNKNOWN = "unknown"


class ActionResult(str, Enum):
    STRATEGY_STARTED = "strategy_started"
    STOPPED_SAFE = "stopped_safe"
    SWITCHED_SAFE = "switched_safe"


class ProtocolError(ValueError):
    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


@dataclass(frozen=True, slots=True)
class StrategySummary:
    strategy_id: str
    name: str

    def to_wire(self) -> dict[str, str]:
        return {"strategy_id": self.strategy_id, "name": self.name}


@dataclass(frozen=True, slots=True)
class MacroSnapshot:
    macro_state: MacroState
    roblox_running: bool
    current_strategy_id: str | None

    def to_wire(self) -> dict[str, Any]:
        return {
            "macro_state": self.macro_state.value,
            "roblox_running": self.roblox_running,
            "current_strategy_id": self.current_strategy_id,
        }


@dataclass(frozen=True, slots=True)
class HelloMessage:
    agent_version: str
    supported_operations: tuple[Operation, ...]
    snapshot: MacroSnapshot


@dataclass(frozen=True, slots=True)
class HeartbeatMessage:
    snapshot: MacroSnapshot


@dataclass(frozen=True, slots=True)
class CommandError:
    code: str
    message: str

    def to_wire(self) -> dict[str, str]:
        return {"code": self.code, "message": self.message}


@dataclass(frozen=True, slots=True)
class CommandUpdateMessage:
    command_id: str
    status: CommandStatus
    snapshot: MacroSnapshot | None = None
    strategies: tuple[StrategySummary, ...] | None = None
    action_result: ActionResult | None = None
    error: CommandError | None = None


AgentMessage = HelloMessage | HeartbeatMessage | CommandUpdateMessage


_STRATEGY_ID_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9_-]{7,63}\Z")
_ERROR_CODE_RE = re.compile(r"[A-Z][A-Z0-9_]{0,63}\Z")
_DISCORD_USER_ID_RE = re.compile(r"[1-9][0-9]{0,19}\Z")


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


def format_utc(value: datetime) -> str:
    if value.tzinfo is None or value.utcoffset() is None:
        raise ValueError("Timestamp must be timezone-aware.")
    return value.astimezone(timezone.utc).isoformat(timespec="milliseconds").replace(
        "+00:00", "Z"
    )


def parse_utc(value: str) -> datetime:
    if not isinstance(value, str) or not value.endswith("Z"):
        raise ProtocolError("INVALID_TIMESTAMP", "Timestamp must be UTC RFC3339 text.")
    try:
        parsed = datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError as exc:
        raise ProtocolError("INVALID_TIMESTAMP", "Timestamp is invalid.") from exc
    if parsed.utcoffset() != timedelta(0):
        raise ProtocolError("INVALID_TIMESTAMP", "Timestamp must be UTC.")
    return parsed


def validate_discord_user_id(value: str | int) -> str:
    normalized = str(value)
    if not _DISCORD_USER_ID_RE.fullmatch(normalized):
        raise ProtocolError("INVALID_DISCORD_USER_ID", "Invalid Discord user ID.")
    return normalized


def validate_strategy_id(value: object) -> str:
    if not isinstance(value, str) or not _STRATEGY_ID_RE.fullmatch(value):
        raise ProtocolError(
            "INVALID_STRATEGY_ID",
            "Strategy ID must be an opaque 8-64 character identifier.",
        )
    return value


def validate_command_arguments(
    operation: Operation, arguments: Mapping[str, object]
) -> dict[str, str]:
    if not isinstance(arguments, Mapping):
        raise ProtocolError("INVALID_ARGUMENTS", "Command arguments must be an object.")

    if operation in {Operation.START_STRATEGY, Operation.SWITCH_STRATEGY}:
        _expect_keys(arguments, {"strategy_id"})
        return {"strategy_id": validate_strategy_id(arguments["strategy_id"])}

    _expect_keys(arguments, set())
    return {}


def parse_agent_message(payload: str | bytes) -> AgentMessage:
    document = _load_json_object(payload)
    protocol = document.get("protocol")
    if type(protocol) is not int or protocol != PROTOCOL_VERSION:
        raise ProtocolError(
            "UNSUPPORTED_PROTOCOL", f"Only protocol {PROTOCOL_VERSION} is supported."
        )

    message_type = document.get("type")
    if message_type == "HELLO":
        return _parse_hello(document)
    if message_type == "HEARTBEAT":
        return _parse_heartbeat(document)
    if message_type == "COMMAND_UPDATE":
        return _parse_command_update(document)
    raise ProtocolError("UNKNOWN_MESSAGE_TYPE", "Unknown Agent message type.")


def encode_welcome(
    heartbeat_interval_seconds: int,
    server_time: datetime,
    reconcile_commands: Sequence[tuple[str, Operation]] = (),
) -> str:
    if heartbeat_interval_seconds <= 0:
        raise ValueError("Heartbeat interval must be positive.")
    pending: list[dict[str, str]] = []
    seen: set[str] = set()
    for command_id, operation in reconcile_commands:
        normalized_id = _validate_uuid(command_id)
        if normalized_id in seen:
            raise ValueError("Duplicate reconciliation command ID.")
        if not isinstance(operation, Operation):
            raise ProtocolError("UNSUPPORTED_OPERATION", "Unsupported operation.")
        seen.add(normalized_id)
        pending.append(
            {"command_id": normalized_id, "operation": operation.value}
        )
    return _encode(
        {
            "protocol": PROTOCOL_VERSION,
            "type": "WELCOME",
            "heartbeat_interval_seconds": heartbeat_interval_seconds,
            "server_time": format_utc(server_time),
            "reconcile_commands": pending,
        }
    )


def encode_command(
    *,
    command_id: str,
    operation: Operation,
    arguments: Mapping[str, object],
    issued_at: datetime,
    expires_at: datetime,
) -> str:
    if not isinstance(operation, Operation):
        raise ProtocolError("UNSUPPORTED_OPERATION", "Unsupported operation.")
    normalized_id = _validate_uuid(command_id)
    normalized_arguments = validate_command_arguments(operation, arguments)
    if expires_at <= issued_at:
        raise ValueError("Command expiry must be later than issue time.")
    return _encode(
        {
            "protocol": PROTOCOL_VERSION,
            "type": "COMMAND",
            "command_id": normalized_id,
            "operation": operation.value,
            "issued_at": format_utc(issued_at),
            "expires_at": format_utc(expires_at),
            "arguments": normalized_arguments,
        }
    )


def _parse_hello(document: Mapping[str, object]) -> HelloMessage:
    _expect_keys(
        document,
        {"protocol", "type", "agent_version", "supported_operations", "snapshot"},
    )
    agent_version = _clean_text(
        document["agent_version"], "INVALID_AGENT_VERSION", 1, 64
    )
    raw_operations = document["supported_operations"]
    if not isinstance(raw_operations, list) or not raw_operations:
        raise ProtocolError(
            "INVALID_CAPABILITIES", "supported_operations must be a non-empty array."
        )
    if len(raw_operations) > len(Operation):
        raise ProtocolError("INVALID_CAPABILITIES", "Too many supported operations.")

    operations: list[Operation] = []
    for raw_operation in raw_operations:
        if not isinstance(raw_operation, str):
            raise ProtocolError(
                "INVALID_CAPABILITIES", "Operation names must be strings."
            )
        try:
            operation = Operation(raw_operation)
        except ValueError as exc:
            raise ProtocolError(
                "UNSUPPORTED_OPERATION", "Agent advertised an unsupported operation."
            ) from exc
        if operation in operations:
            raise ProtocolError("INVALID_CAPABILITIES", "Duplicate operation.")
        operations.append(operation)

    return HelloMessage(
        agent_version=agent_version,
        supported_operations=tuple(operations),
        snapshot=_parse_snapshot(document["snapshot"]),
    )


def _parse_heartbeat(document: Mapping[str, object]) -> HeartbeatMessage:
    _expect_keys(document, {"protocol", "type", "snapshot"})
    return HeartbeatMessage(snapshot=_parse_snapshot(document["snapshot"]))


def _parse_command_update(document: Mapping[str, object]) -> CommandUpdateMessage:
    base_keys = {"protocol", "type", "command_id", "status"}
    if not base_keys.issubset(document):
        raise ProtocolError("MISSING_FIELD", "Command update is missing a field.")

    command_id = _validate_uuid(document["command_id"])
    try:
        status = CommandStatus(document["status"])
    except (TypeError, ValueError) as exc:
        raise ProtocolError("INVALID_COMMAND_STATUS", "Invalid command status.") from exc
    if status not in AGENT_COMMAND_STATUSES:
        raise ProtocolError(
            "INVALID_COMMAND_STATUS", "The Agent cannot report queued status."
        )

    if status in {CommandStatus.ACCEPTED, CommandStatus.EXECUTING}:
        _expect_keys(document, base_keys)
        return CommandUpdateMessage(command_id=command_id, status=status)

    if status is CommandStatus.FAILED:
        _expect_keys(document, base_keys | {"error"})
        return CommandUpdateMessage(
            command_id=command_id,
            status=status,
            error=_parse_command_error(document["error"]),
        )

    _expect_keys(
        document, base_keys, {"snapshot", "strategies", "action_result"}
    )
    has_snapshot = "snapshot" in document
    has_strategies = "strategies" in document
    if has_snapshot == has_strategies:
        raise ProtocolError(
            "INVALID_COMMAND_RESULT",
            "Completed commands require exactly one typed result.",
        )
    action_result: ActionResult | None = None
    if "action_result" in document:
        if not has_snapshot:
            raise ProtocolError(
                "INVALID_COMMAND_RESULT",
                "Only action snapshot results may carry action_result.",
            )
        try:
            action_result = ActionResult(document["action_result"])
        except (TypeError, ValueError) as exc:
            raise ProtocolError(
                "INVALID_COMMAND_RESULT", "Unknown action result."
            ) from exc
    return CommandUpdateMessage(
        command_id=command_id,
        status=status,
        snapshot=_parse_snapshot(document["snapshot"]) if has_snapshot else None,
        strategies=(
            _parse_strategies(document["strategies"]) if has_strategies else None
        ),
        action_result=action_result,
    )


def _parse_snapshot(value: object) -> MacroSnapshot:
    if not isinstance(value, dict):
        raise ProtocolError("INVALID_SNAPSHOT", "Snapshot must be an object.")
    _expect_keys(
        value, {"macro_state", "roblox_running", "current_strategy_id"}
    )
    try:
        macro_state = MacroState(value["macro_state"])
    except (TypeError, ValueError) as exc:
        raise ProtocolError("INVALID_SNAPSHOT", "Invalid macro state.") from exc
    if type(value["roblox_running"]) is not bool:
        raise ProtocolError("INVALID_SNAPSHOT", "Roblox state must be boolean.")
    current_strategy = value["current_strategy_id"]
    if current_strategy is not None:
        current_strategy = validate_strategy_id(current_strategy)
    return MacroSnapshot(macro_state, value["roblox_running"], current_strategy)


def _parse_strategies(value: object) -> tuple[StrategySummary, ...]:
    if not isinstance(value, list) or len(value) > MAX_STRATEGIES:
        raise ProtocolError("INVALID_STRATEGIES", "Invalid strategy list.")
    result: list[StrategySummary] = []
    identifiers: set[str] = set()
    for item in value:
        if not isinstance(item, dict):
            raise ProtocolError("INVALID_STRATEGIES", "Strategy must be an object.")
        _expect_keys(item, {"strategy_id", "name"})
        strategy_id = validate_strategy_id(item["strategy_id"])
        name = _clean_text(item["name"], "INVALID_STRATEGIES", 1, 200)
        if any(character in name for character in ("/", "\\", ":")):
            raise ProtocolError(
                "INVALID_STRATEGIES", "Strategy display names cannot contain paths."
            )
        if strategy_id in identifiers:
            raise ProtocolError("INVALID_STRATEGIES", "Duplicate strategy ID.")
        identifiers.add(strategy_id)
        result.append(StrategySummary(strategy_id, name))
    return tuple(result)


def _parse_command_error(value: object) -> CommandError:
    if not isinstance(value, dict):
        raise ProtocolError("INVALID_COMMAND_ERROR", "Error must be an object.")
    _expect_keys(value, {"code", "message"})
    code = value["code"]
    if not isinstance(code, str) or not _ERROR_CODE_RE.fullmatch(code):
        raise ProtocolError("INVALID_COMMAND_ERROR", "Invalid error code.")
    message = _clean_text(value["message"], "INVALID_COMMAND_ERROR", 1, 500)
    return CommandError(code, message)


def _load_json_object(payload: str | bytes) -> dict[str, object]:
    if isinstance(payload, bytes):
        if len(payload) > MAX_MESSAGE_BYTES:
            raise ProtocolError("MESSAGE_TOO_LARGE", "Message exceeds size limit.")
        try:
            text = payload.decode("utf-8", errors="strict")
        except UnicodeDecodeError as exc:
            raise ProtocolError("INVALID_JSON", "Message must be UTF-8 JSON.") from exc
    elif isinstance(payload, str):
        if len(payload.encode("utf-8")) > MAX_MESSAGE_BYTES:
            raise ProtocolError("MESSAGE_TOO_LARGE", "Message exceeds size limit.")
        text = payload
    else:
        raise ProtocolError("INVALID_JSON", "Message must be text JSON.")

    try:
        value = json.loads(text, object_pairs_hook=_unique_object)
    except ProtocolError:
        raise
    except (json.JSONDecodeError, UnicodeError) as exc:
        raise ProtocolError("INVALID_JSON", "Malformed JSON.") from exc
    if not isinstance(value, dict):
        raise ProtocolError("INVALID_JSON", "Message root must be an object.")
    return value


def _unique_object(pairs: Sequence[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise ProtocolError("DUPLICATE_FIELD", "Duplicate JSON field.")
        result[key] = value
    return result


def _expect_keys(
    value: Mapping[str, object],
    required: set[str],
    optional: set[str] | None = None,
) -> None:
    optional = optional or set()
    keys = set(value)
    missing = required - keys
    extra = keys - required - optional
    if missing:
        raise ProtocolError("MISSING_FIELD", "Message is missing a required field.")
    if extra:
        raise ProtocolError("UNKNOWN_FIELD", "Message contains an unknown field.")


def _clean_text(value: object, code: str, minimum: int, maximum: int) -> str:
    if not isinstance(value, str) or not minimum <= len(value) <= maximum:
        raise ProtocolError(code, "Text field has an invalid length.")
    if value != value.strip() or any(
        unicodedata.category(character).startswith("C") for character in value
    ):
        raise ProtocolError(code, "Text field contains invalid characters.")
    return value


def _validate_uuid(value: object) -> str:
    if not isinstance(value, str):
        raise ProtocolError("INVALID_COMMAND_ID", "Command ID must be a UUID.")
    try:
        parsed = uuid.UUID(value)
    except (ValueError, AttributeError) as exc:
        raise ProtocolError("INVALID_COMMAND_ID", "Command ID must be a UUID.") from exc
    canonical = str(parsed)
    if value != canonical:
        raise ProtocolError("INVALID_COMMAND_ID", "Command ID must be canonical.")
    return canonical


def _encode(value: Mapping[str, object]) -> str:
    encoded = json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True)
    if len(encoded.encode("utf-8")) > MAX_MESSAGE_BYTES:
        raise ProtocolError("MESSAGE_TOO_LARGE", "Message exceeds size limit.")
    return encoded
