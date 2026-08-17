from __future__ import annotations

import tempfile
import unittest
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

from aiohttp.test_utils import TestClient, TestServer

from central.config import RemoteConfig
from central.onboarding import OnboardingOptions, OnboardingService
from central.server import create_app
from central.service import RemoteService
from central.store import RemoteStore


OWNER = "123456789012345678"
SETUP = "uron_v1." + ("D" * 43)


class _FakeDiscordService(OnboardingService):
    async def _exchange_discord_identity(self, code: str) -> str:
        if code != "good-code":
            raise AssertionError("Unexpected OAuth code in test.")
        return OWNER


class RemoteOnboardingHttpTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        database_path = Path(self.temp.name) / "remote.db"
        self.config = RemoteConfig(database_path=database_path)
        self.store = RemoteStore(database_path)
        self.service = RemoteService(
            self.store,
            command_delivery_ttl_seconds=self.config.command_delivery_ttl_seconds,
        )
        self.onboarding = _FakeDiscordService(
            self.store,
            OnboardingOptions(
                client_id="123456789012345678",
                client_secret="x" * 32,
                public_https_origin="https://remote.example",
            ),
        )
        self.client = TestClient(
            TestServer(
                create_app(
                    self.config,
                    store=self.store,
                    service=self.service,
                    onboarding_service=self.onboarding,
                )
            )
        )
        await self.client.start_server()

    async def asyncTearDown(self) -> None:
        await self.client.close()
        self.temp.cleanup()

    async def test_header_only_setup_flow_and_no_identity_input(self) -> None:
        headers = {"Authorization": f"Onboarding {SETUP}"}
        response = await self.client.post(
            "/remote/v1/onboarding/begin",
            headers=headers,
        )
        self.assertEqual(201, response.status)
        self.assertIn("no-store", response.headers.get("Cache-Control", ""))
        started = await response.json()
        self.assertNotIn(SETUP, str(started))
        authorize = started["authorization_url"]
        query = parse_qs(urlsplit(authorize).query)
        state = query["state"][0]

        pending = await self.client.post(
            "/remote/v1/onboarding/status",
            headers=headers,
        )
        self.assertEqual(202, pending.status)
        self.assertEqual("pending", (await pending.json())["status"])

        callback = await self.client.get(
            "/remote/v1/onboarding/discord/callback",
            params={"state": state, "code": "good-code"},
        )
        self.assertEqual(200, callback.status)
        callback_text = await callback.text()
        self.assertIn("Discord connected", callback_text)
        self.assertNotIn(state, callback_text)
        self.assertNotIn("good-code", callback_text)
        self.assertNotIn(SETUP, callback_text)

        ready = await self.client.post(
            "/remote/v1/onboarding/status",
            headers=headers,
        )
        self.assertEqual(201, ready.status)
        ready_json = await ready.json()
        self.assertEqual("ready", ready_json["status"])
        self.assertTrue(ready_json["device_credential"].startswith("urad_v1."))
        self.assertEqual("/remote/v1/agent", ready_json["agent_websocket_path"])
        self.assertEqual(1, len(self.store.list_devices_for_owner(OWNER)))

        completed = await self.client.post(
            "/remote/v1/onboarding/complete",
            headers=headers,
        )
        self.assertEqual(204, completed.status)

    async def test_body_query_and_missing_auth_are_rejected(self) -> None:
        missing = await self.client.post("/remote/v1/onboarding/begin")
        self.assertEqual(401, missing.status)
        self.assertEqual("Onboarding", missing.headers.get("WWW-Authenticate"))

        with_body = await self.client.post(
            "/remote/v1/onboarding/begin",
            headers={"Authorization": f"Onboarding {SETUP}"},
            json={"discord_user_id": OWNER},
        )
        self.assertEqual(400, with_body.status)

        with_query = await self.client.post(
            "/remote/v1/onboarding/begin?discord_user_id=" + OWNER,
            headers={"Authorization": f"Onboarding {SETUP}"},
        )
        self.assertEqual(400, with_query.status)
        self.assertEqual((), self.store.list_devices_for_owner(OWNER))

    async def test_denied_oauth_never_provisions_device(self) -> None:
        headers = {"Authorization": f"Onboarding {SETUP}"}
        started = await (
            await self.client.post("/remote/v1/onboarding/begin", headers=headers)
        ).json()
        state = parse_qs(urlsplit(started["authorization_url"]).query)["state"][0]
        denied = await self.client.get(
            "/remote/v1/onboarding/discord/callback",
            params={"state": state, "error": "access_denied"},
        )
        self.assertEqual(200, denied.status)
        status = await self.client.post(
            "/remote/v1/onboarding/status",
            headers=headers,
        )
        self.assertEqual(409, status.status)
        self.assertEqual("OAUTH_DENIED", (await status.json())["error"]["code"])
        self.assertEqual((), self.store.list_devices_for_owner(OWNER))


if __name__ == "__main__":
    unittest.main()
