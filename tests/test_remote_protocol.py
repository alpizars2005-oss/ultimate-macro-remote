from __future__ import annotations

import json
import unittest
import uuid
from datetime import timedelta

from central.protocol import (
    FORBIDDEN_OPERATION_NAMES,
    MAX_MESSAGE_BYTES,
    RESERVED_OPERATIONS,
    CommandStatus,
    CommandUpdateMessage,
    HelloMessage,
    Operation,
    ProtocolError,
    encode_command,
    encode_welcome,
    parse_agent_message,
    utc_now,
    validate_command_arguments,
)


STRATEGY_ID = "strat_dead_ahead_01"


def snapshot(
    macro_state: str = "idle", current_strategy_id: str | None = None
) -> dict[str, object]:
    return {
        "macro_state": macro_state,
        "roblox_running": False,
        "current_strategy_id": current_strategy_id,
    }


def hello(**overrides: object) -> dict[str, object]:
    value: dict[str, object] = {
        "protocol": 1,
        "type": "HELLO",
        "agent_version": "0.1.0-test",
        "supported_operations": [operation.value for operation in Operation],
        "snapshot": snapshot(),
    }
    value.update(overrides)
    return value


class ProtocolV1Tests(unittest.TestCase):
    def test_parses_strict_hello(self) -> None:
        parsed = parse_agent_message(json.dumps(hello()))
        self.assertIsInstance(parsed, HelloMessage)
        self.assertEqual(tuple(Operation), parsed.supported_operations)

    def test_each_allowlisted_operation_encodes(self) -> None:
        now = utc_now()
        for operation in Operation:
            with self.subTest(operation=operation.value):
                arguments = (
                    {"strategy_id": STRATEGY_ID}
                    if operation
                    in {Operation.START_STRATEGY, Operation.SWITCH_STRATEGY}
                    else {}
                )
                encoded = encode_command(
                    command_id=str(uuid.uuid4()),
                    operation=operation,
                    arguments=arguments,
                    issued_at=now,
                    expires_at=now + timedelta(seconds=30),
                )
                document = json.loads(encoded)
                self.assertEqual(1, document["protocol"])
                self.assertEqual(operation.value, document["operation"])
                self.assertEqual(arguments, document["arguments"])

    def test_reserved_and_forbidden_operations_are_rejected(self) -> None:
        for operation in sorted(RESERVED_OPERATIONS | FORBIDDEN_OPERATION_NAMES):
            with self.subTest(operation=operation):
                payload = hello(supported_operations=[operation])
                with self.assertRaisesRegex(ProtocolError, "unsupported operation"):
                    parse_agent_message(json.dumps(payload))

    def test_strategy_argument_is_opaque_not_a_path(self) -> None:
        rejected = [
            "..\\secret.strat",
            "../secret.strat",
            r"C:\\macro\\secret.strat",
            r"\\\\server\\share\\secret.strat",
            "secret.strat",
            "short",
        ]
        for strategy_id in rejected:
            with self.subTest(strategy_id=strategy_id):
                with self.assertRaises(ProtocolError):
                    validate_command_arguments(
                        Operation.START_STRATEGY, {"strategy_id": strategy_id}
                    )

    def test_non_strategy_operations_reject_arguments(self) -> None:
        with self.assertRaisesRegex(ProtocolError, "unknown field"):
            validate_command_arguments(
                Operation.GET_STATUS, {"command": "whoami"}
            )

    def test_rejects_unknown_and_duplicate_fields(self) -> None:
        value = hello(arbitrary_command="calc.exe")
        with self.assertRaisesRegex(ProtocolError, "unknown field"):
            parse_agent_message(json.dumps(value))

        duplicate = (
            '{"protocol":1,"protocol":1,"type":"HEARTBEAT","snapshot":'
            + json.dumps(snapshot())
            + "}"
        )
        with self.assertRaisesRegex(ProtocolError, "Duplicate JSON field"):
            parse_agent_message(duplicate)

    def test_rejects_wrong_version_malformed_json_and_oversize(self) -> None:
        with self.assertRaisesRegex(ProtocolError, "protocol 1"):
            parse_agent_message(json.dumps(hello(protocol=2)))
        with self.assertRaisesRegex(ProtocolError, "Malformed JSON"):
            parse_agent_message("{")
        with self.assertRaisesRegex(ProtocolError, "size limit"):
            parse_agent_message(" " * (MAX_MESSAGE_BYTES + 1))

    def test_rejects_queued_agent_status(self) -> None:
        update = {
            "protocol": 1,
            "type": "COMMAND_UPDATE",
            "command_id": str(uuid.uuid4()),
            "status": CommandStatus.QUEUED.value,
        }
        with self.assertRaisesRegex(ProtocolError, "cannot report queued"):
            parse_agent_message(json.dumps(update))

    def test_completed_update_has_exactly_one_typed_result(self) -> None:
        base = {
            "protocol": 1,
            "type": "COMMAND_UPDATE",
            "command_id": str(uuid.uuid4()),
            "status": CommandStatus.COMPLETED.value,
        }
        with self.assertRaisesRegex(ProtocolError, "exactly one"):
            parse_agent_message(json.dumps(base))

        complete = base | {"snapshot": snapshot()}
        parsed = parse_agent_message(json.dumps(complete))
        self.assertIsInstance(parsed, CommandUpdateMessage)

        action_complete = complete | {"action_result": "stopped_safe"}
        parsed_action = parse_agent_message(json.dumps(action_complete))
        self.assertEqual("stopped_safe", parsed_action.action_result.value)

        with self.assertRaisesRegex(ProtocolError, "Unknown action result"):
            parse_agent_message(
                json.dumps(complete | {"action_result": "arbitrary_action"})
            )

        ambiguous = complete | {"strategies": []}
        with self.assertRaisesRegex(ProtocolError, "exactly one"):
            parse_agent_message(json.dumps(ambiguous))

        with self.assertRaisesRegex(ProtocolError, "action_result"):
            parse_agent_message(
                json.dumps(base | {"strategies": [], "action_result": "stopped_safe"})
            )

    def test_running_snapshot_may_have_an_unapproved_unknown_strategy(self) -> None:
        parsed = parse_agent_message(
            json.dumps(hello(snapshot=snapshot("running")))
        )
        self.assertIsNone(parsed.snapshot.current_strategy_id)

    def test_welcome_golden_shape_includes_reconciliation_not_replay(self) -> None:
        command_id = "11111111-1111-4111-8111-111111111111"
        document = json.loads(
            encode_welcome(
                30,
                utc_now(),
                ((command_id, Operation.STOP_SAFE),),
            )
        )
        self.assertEqual(
            [{"command_id": command_id, "operation": "STOP_SAFE"}],
            document["reconcile_commands"],
        )
        self.assertNotIn("arguments", document["reconcile_commands"][0])

    def test_strategy_inventory_rejects_paths_and_duplicate_ids(self) -> None:
        base = {
            "protocol": 1,
            "type": "COMMAND_UPDATE",
            "command_id": str(uuid.uuid4()),
            "status": "completed",
        }
        with self.assertRaisesRegex(ProtocolError, "cannot contain paths"):
            parse_agent_message(
                json.dumps(
                    base
                    | {
                        "strategies": [
                            {"strategy_id": STRATEGY_ID, "name": "folder/evil"}
                        ]
                    }
                )
            )
        with self.assertRaisesRegex(ProtocolError, "Duplicate strategy"):
            parse_agent_message(
                json.dumps(
                    base
                    | {
                        "strategies": [
                            {"strategy_id": STRATEGY_ID, "name": "One"},
                            {"strategy_id": STRATEGY_ID, "name": "Two"},
                        ]
                    }
                )
            )


if __name__ == "__main__":
    unittest.main()
