from __future__ import annotations

import re
import unittest
from datetime import datetime, timedelta, timezone

from central.linking import (
    LinkingError,
    LinkingOptions,
    LinkingService,
    generate_link_code,
    normalize_link_code,
)
from central.store import RemoteStore


OWNER = "123456789012345678"
OTHER_OWNER = "987654321098765432"
SETUP = "urlink_v1." + ("A" * 43)


class RemoteLinkingTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self) -> None:
        self.now = datetime(2026, 9, 2, 23, 30, tzinfo=timezone.utc)
        self.store = RemoteStore(":memory:")
        self.service = LinkingService(
            self.store,
            LinkingOptions(session_ttl_seconds=600),
            clock=lambda: self.now,
        )

    def tearDown(self) -> None:
        self.store.close()

    def test_generated_code_uses_human_safe_50_bit_format(self) -> None:
        codes = {generate_link_code() for _ in range(64)}
        self.assertEqual(64, len(codes))
        for code in codes:
            self.assertRegex(code, r"\AULT-[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{5}-[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{5}\Z")
            self.assertNotRegex(code, r"[01IO]")

    def test_normalization_accepts_copy_typing_variants_only(self) -> None:
        self.assertEqual(
            "ULT-23456-ABCDE",
            normalize_link_code(" ult 23456 abcde "),
        )
        self.assertEqual(
            "ULT-23456-ABCDE",
            normalize_link_code("ULT-23456-ABCDE"),
        )
        self.assertIsNone(normalize_link_code("23456-ABCDE"))
        self.assertIsNone(normalize_link_code("ULT-23450-ABCDE"))

    async def test_discord_claim_is_authoritative_and_agent_secret_is_one_time(self) -> None:
        started = await self.service.begin(SETUP, peer_source="127.0.0.1")
        self.assertTrue(started.code.startswith("ULT-"))
        self.assertIsNone(await self.service.poll(SETUP))

        claim = await self.service.claim(OWNER, started.code.lower())
        self.assertEqual(started.expires_at, claim.expires_at)

        ready = await self.service.poll(SETUP)
        self.assertIsNotNone(ready)
        assert ready is not None
        self.assertTrue(ready.device_credential.startswith("urad_v1."))
        self.assertIsNotNone(self.store.authenticate(ready.device_credential))
        devices = self.store.list_devices_for_owner(OWNER)
        self.assertEqual(1, len(devices))
        self.assertEqual((), self.store.list_devices_for_owner(OTHER_OWNER))

        repeated = await self.service.claim(OWNER, started.code)
        self.assertEqual(started.expires_at, repeated.expires_at)
        with self.assertRaises(LinkingError) as other_claim:
            await self.service.claim(OTHER_OWNER, started.code)
        self.assertEqual("LINK_CODE_INVALID", other_claim.exception.code)

        await self.service.complete(SETUP)
        await self.service.complete(SETUP)
        with self.assertRaises(LinkingError) as completed:
            await self.service.poll(SETUP)
        self.assertEqual("LINK_ALREADY_COMPLETED", completed.exception.code)

    async def test_expired_unacknowledged_claim_revokes_device(self) -> None:
        started = await self.service.begin(SETUP, peer_source="127.0.0.1")
        await self.service.claim(OWNER, started.code)
        ready = await self.service.poll(SETUP)
        assert ready is not None
        self.assertIsNotNone(self.store.authenticate(ready.device_credential))

        self.now += timedelta(minutes=11)
        with self.assertRaises(LinkingError):
            await self.service.poll(SETUP)
        self.assertIsNone(self.store.authenticate(ready.device_credential))
        self.assertEqual((), self.store.list_devices_for_owner(OWNER))

    async def test_existing_device_blocks_second_link_session(self) -> None:
        self.store.provision_device(OWNER)
        started = await self.service.begin(SETUP, peer_source="127.0.0.1")
        with self.assertRaises(LinkingError) as context:
            await self.service.claim(OWNER, started.code)
        self.assertEqual("DEVICE_ALREADY_LINKED", context.exception.code)

    async def test_claim_attempts_are_rate_limited_before_lookup(self) -> None:
        limited = LinkingService(
            self.store,
            LinkingOptions(
                session_ttl_seconds=600,
                claim_owner_limit=2,
                claim_global_limit=20,
                rate_window_seconds=600,
            ),
            clock=lambda: self.now,
        )
        for _ in range(2):
            with self.assertRaises(LinkingError) as invalid:
                await limited.claim(OWNER, "ULT-23456-ABCDE")
            self.assertEqual("LINK_CODE_INVALID", invalid.exception.code)
        with self.assertRaises(LinkingError) as rate_limited:
            await limited.claim(OWNER, "ULT-23456-ABCDE")
        self.assertEqual("LINK_RATE_LIMITED", rate_limited.exception.code)
        self.assertGreaterEqual(rate_limited.exception.retry_after_seconds or 0, 1)


if __name__ == "__main__":
    unittest.main()
