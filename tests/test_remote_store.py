from __future__ import annotations

import tempfile
import unittest
from datetime import timedelta
from pathlib import Path

from central.protocol import (
    CommandStatus,
    MacroSnapshot,
    MacroState,
    Operation,
    format_utc,
    utc_now,
)
from central.store import (
    InvalidTransition,
    RemoteStore,
    StoreAuthorizationError,
    StoreConflict,
)


OWNER = "123456789012345678"
OTHER_OWNER = "223456789012345678"
STRATEGY_ID = "strat_dead_ahead_01"


class RemoteStoreTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.database_path = Path(self.temporary_directory.name) / "remote.db"
        self.store = RemoteStore(self.database_path)

    def tearDown(self) -> None:
        self.store.close()
        self.temporary_directory.cleanup()

    def test_credentials_are_random_hashed_revocable_and_redacted(self) -> None:
        first = self.store.provision_device(OWNER)
        second = self.store.provision_device(OWNER)
        self.assertNotEqual(first.credential, second.credential)
        self.assertNotIn(first.credential, repr(first))
        self.assertEqual(first.device.device_id, self.store.authenticate(first.credential).device_id)
        self.assertIsNone(self.store.authenticate(first.credential + "x"))

        row = self.store.raw_connection_for_tests().execute(
            "SELECT credential_hash FROM devices WHERE device_id = ?",
            (first.device.device_id,),
        ).fetchone()
        self.assertIsInstance(row["credential_hash"], bytes)
        self.assertNotIn(first.credential.encode(), self.database_path.read_bytes())

        self.store.revoke_device(first.device.device_id)
        self.assertIsNone(self.store.authenticate(first.credential))

    def test_database_rejects_a_second_backend_writer(self) -> None:
        with self.assertRaisesRegex(StoreConflict, "another backend"):
            RemoteStore(self.database_path)

    def test_owner_association_persists_and_excludes_revoked_devices(self) -> None:
        device = self.store.provision_device(OWNER)
        self.assertEqual(
            (device.device.device_id,),
            tuple(item.device_id for item in self.store.list_devices_for_owner(OWNER)),
        )
        self.assertEqual((), self.store.list_devices_for_owner(OTHER_OWNER))
        self.store.revoke_device(device.device.device_id)
        self.assertEqual((), self.store.list_devices_for_owner(OWNER))

    def test_presence_is_persistent_but_restart_marks_offline(self) -> None:
        provisioned = self.store.provision_device(OWNER)
        self.store.update_presence(
            device_id=provisioned.device.device_id,
            connected=True,
            agent_version="0.1.0",
            supported_operations=tuple(Operation),
            snapshot=MacroSnapshot(MacroState.IDLE, False, None),
        )
        self.assertTrue(self.store.get_device(provisioned.device.device_id).connected)
        self.store.close()
        self.store = RemoteStore(self.database_path)
        restored = self.store.get_device(provisioned.device.device_id)
        self.assertFalse(restored.connected)
        self.assertEqual("0.1.0", restored.agent_version)
        self.assertEqual(tuple(Operation), restored.supported_operations)

    def test_command_lifecycle_is_monotonic_and_correlated_to_device(self) -> None:
        provisioned = self.store.provision_device(OWNER)
        command = self._create_command(provisioned.device.device_id)
        accepted = self.store.transition_command(
            command_id=command.command_id,
            device_id=provisioned.device.device_id,
            status=CommandStatus.ACCEPTED,
        )
        self.assertEqual(CommandStatus.ACCEPTED, accepted.status)
        executing = self.store.transition_command(
            command_id=command.command_id,
            device_id=provisioned.device.device_id,
            status=CommandStatus.EXECUTING,
        )
        self.assertEqual(CommandStatus.EXECUTING, executing.status)
        completed = self.store.transition_command(
            command_id=command.command_id,
            device_id=provisioned.device.device_id,
            status=CommandStatus.COMPLETED,
            result={"snapshot": {"macro_state": "running"}},
        )
        self.assertEqual(CommandStatus.COMPLETED, completed.status)
        with self.assertRaises(InvalidTransition):
            self.store.transition_command(
                command_id=command.command_id,
                device_id=provisioned.device.device_id,
                status=CommandStatus.FAILED,
                error_code="TOO_LATE",
                error_message="Terminal state is immutable.",
            )

    def test_wrong_device_and_owner_cannot_claim_command(self) -> None:
        first = self.store.provision_device(OWNER)
        second = self.store.provision_device(OTHER_OWNER)
        command = self._create_command(first.device.device_id)
        with self.assertRaises(StoreAuthorizationError):
            self.store.transition_command(
                command_id=command.command_id,
                device_id=second.device.device_id,
                status=CommandStatus.ACCEPTED,
            )
        with self.assertRaises(StoreAuthorizationError):
            self.store.get_command_for_owner(command.command_id, OTHER_OWNER)

    def test_only_one_mutating_command_can_be_active(self) -> None:
        provisioned = self.store.provision_device(OWNER)
        self._create_command(provisioned.device.device_id)
        with self.assertRaisesRegex(StoreConflict, "already active"):
            self.store.create_command(
                device_id=provisioned.device.device_id,
                owner_discord_user_id=OWNER,
                operation=Operation.STOP_SAFE,
                arguments={},
                expires_at=format_utc(utc_now() + timedelta(seconds=30)),
            )

    def test_server_restart_requires_reconciliation_without_replay(self) -> None:
        provisioned = self.store.provision_device(OWNER)
        command = self._create_command(provisioned.device.device_id)
        self.store.close()
        self.store = RemoteStore(self.database_path)
        restored = self.store.get_command(command.command_id)
        self.assertEqual(CommandStatus.RECONCILING, restored.status)
        self.assertEqual("SERVER_RESTART_OUTCOME_UNKNOWN", restored.error_code)
        self.store.update_presence(
            device_id=provisioned.device.device_id,
            connected=True,
            snapshot=MacroSnapshot(MacroState.IDLE, False, None),
        )
        with self.assertRaises(StoreConflict):
            self.store.create_command(
                device_id=provisioned.device.device_id,
                owner_discord_user_id=OWNER,
                operation=Operation.STOP_SAFE,
                arguments={},
                expires_at=format_utc(utc_now() + timedelta(seconds=30)),
            )

    def test_conflicting_terminal_replay_is_rejected(self) -> None:
        provisioned = self.store.provision_device(OWNER)
        command = self._create_command(provisioned.device.device_id)
        for status in (CommandStatus.ACCEPTED, CommandStatus.EXECUTING):
            command = self.store.transition_command(
                command_id=command.command_id,
                device_id=provisioned.device.device_id,
                status=status,
            )
        result = {"snapshot": {"macro_state": "running"}}
        self.store.transition_command(
            command_id=command.command_id,
            device_id=provisioned.device.device_id,
            status=CommandStatus.COMPLETED,
            result=result,
        )
        self.store.transition_command(
            command_id=command.command_id,
            device_id=provisioned.device.device_id,
            status=CommandStatus.COMPLETED,
            result=result,
        )
        with self.assertRaisesRegex(InvalidTransition, "Conflicting replay"):
            self.store.transition_command(
                command_id=command.command_id,
                device_id=provisioned.device.device_id,
                status=CommandStatus.COMPLETED,
                result={"snapshot": {"macro_state": "not_running"}},
            )

    def test_revoked_device_cannot_win_command_creation_race(self) -> None:
        provisioned = self.store.provision_device(OWNER)
        self.store.revoke_device(provisioned.device.device_id)
        with self.assertRaises(StoreAuthorizationError):
            self.store.create_command(
                device_id=provisioned.device.device_id,
                owner_discord_user_id=OWNER,
                operation=Operation.START_STRATEGY,
                arguments={"strategy_id": STRATEGY_ID},
                expires_at=format_utc(utc_now() + timedelta(seconds=30)),
            )

    def test_revocation_terminally_fails_every_unknown_command_state(self) -> None:
        for index, lifecycle in enumerate(
            (
                (),
                (CommandStatus.ACCEPTED,),
                (CommandStatus.ACCEPTED, CommandStatus.EXECUTING),
                (
                    CommandStatus.ACCEPTED,
                    CommandStatus.EXECUTING,
                    CommandStatus.RECONCILING,
                ),
            )
        ):
            with self.subTest(lifecycle=lifecycle):
                owner = str(int(OWNER) + index + 1)
                provisioned = self.store.provision_device(owner)
                self.store.update_presence(
                    device_id=provisioned.device.device_id,
                    connected=True,
                    snapshot=MacroSnapshot(MacroState.IDLE, False, None),
                )
                command = self.store.create_command(
                    device_id=provisioned.device.device_id,
                    owner_discord_user_id=owner,
                    operation=Operation.START_STRATEGY,
                    arguments={"strategy_id": STRATEGY_ID},
                    expires_at=format_utc(utc_now() + timedelta(seconds=30)),
                )
                for status in lifecycle:
                    if status is CommandStatus.RECONCILING:
                        command = self.store.transition_command(
                            command_id=command.command_id,
                            device_id=provisioned.device.device_id,
                            status=status,
                            error_code="CONNECTION_LOST_OUTCOME_UNKNOWN",
                            error_message="Awaiting reconciliation.",
                        )
                    else:
                        command = self.store.transition_command(
                            command_id=command.command_id,
                            device_id=provisioned.device.device_id,
                            status=status,
                        )
                affected = self.store.revoke_device(provisioned.device.device_id)
                self.assertEqual((command.command_id,), tuple(c.command_id for c in affected))
                revoked = self.store.get_command(command.command_id)
                self.assertEqual(CommandStatus.FAILED, revoked.status)
                self.assertEqual(
                    "DEVICE_REVOKED_OUTCOME_UNKNOWN", revoked.error_code
                )
                self.assertEqual(
                    (),
                    self.store.list_reconciling_commands(
                        provisioned.device.device_id
                    ),
                )

    def _create_command(self, device_id: str):
        self.store.update_presence(
            device_id=device_id,
            connected=True,
            snapshot=MacroSnapshot(MacroState.IDLE, False, None),
        )
        return self.store.create_command(
            device_id=device_id,
            owner_discord_user_id=OWNER,
            operation=Operation.START_STRATEGY,
            arguments={"strategy_id": STRATEGY_ID},
            expires_at=format_utc(utc_now() + timedelta(seconds=30)),
        )


if __name__ == "__main__":
    unittest.main()
