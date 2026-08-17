from __future__ import annotations

import asyncio
import inspect
import json
import tempfile
import unittest
from pathlib import Path

from aiohttp import WSServerHandshakeError
from aiohttp.test_utils import TestClient, TestServer

from central.config import ConfigurationError, RemoteConfig
from central.protocol import (
    CommandStatus,
    MacroState,
    Operation,
    ProtocolError,
    parse_agent_message,
)
from central.server import create_app
from central.service import RemoteService, RemoteServiceError
from central.store import RemoteStore


OWNER = "123456789012345678"
OTHER_OWNER = "223456789012345678"
STRATEGY_ID = "strat_dead_ahead_01"


def snapshot(
    macro_state: str = "idle",
    current_strategy_id: str | None = None,
    roblox_running: bool = False,
) -> dict[str, object]:
    return {
        "macro_state": macro_state,
        "roblox_running": roblox_running,
        "current_strategy_id": current_strategy_id,
    }


def hello() -> dict[str, object]:
    return {
        "protocol": 1,
        "type": "HELLO",
        "agent_version": "0.1.0-simulator",
        "supported_operations": [operation.value for operation in Operation],
        "snapshot": snapshot(),
    }


class RemoteServerTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory()
        database_path = Path(self.temporary_directory.name) / "remote.db"
        self.config = RemoteConfig(
            database_path=database_path,
            first_message_timeout_seconds=1,
            heartbeat_interval_seconds=1,
            heartbeat_timeout_seconds=3,
            command_delivery_ttl_seconds=10,
        )
        self.store = RemoteStore(database_path)
        self.service = RemoteService(
            self.store,
            command_delivery_ttl_seconds=self.config.command_delivery_ttl_seconds,
        )
        self.client = TestClient(
            TestServer(
                create_app(self.config, store=self.store, service=self.service)
            )
        )
        await self.client.start_server()

    async def asyncTearDown(self) -> None:
        await self.client.close()
        self.temporary_directory.cleanup()

    async def test_missing_bad_and_revoked_credentials_fail_before_upgrade(self) -> None:
        provisioned = self.store.provision_device(OWNER)
        for credential in (None, provisioned.credential + "x"):
            headers = {} if credential is None else {"Authorization": f"Bearer {credential}"}
            with self.subTest(credential="missing" if credential is None else "bad"):
                with self.assertRaises(WSServerHandshakeError) as raised:
                    await self.client.ws_connect(
                        "/remote/v1/agent", headers=headers
                    )
                self.assertEqual(401, raised.exception.status)

        self.store.revoke_device(provisioned.device.device_id)
        with self.assertRaises(WSServerHandshakeError) as raised:
            await self.client.ws_connect(
                "/remote/v1/agent",
                headers={"Authorization": f"Bearer {provisioned.credential}"},
            )
        self.assertEqual(401, raised.exception.status)

    async def test_simulated_agent_command_lifecycle(self) -> None:
        provisioned, socket = await self._connect_agent(OWNER)
        command = await self.service.dispatch_for_user(
            discord_user_id=OWNER,
            operation=Operation.START_STRATEGY,
            strategy_id=STRATEGY_ID,
        )
        self.assertEqual(CommandStatus.QUEUED, command.status)
        wire_command = await socket.receive_json()
        self.assertEqual("COMMAND", wire_command["type"])
        self.assertEqual("START_STRATEGY", wire_command["operation"])
        self.assertEqual({"strategy_id": STRATEGY_ID}, wire_command["arguments"])

        await self._send_status(socket, command.command_id, "accepted")
        await self._send_status(socket, command.command_id, "executing")
        await socket.send_json(
            {
                "protocol": 1,
                "type": "COMMAND_UPDATE",
                "command_id": command.command_id,
                "status": "completed",
                "snapshot": snapshot("running", STRATEGY_ID, True),
                "action_result": "strategy_started",
            }
        )
        completed = await self.service.wait_for_terminal(
            discord_user_id=OWNER,
            command_id=command.command_id,
            timeout_seconds=1,
        )
        self.assertEqual(CommandStatus.COMPLETED, completed.status)
        self.assertEqual(
            STRATEGY_ID,
            completed.result["snapshot"]["current_strategy_id"],
        )
        device = self.store.get_device(provisioned.device.device_id)
        self.assertTrue(device.connected)
        self.assertEqual(STRATEGY_ID, device.current_strategy_id)
        await socket.close()

    async def test_list_strategies_has_typed_path_free_result(self) -> None:
        _, socket = await self._connect_agent(OWNER)
        command = await self.service.dispatch_for_user(
            discord_user_id=OWNER, operation=Operation.LIST_STRATEGIES
        )
        await socket.receive_json()
        await self._send_status(socket, command.command_id, "accepted")
        await self._send_status(socket, command.command_id, "executing")
        await socket.send_json(
            {
                "protocol": 1,
                "type": "COMMAND_UPDATE",
                "command_id": command.command_id,
                "status": "completed",
                "strategies": [
                    {"strategy_id": STRATEGY_ID, "name": "Dead Ahead Easy"}
                ],
            }
        )
        completed = await self.service.wait_for_terminal(
            discord_user_id=OWNER,
            command_id=command.command_id,
            timeout_seconds=1,
        )
        serialized = json.dumps(completed.result)
        self.assertEqual(CommandStatus.COMPLETED, completed.status)
        self.assertNotIn("C:\\", serialized)
        self.assertNotIn("Resources", serialized)
        await socket.close()

    async def test_identity_resolution_never_accepts_a_device_id(self) -> None:
        signature = inspect.signature(self.service.dispatch_for_user)
        self.assertNotIn("device_id", signature.parameters)
        await self._connect_agent(OWNER)
        with self.assertRaises(RemoteServiceError) as raised:
            await self.service.dispatch_for_user(
                discord_user_id=OTHER_OWNER, operation=Operation.GET_STATUS
            )
        self.assertEqual("DEVICE_NOT_LINKED", raised.exception.code)

    async def test_offline_and_multiple_devices_fail_without_queued_commands(self) -> None:
        self.store.provision_device(OWNER)
        with self.assertRaises(RemoteServiceError) as raised:
            await self.service.dispatch_for_user(
                discord_user_id=OWNER, operation=Operation.GET_STATUS
            )
        self.assertEqual("DEVICE_OFFLINE", raised.exception.code)

        self.store.provision_device(OWNER)
        with self.assertRaises(RemoteServiceError) as raised:
            await self.service.dispatch_for_user(
                discord_user_id=OWNER, operation=Operation.GET_STATUS
            )
        self.assertEqual("MULTIPLE_DEVICES", raised.exception.code)
        count = self.store.raw_connection_for_tests().execute(
            "SELECT COUNT(*) AS count FROM commands"
        ).fetchone()["count"]
        self.assertEqual(0, count)

    async def test_invalid_read_lifecycle_closes_agent_and_fails_read(self) -> None:
        _, socket = await self._connect_agent(OWNER)
        command = await self.service.dispatch_for_user(
            discord_user_id=OWNER, operation=Operation.GET_STATUS
        )
        await socket.receive_json()
        await socket.send_json(
            {
                "protocol": 1,
                "type": "COMMAND_UPDATE",
                "command_id": command.command_id,
                "status": "completed",
                "snapshot": snapshot(),
            }
        )
        close_message = await socket.receive(timeout=1)
        self.assertIn(close_message.type.name, {"CLOSE", "CLOSED"})
        await self._wait_for_status(command.command_id, CommandStatus.FAILED)
        self.assertEqual(
            CommandStatus.FAILED,
            self.store.get_command(command.command_id).status,
        )

    async def test_delivery_expiry_reconnects_for_reconciliation_without_replay(self) -> None:
        self.service.command_delivery_ttl_seconds = 0.05
        provisioned, socket = await self._connect_agent(OWNER)
        command = await self.service.dispatch_for_user(
            discord_user_id=OWNER,
            operation=Operation.START_STRATEGY,
            strategy_id=STRATEGY_ID,
        )
        old_session = self.service._sessions[provisioned.device.device_id]
        await socket.receive_json()
        await socket.receive(timeout=1)
        await self._wait_for_status(command.command_id, CommandStatus.RECONCILING)
        late_acceptance = parse_agent_message(
            json.dumps(
                {
                    "protocol": 1,
                    "type": "COMMAND_UPDATE",
                    "command_id": command.command_id,
                    "status": "accepted",
                }
            )
        )
        with self.assertRaisesRegex(ProtocolError, "no longer active"):
            await self.service.handle_agent_message(old_session, late_acceptance)
        self.assertEqual(
            CommandStatus.RECONCILING,
            self.store.get_command(command.command_id).status,
        )

        reconnect = await self.client.ws_connect(
            "/remote/v1/agent",
            headers={"Authorization": f"Bearer {provisioned.credential}"},
        )
        await reconnect.send_json(hello())
        welcome = await reconnect.receive_json()
        self.assertEqual(
            [{"command_id": command.command_id, "operation": "START_STRATEGY"}],
            welcome["reconcile_commands"],
        )
        await reconnect.send_json(
            {
                "protocol": 1,
                "type": "COMMAND_UPDATE",
                "command_id": command.command_id,
                "status": "failed",
                "error": {
                    "code": "NOT_PRESENT_LOCALLY",
                    "message": "No local journal or mailbox entry exists.",
                },
            }
        )
        terminal = await self.service.wait_for_terminal(
            discord_user_id=OWNER,
            command_id=command.command_id,
            timeout_seconds=1,
        )
        self.assertEqual(CommandStatus.FAILED, terminal.status)

        replacement = await self.service.dispatch_for_user(
            discord_user_id=OWNER,
            operation=Operation.START_STRATEGY,
            strategy_id=STRATEGY_ID,
        )
        self.assertEqual(CommandStatus.QUEUED, replacement.status)
        received = await reconnect.receive_json()
        self.assertEqual(replacement.command_id, received["command_id"])
        await reconnect.close()

    async def test_disconnect_after_acceptance_preserves_unknown_outcome(self) -> None:
        provisioned, socket = await self._connect_agent(OWNER)
        command = await self.service.dispatch_for_user(
            discord_user_id=OWNER, operation=Operation.STOP_SAFE
        )
        await socket.receive_json()
        await self._send_status(socket, command.command_id, "accepted")
        await self._wait_for_status(command.command_id, CommandStatus.ACCEPTED)
        await socket.close()
        await self._wait_for_status(command.command_id, CommandStatus.RECONCILING)

        reconnect = await self.client.ws_connect(
            "/remote/v1/agent",
            headers={"Authorization": f"Bearer {provisioned.credential}"},
        )
        await reconnect.send_json(hello())
        welcome = await reconnect.receive_json()
        self.assertEqual(command.command_id, welcome["reconcile_commands"][0]["command_id"])
        await reconnect.close()

    async def test_operation_specific_completion_semantics_are_enforced(self) -> None:
        cases = (
            (
                Operation.START_STRATEGY,
                STRATEGY_ID,
                snapshot(),
                "strategy_started",
            ),
            (
                Operation.STOP_SAFE,
                None,
                snapshot("running", STRATEGY_ID),
                "stopped_safe",
            ),
            (
                Operation.SWITCH_STRATEGY,
                STRATEGY_ID,
                snapshot("running", "strat_other_target_02"),
                "switched_safe",
            ),
        )
        owners = (
            "123456789012345681",
            "123456789012345682",
            "123456789012345683",
        )
        for owner, (operation, target, invalid_snapshot, action_result) in zip(
            owners, cases
        ):
            with self.subTest(operation=operation.value):
                _, socket = await self._connect_agent(owner)
                command = await self.service.dispatch_for_user(
                    discord_user_id=owner,
                    operation=operation,
                    strategy_id=target,
                )
                await socket.receive_json()
                await self._send_status(socket, command.command_id, "accepted")
                await self._wait_for_status(command.command_id, CommandStatus.ACCEPTED)
                await self._send_status(socket, command.command_id, "executing")
                await self._wait_for_status(command.command_id, CommandStatus.EXECUTING)
                await socket.send_json(
                    {
                        "protocol": 1,
                        "type": "COMMAND_UPDATE",
                        "command_id": command.command_id,
                        "status": "completed",
                        "snapshot": invalid_snapshot,
                        "action_result": action_result,
                    }
                )
                await socket.receive(timeout=1)
                await self._wait_for_status(
                    command.command_id, CommandStatus.RECONCILING
                )

    async def test_conflicting_terminal_replay_cannot_change_presence(self) -> None:
        provisioned, socket = await self._connect_agent(OWNER)
        command = await self.service.dispatch_for_user(
            discord_user_id=OWNER, operation=Operation.GET_STATUS
        )
        await socket.receive_json()
        await self._send_status(socket, command.command_id, "accepted")
        await self._send_status(socket, command.command_id, "executing")
        await socket.send_json(
            {
                "protocol": 1,
                "type": "COMMAND_UPDATE",
                "command_id": command.command_id,
                "status": "completed",
                "snapshot": snapshot(),
            }
        )
        await self.service.wait_for_terminal(
            discord_user_id=OWNER,
            command_id=command.command_id,
            timeout_seconds=1,
        )
        await socket.send_json(
            {
                "protocol": 1,
                "type": "COMMAND_UPDATE",
                "command_id": command.command_id,
                "status": "completed",
                "snapshot": snapshot("running", STRATEGY_ID),
            }
        )
        await socket.receive(timeout=1)
        device = self.store.get_device(provisioned.device.device_id)
        self.assertEqual(MacroState.IDLE, device.macro_state)

    async def test_welcome_is_sent_before_session_becomes_dispatchable(self) -> None:
        class BlockingSocket:
            def __init__(self) -> None:
                self.closed = False
                self.started = asyncio.Event()
                self.release = asyncio.Event()
                self.sent: list[str] = []

            async def send_str(self, data: str) -> None:
                self.started.set()
                await self.release.wait()
                self.sent.append(data)

            async def close(self, *, code: int = 1000, message: bytes = b"") -> None:
                self.closed = True

        provisioned = self.store.provision_device(OWNER)
        fake_socket = BlockingSocket()
        parsed_hello = parse_agent_message(json.dumps(hello()))
        session = await self.service.prepare_agent(
            provisioned.device, parsed_hello, fake_socket
        )
        welcome_send = asyncio.create_task(session.send("WELCOME"))
        await fake_socket.started.wait()
        with self.assertRaises(RemoteServiceError) as raised:
            await self.service.dispatch_for_user(
                discord_user_id=OWNER, operation=Operation.GET_STATUS
            )
        self.assertEqual("DEVICE_OFFLINE", raised.exception.code)
        fake_socket.release.set()
        await welcome_send
        await self.service.activate_agent(session)
        command = await self.service.dispatch_for_user(
            discord_user_id=OWNER, operation=Operation.GET_STATUS
        )
        self.assertEqual(CommandStatus.QUEUED, command.status)
        self.assertEqual("WELCOME", fake_socket.sent[0])
        self.assertEqual("COMMAND", json.loads(fake_socket.sent[1])["type"])
        await self.service.unregister_agent(session)

    async def test_pending_handshake_reserves_device_before_welcome(self) -> None:
        class IdleSocket:
            closed = False

            async def send_str(self, data: str) -> None:
                pass

            async def close(self, *, code: int = 1000, message: bytes = b"") -> None:
                self.closed = True

        provisioned = self.store.provision_device(OWNER)
        parsed_hello = parse_agent_message(json.dumps(hello()))
        first = await self.service.prepare_agent(
            provisioned.device, parsed_hello, IdleSocket()
        )
        with self.assertRaises(RemoteServiceError) as raised:
            await self.service.prepare_agent(
                provisioned.device, parsed_hello, IdleSocket()
            )
        self.assertEqual("DEVICE_ALREADY_CONNECTED", raised.exception.code)
        await self.service.unregister_agent(first)
        self.assertFalse(self.store.get_device(provisioned.device.device_id).connected)

    async def test_active_revocation_closes_session_and_rejects_credential(self) -> None:
        provisioned, socket = await self._connect_agent(OWNER)
        command = await self.service.dispatch_for_user(
            discord_user_id=OWNER, operation=Operation.STOP_SAFE
        )
        await socket.receive_json()
        await self._send_status(socket, command.command_id, "accepted")
        await self._wait_for_status(command.command_id, CommandStatus.ACCEPTED)
        waiter = asyncio.create_task(
            self.service.wait_for_terminal(
                discord_user_id=OWNER,
                command_id=command.command_id,
                timeout_seconds=1,
            )
        )
        await self.service.revoke_device(provisioned.device.device_id)
        revoked_command = await waiter
        await socket.receive(timeout=1)
        self.assertEqual(CommandStatus.FAILED, revoked_command.status)
        self.assertEqual(
            "DEVICE_REVOKED_OUTCOME_UNKNOWN", revoked_command.error_code
        )
        self.assertIsNone(self.store.authenticate(provisioned.credential))
        with self.assertRaises(WSServerHandshakeError):
            await self.client.ws_connect(
                "/remote/v1/agent",
                headers={"Authorization": f"Bearer {provisioned.credential}"},
            )

    async def _connect_agent(self, owner: str):
        provisioned = self.store.provision_device(owner)
        socket = await self.client.ws_connect(
            "/remote/v1/agent",
            headers={"Authorization": f"Bearer {provisioned.credential}"},
        )
        await socket.send_json(hello())
        welcome = await socket.receive_json()
        self.assertEqual("WELCOME", welcome["type"])
        self.assertEqual(1, welcome["protocol"])
        return provisioned, socket

    async def _wait_for_status(
        self, command_id: str, expected: CommandStatus
    ) -> None:
        for _ in range(50):
            if self.store.get_command(command_id).status is expected:
                return
            await asyncio.sleep(0.01)
        self.fail(f"Command {command_id} did not reach {expected.value}.")

    @staticmethod
    async def _send_status(socket, command_id: str, status: str) -> None:
        await socket.send_json(
            {
                "protocol": 1,
                "type": "COMMAND_UPDATE",
                "command_id": command_id,
                "status": status,
            }
        )
        await asyncio.sleep(0)


class RemoteConfigTests(unittest.TestCase):
    def test_plaintext_non_loopback_bind_is_rejected(self) -> None:
        with self.assertRaisesRegex(ConfigurationError, "loopback"):
            RemoteConfig(bind_host="0.0.0.0").validate()

    def test_loopback_plaintext_and_non_loopback_tls_are_allowed(self) -> None:
        RemoteConfig().validate()
        RemoteConfig(
            bind_host="0.0.0.0",
            tls_certificate=Path("certificate.pem"),
            tls_private_key=Path("private.key"),
        ).validate()

    def test_hostname_is_not_assumed_to_resolve_only_to_loopback(self) -> None:
        with self.assertRaises(ConfigurationError):
            RemoteConfig(bind_host="localhost").validate()

    def test_injected_service_and_store_must_match(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            first = RemoteStore(Path(directory) / "first.db")
            second = RemoteStore(Path(directory) / "second.db")
            service = RemoteService(first, command_delivery_ttl_seconds=10)
            try:
                with self.assertRaisesRegex(ValueError, "must match"):
                    create_app(RemoteConfig(), store=second, service=service)
            finally:
                first.close()
                second.close()


if __name__ == "__main__":
    unittest.main()
