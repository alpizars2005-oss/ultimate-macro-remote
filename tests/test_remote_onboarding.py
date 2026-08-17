from __future__ import annotations

import unittest
from datetime import datetime, timedelta, timezone
from urllib.parse import parse_qs, urlsplit

from central.onboarding import (
    OnboardingConfigurationError,
    OnboardingError,
    OnboardingOptions,
    OnboardingService,
)
from central.store import RemoteStore


class _FakeDiscordOnboardingService(OnboardingService):
    def __init__(self, store, options, *, owner="123456789012345678", clock):
        super().__init__(store, options, clock=clock)
        self.owner = owner
        self.exchanged_codes: list[str] = []

    async def _exchange_discord_identity(self, code: str) -> str:
        self.exchanged_codes.append(code)
        return self.owner


class RemoteOnboardingTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self) -> None:
        self.now = datetime(2026, 8, 15, 18, 0, tzinfo=timezone.utc)
        self.store = RemoteStore(":memory:")
        self.options = OnboardingOptions(
            client_id="123456789012345678",
            client_secret="x" * 32,
            public_https_origin="https://remote.example",
            session_ttl_seconds=600,
        )

    def tearDown(self) -> None:
        self.store.close()

    async def test_oauth_identity_is_authoritative_and_agent_secret_is_one_time(self) -> None:
        service = _FakeDiscordOnboardingService(
            self.store,
            self.options,
            clock=lambda: self.now,
        )
        setup_secret = "uron_v1." + ("A" * 43)

        started = await service.begin(setup_secret, peer_source="127.0.0.1")
        parsed = urlsplit(started.authorization_url)
        self.assertEqual("https", parsed.scheme)
        self.assertEqual("discord.com", parsed.hostname)
        self.assertEqual("/oauth2/authorize", parsed.path)
        query = parse_qs(parsed.query)
        self.assertEqual(["identify"], query["scope"])
        self.assertEqual([self.options.client_id], query["client_id"])
        self.assertEqual([self.options.redirect_uri], query["redirect_uri"])
        state = query["state"][0]
        self.assertTrue(state.startswith("urstate_v1."))
        self.assertNotIn(setup_secret, started.authorization_url)

        self.assertIsNone(await service.poll(setup_secret))
        await service.authorize_callback(state=state, code="discord-code")
        self.assertEqual(["discord-code"], service.exchanged_codes)

        # Browser success now means the durable device association already exists;
        # the following Agent poll only receives the one-time credential.
        devices_after_callback = self.store.list_devices_for_owner(service.owner)
        self.assertEqual(1, len(devices_after_callback))

        ready = await service.poll(setup_secret)
        self.assertIsNotNone(ready)
        assert ready is not None
        self.assertTrue(ready.device_credential.startswith("urad_v1."))
        devices = self.store.list_devices_for_owner(service.owner)
        self.assertEqual(1, len(devices))
        self.assertIsNotNone(self.store.authenticate(ready.device_credential))

        repeated = await service.poll(setup_secret)
        self.assertEqual(ready.device_credential, repeated.device_credential)
        await service.complete(setup_secret)
        await service.complete(setup_secret)
        with self.assertRaises(OnboardingError) as context:
            await service.poll(setup_secret)
        self.assertEqual("ONBOARDING_ALREADY_COMPLETED", context.exception.code)

    async def test_expired_unacknowledged_device_is_revoked(self) -> None:
        service = _FakeDiscordOnboardingService(
            self.store,
            self.options,
            clock=lambda: self.now,
        )
        setup_secret = "uron_v1." + ("B" * 43)
        started = await service.begin(setup_secret, peer_source="127.0.0.1")
        state = parse_qs(urlsplit(started.authorization_url).query)["state"][0]
        await service.authorize_callback(state=state, code="discord-code")

        devices = self.store.list_devices_for_owner(service.owner)
        self.assertEqual(1, len(devices))

        ready = await service.poll(setup_secret)
        assert ready is not None
        self.assertIsNotNone(self.store.authenticate(ready.device_credential))

        self.now += timedelta(minutes=11)
        with self.assertRaises(OnboardingError):
            await service.poll(setup_secret)
        self.assertIsNone(self.store.authenticate(ready.device_credential))

    async def test_client_cannot_supply_discord_owner(self) -> None:
        service = _FakeDiscordOnboardingService(
            self.store,
            self.options,
            owner="987654321098765432",
            clock=lambda: self.now,
        )
        setup_secret = "uron_v1." + ("C" * 43)
        started = await service.begin(setup_secret, peer_source="127.0.0.1")
        state = parse_qs(urlsplit(started.authorization_url).query)["state"][0]
        await service.authorize_callback(state=state, code="discord-code")

        self.assertEqual((), self.store.list_devices_for_owner("123456789012345678"))
        self.assertEqual(
            1,
            len(self.store.list_devices_for_owner("987654321098765432")),
        )

    def test_partial_oauth_configuration_fails_closed(self) -> None:
        with self.assertRaises(OnboardingConfigurationError):
            OnboardingOptions(
                client_id="123456789012345678",
                client_secret="",
                public_https_origin="https://remote.example",
            ).validate()

        disabled = OnboardingOptions()
        disabled.validate()
        self.assertFalse(disabled.enabled)


if __name__ == "__main__":
    unittest.main()
