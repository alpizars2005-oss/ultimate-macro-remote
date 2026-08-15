from __future__ import annotations

import base64
import concurrent.futures
import json
import tempfile
import unittest
import uuid
from unittest import mock
from datetime import datetime, timedelta, timezone
from pathlib import Path

from aiohttp import WSServerHandshakeError
from aiohttp.test_utils import TestClient, TestServer

from central.config import RemoteConfig
from central.pairing import (
    PairingConfigurationError,
    PairingError,
    PairingOptions,
    PairingService,
)
from central.server import create_app
from central.service import RemoteService
from central.store import RemoteStore


OWNER = "123456789012345678"
OTHER_OWNER = "223456789012345678"
NOW = datetime(2026, 8, 15, 18, 0, tzinfo=timezone.utc)


class MutableClock:
    def __init__(self, value: datetime = NOW) -> None:
        self.value = value

    def __call__(self) -> datetime:
        return self.value


class PairingServiceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.database_path = Path(self.temporary_directory.name) / "remote.db"
        self.store = RemoteStore(self.database_path)
        self.clock = MutableClock()
        self.options = PairingOptions(
            ticket_ttl_seconds=600,
            issue_limit=5,
            issue_window_seconds=3600,
            redemption_source_limit=10,
            redemption_global_limit=100,
            redemption_window_seconds=600,
        )
        self.pairing = PairingService(
            self.store, self.options, clock=self.clock
        )

    def tearDown(self) -> None:
        self.store.close()
        self.temporary_directory.cleanup()

    def test_ticket_has_256_bits_and_only_its_digest_is_persisted(self) -> None:
        issued = self.pairing.issue_for_discord_user(OWNER)
        prefix, payload = issued.ticket.split(".", 1)
        self.assertEqual("urpair_v1", prefix)
        decoded = base64.urlsafe_b64decode(payload + "=")
        self.assertEqual(32, len(decoded))
        self.assertNotIn(issued.ticket, repr(issued))

        row = self.store.raw_connection_for_tests().execute(
            "SELECT token_hash FROM pairing_tickets"
        ).fetchone()
        self.assertEqual(32, len(bytes(row["token_hash"])))
        serialized_rows = json.dumps(
            [
                dict(item)
                for item in self.store.raw_connection_for_tests().execute(
                    "SELECT ticket_id, owner_discord_user_id, created_at, expires_at "
                    "FROM pairing_tickets"
                ).fetchall()
            ]
        )
        self.assertNotIn(issued.ticket, serialized_rows)
        self.store.raw_connection_for_tests().execute("PRAGMA wal_checkpoint(FULL)")
        persisted = b""
        for path in (
            self.database_path,
            Path(str(self.database_path) + "-wal"),
            Path(str(self.database_path) + "-shm"),
        ):
            if path.exists():
                persisted += path.read_bytes()
        self.assertNotIn(issued.ticket.encode("ascii"), persisted)

    def test_ticket_is_single_use_and_owner_comes_only_from_issuance(self) -> None:
        issued = self.pairing.issue_for_discord_user(OWNER)
        self.clock.value = NOW + timedelta(seconds=599)
        redemption = self.pairing.redeem(issued.ticket, peer_source="127.0.0.1")
        device = self.store.authenticate(redemption.device_credential)
        self.assertIsNotNone(device)
        self.assertEqual(OWNER, device.owner_discord_user_id)
        self.assertEqual((), self.store.list_devices_for_owner(OTHER_OWNER))

        with self.assertRaises(PairingError) as replay:
            self.pairing.redeem(issued.ticket, peer_source="127.0.0.1")
        self.assertEqual("PAIRING_INVALID", replay.exception.code)
        self.assertEqual(1, len(self.store.list_devices_for_owner(OWNER)))

    def test_ticket_expires_at_server_deadline(self) -> None:
        issued = self.pairing.issue_for_discord_user(OWNER)
        self.clock.value = NOW + timedelta(seconds=600)
        with self.assertRaises(PairingError) as raised:
            self.pairing.redeem(issued.ticket, peer_source="127.0.0.1")
        self.assertEqual("PAIRING_INVALID", raised.exception.code)
        self.assertEqual((), self.store.list_devices_for_owner(OWNER))

    def test_reissue_invalidates_previous_ticket_and_is_rate_limited(self) -> None:
        options = PairingOptions(issue_limit=2)
        pairing = PairingService(self.store, options, clock=self.clock)
        first = pairing.issue_for_discord_user(OWNER)
        second = pairing.issue_for_discord_user(OWNER)
        with self.assertRaises(PairingError) as old:
            pairing.redeem(first.ticket, peer_source="192.0.2.1")
        self.assertEqual("PAIRING_INVALID", old.exception.code)
        pairing.redeem(second.ticket, peer_source="192.0.2.1")

        other_first = pairing.issue_for_discord_user(OTHER_OWNER)
        other_second = pairing.issue_for_discord_user(OTHER_OWNER)
        self.assertNotEqual(other_first.ticket, other_second.ticket)
        with self.assertRaises(PairingError) as limited:
            pairing.issue_for_discord_user(OTHER_OWNER)
        self.assertEqual("PAIRING_RATE_LIMITED", limited.exception.code)

    def test_redemption_limits_apply_before_ticket_lookup(self) -> None:
        options = PairingOptions(
            redemption_source_limit=2,
            redemption_global_limit=10,
        )
        pairing = PairingService(self.store, options, clock=self.clock)
        for _ in range(2):
            with self.assertRaises(PairingError) as invalid:
                pairing.redeem("invalid", peer_source="2001:0db8::1")
            self.assertEqual("PAIRING_INVALID", invalid.exception.code)
        with self.assertRaises(PairingError) as limited:
            pairing.redeem("invalid", peer_source="2001:db8:0:0::1")
        self.assertEqual("PAIRING_RATE_LIMITED", limited.exception.code)
        self.assertIsNotNone(limited.exception.retry_after_seconds)

        with self.assertRaises(PairingError) as different_source:
            pairing.redeem("invalid", peer_source="2001:db8::2")
        self.assertEqual("PAIRING_INVALID", different_source.exception.code)

    def test_global_rate_limit_and_exact_window_boundary(self) -> None:
        options = PairingOptions(
            redemption_source_limit=10,
            redemption_global_limit=2,
            redemption_window_seconds=60,
        )
        pairing = PairingService(self.store, options, clock=self.clock)
        for source in ("192.0.2.1", "192.0.2.2"):
            with self.assertRaises(PairingError) as invalid:
                pairing.redeem("invalid", peer_source=source)
            self.assertEqual("PAIRING_INVALID", invalid.exception.code)
        with self.assertRaises(PairingError) as limited:
            pairing.redeem("invalid", peer_source="192.0.2.3")
        self.assertEqual("PAIRING_RATE_LIMITED", limited.exception.code)

        self.clock.value = NOW + timedelta(seconds=60)
        with self.assertRaises(PairingError) as boundary:
            pairing.redeem("invalid", peer_source="192.0.2.3")
        self.assertEqual("PAIRING_INVALID", boundary.exception.code)

    def test_concurrent_redemption_has_exactly_one_winner(self) -> None:
        issued = self.pairing.issue_for_discord_user(OWNER)

        def redeem() -> str:
            try:
                self.pairing.redeem(issued.ticket, peer_source="198.51.100.4")
            except PairingError as exc:
                return exc.code
            return "success"

        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as executor:
            results = list(executor.map(lambda _: redeem(), range(2)))
        self.assertCountEqual(["success", "PAIRING_INVALID"], results)
        self.assertEqual(1, len(self.store.list_devices_for_owner(OWNER)))

    def test_device_insert_failure_rolls_back_ticket_consumption(self) -> None:
        issued = self.pairing.issue_for_discord_user(OWNER)
        existing = self.store.provision_device(OTHER_OWNER)
        with mock.patch(
            "central.store.uuid.uuid4",
            return_value=uuid.UUID(existing.device.device_id),
        ):
            with self.assertRaises(PairingError) as failed:
                self.pairing.redeem(issued.ticket, peer_source="198.51.100.10")
        self.assertEqual("PAIRING_UNAVAILABLE", failed.exception.code)

        redemption = self.pairing.redeem(
            issued.ticket, peer_source="198.51.100.10"
        )
        self.assertIsNotNone(self.store.authenticate(redemption.device_credential))

    def test_existing_device_blocks_issuance_and_redemption(self) -> None:
        self.store.provision_device(OWNER)
        with self.assertRaises(PairingError) as issue:
            self.pairing.issue_for_discord_user(OWNER)
        self.assertEqual("DEVICE_ALREADY_LINKED", issue.exception.code)

        issued = self.pairing.issue_for_discord_user(OTHER_OWNER)
        existing = self.store.provision_device(OTHER_OWNER)
        with self.assertRaises(PairingError) as redeem:
            self.pairing.redeem(issued.ticket, peer_source="203.0.113.9")
        self.assertEqual("PAIRING_INVALID", redeem.exception.code)
        self.store.revoke_device(existing.device.device_id)
        with self.assertRaises(PairingError) as still_invalid:
            self.pairing.redeem(issued.ticket, peer_source="203.0.113.9")
        self.assertEqual("PAIRING_INVALID", still_invalid.exception.code)

    def test_linked_owner_pair_requests_are_still_rate_limited(self) -> None:
        self.store.provision_device(OWNER)
        pairing = PairingService(
            self.store, PairingOptions(issue_limit=1), clock=self.clock
        )
        with self.assertRaises(PairingError) as linked:
            pairing.issue_for_discord_user(OWNER)
        self.assertEqual("DEVICE_ALREADY_LINKED", linked.exception.code)
        with self.assertRaises(PairingError) as limited:
            pairing.issue_for_discord_user(OWNER)
        self.assertEqual("PAIRING_RATE_LIMITED", limited.exception.code)
        count = self.store.raw_connection_for_tests().execute(
            "SELECT COUNT(*) AS count FROM pairing_rate_events "
            "WHERE scope = 'issue_owner'"
        ).fetchone()["count"]
        self.assertEqual(1, count)

    def test_ticket_and_rate_state_survive_restart(self) -> None:
        issued = self.pairing.issue_for_discord_user(OWNER)
        self.store.close()
        self.store = RemoteStore(self.database_path)
        restarted = PairingService(self.store, self.options, clock=self.clock)
        redemption = restarted.redeem(issued.ticket, peer_source="127.0.0.1")
        self.assertIsNotNone(self.store.authenticate(redemption.device_credential))

    def test_pairing_configuration_requires_short_ttl_and_https_origin(self) -> None:
        for options in (
            PairingOptions(ticket_ttl_seconds=59),
            PairingOptions(ticket_ttl_seconds=1801),
            PairingOptions(public_https_origin="http://remote.example.test"),
            PairingOptions(public_https_origin="https://user@remote.example.test"),
            PairingOptions(public_https_origin="https://remote.example.test/path"),
        ):
            with self.subTest(options=options):
                with self.assertRaises(PairingConfigurationError):
                    options.validate()

        PairingOptions(
            public_https_origin="https://remote.example.test"
        ).validate()


class PairingHttpTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory()
        database_path = Path(self.temporary_directory.name) / "remote.db"
        self.config = RemoteConfig(database_path=database_path)
        self.store = RemoteStore(database_path)
        self.service = RemoteService(
            self.store,
            command_delivery_ttl_seconds=self.config.command_delivery_ttl_seconds,
        )
        self.clock = MutableClock()
        self.pairing = PairingService(
            self.store,
            PairingOptions(
                redemption_source_limit=4,
                redemption_global_limit=20,
            ),
            clock=self.clock,
        )
        self.client = TestClient(
            TestServer(
                create_app(
                    self.config,
                    store=self.store,
                    service=self.service,
                    pairing_service=self.pairing,
                )
            )
        )
        await self.client.start_server()

    async def asyncTearDown(self) -> None:
        await self.client.close()
        self.temporary_directory.cleanup()

    async def test_http_redemption_is_no_store_and_credential_uses_unchanged_wss(self) -> None:
        issued = self.pairing.issue_for_discord_user(OWNER)
        response = await self.client.post(
            "/remote/v1/pair",
            headers={"Authorization": f"Pairing {issued.ticket}"},
        )
        self.assertEqual(201, response.status)
        self.assertEqual("no-store", response.headers["Cache-Control"])
        document = await response.json()
        self.assertEqual(1, document["protocol"])
        self.assertEqual("/remote/v1/agent", document["agent_websocket_path"])
        self.assertNotIn("owner", document)
        self.assertNotIn("device_id", document)
        credential = document["device_credential"]

        socket = await self.client.ws_connect(
            "/remote/v1/agent",
            headers={"Authorization": f"Bearer {credential}"},
        )
        await socket.send_json(
            {
                "protocol": 1,
                "type": "HELLO",
                "agent_version": "r2-pairing-simulator",
                "supported_operations": ["GET_STATUS"],
                "snapshot": {
                    "macro_state": "idle",
                    "roblox_running": False,
                    "current_strategy_id": None,
                },
            }
        )
        welcome = await socket.receive_json()
        self.assertEqual("WELCOME", welcome["type"])
        await socket.close()

    async def test_unknown_expired_and_replayed_tickets_have_same_public_failure(self) -> None:
        issued = self.pairing.issue_for_discord_user(OWNER)
        success = await self.client.post(
            "/remote/v1/pair",
            headers={"Authorization": f"Pairing {issued.ticket}"},
        )
        self.assertEqual(201, success.status)
        replay = await self.client.post(
            "/remote/v1/pair",
            headers={"Authorization": f"Pairing {issued.ticket}"},
        )
        unknown = await self.client.post(
            "/remote/v1/pair",
            headers={"Authorization": "Pairing urpair_v1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"},
        )
        self.assertEqual(replay.status, unknown.status)
        self.assertEqual(await replay.json(), await unknown.json())
        self.assertEqual("no-store", replay.headers["Cache-Control"])

    async def test_body_query_and_wrong_auth_scheme_are_rejected_without_consuming(self) -> None:
        issued = self.pairing.issue_for_discord_user(OWNER)
        attempts = (
            await self.client.post(
                "/remote/v1/pair?ticket=ignored",
                headers={"Authorization": f"Pairing {issued.ticket}"},
            ),
            await self.client.post(
                "/remote/v1/pair",
                data="ignored",
                headers={"Authorization": f"Pairing {issued.ticket}"},
            ),
            await self.client.post(
                "/remote/v1/pair",
                headers={"Authorization": f"Bearer {issued.ticket}"},
            ),
        )
        for response in attempts:
            self.assertEqual(401, response.status)
            self.assertEqual("no-store", response.headers["Cache-Control"])

        valid = await self.client.post(
            "/remote/v1/pair",
            headers={"Authorization": f"Pairing {issued.ticket}"},
        )
        self.assertEqual(201, valid.status)

    async def test_oversized_body_is_rejected_immediately_and_rate_accounted(self) -> None:
        response = await self.client.post(
            "/remote/v1/pair",
            data=b"x" * (64 * 1024 + 1),
            headers={"Authorization": "Pairing invalid"},
        )
        self.assertEqual(401, response.status)
        self.assertEqual("no-store", response.headers["Cache-Control"])
        count = self.store.raw_connection_for_tests().execute(
            "SELECT COUNT(*) AS count FROM pairing_rate_events "
            "WHERE scope IN ('redeem_source', 'redeem_global')"
        ).fetchone()["count"]
        self.assertEqual(2, count)

    async def test_forwarded_address_does_not_bypass_peer_rate_limit(self) -> None:
        for index in range(4):
            response = await self.client.post(
                "/remote/v1/pair",
                headers={
                    "Authorization": "Pairing invalid",
                    "X-Forwarded-For": f"198.51.100.{index + 1}",
                },
            )
            self.assertEqual(401, response.status)
        limited = await self.client.post(
            "/remote/v1/pair",
            headers={
                "Authorization": "Pairing invalid",
                "X-Forwarded-For": "203.0.113.200",
            },
        )
        self.assertEqual(429, limited.status)
        self.assertIn("Retry-After", limited.headers)

    async def test_pairing_ticket_is_not_an_agent_bearer(self) -> None:
        issued = self.pairing.issue_for_discord_user(OWNER)
        with self.assertRaises(WSServerHandshakeError) as raised:
            await self.client.ws_connect(
                "/remote/v1/agent",
                headers={"Authorization": f"Bearer {issued.ticket}"},
            )
        self.assertEqual(401, raised.exception.status)

    async def test_injected_pairing_service_must_share_the_application_store(self) -> None:
        other_store = RemoteStore(":memory:")
        try:
            with self.assertRaisesRegex(ValueError, "PairingService"):
                create_app(
                    self.config,
                    store=self.store,
                    service=self.service,
                    pairing_service=PairingService(other_store),
                )
        finally:
            other_store.close()
