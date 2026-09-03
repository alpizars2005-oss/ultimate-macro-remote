from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from aiohttp.test_utils import TestClient, TestServer

from central.config import RemoteConfig
from central.linking import LinkingService
from central.server import LINKING_KEY, create_app
from central.service import RemoteService
from central.store import RemoteStore


OWNER = "123456789012345678"
SETUP = "urlink_v1." + ("D" * 43)


class RemoteLinkingHttpTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        database_path = Path(self.temp.name) / "remote.db"
        self.config = RemoteConfig(database_path=database_path)
        self.store = RemoteStore(database_path)
        self.service = RemoteService(
            self.store,
            command_delivery_ttl_seconds=self.config.command_delivery_ttl_seconds,
        )
        self.linking = LinkingService(self.store)
        self.app = create_app(
            self.config,
            store=self.store,
            service=self.service,
            linking_service=self.linking,
        )
        self.client = TestClient(TestServer(self.app))
        await self.client.start_server()

    async def asyncTearDown(self) -> None:
        await self.client.close()
        self.temp.cleanup()

    async def test_header_only_link_flow_never_accepts_identity_from_agent(self) -> None:
        self.assertIs(self.linking, self.app[LINKING_KEY])
        headers = {"Authorization": f"Linking {SETUP}"}

        response = await self.client.post("/remote/v1/link/begin", headers=headers)
        self.assertEqual(201, response.status)
        self.assertIn("no-store", response.headers.get("Cache-Control", ""))
        started = await response.json()
        self.assertEqual(1, started["protocol"])
        self.assertRegex(started["link_code"], r"\AULT-[A-Z2-9]{5}-[A-Z2-9]{5}\Z")
        self.assertNotIn(SETUP, str(started))

        pending = await self.client.post("/remote/v1/link/status", headers=headers)
        self.assertEqual(202, pending.status)
        self.assertEqual("pending", (await pending.json())["status"])

        await self.linking.claim(OWNER, started["link_code"])
        ready = await self.client.post("/remote/v1/link/status", headers=headers)
        self.assertEqual(201, ready.status)
        ready_json = await ready.json()
        self.assertEqual("ready", ready_json["status"])
        self.assertTrue(ready_json["device_credential"].startswith("urad_v1."))
        self.assertEqual("/remote/v1/agent", ready_json["agent_websocket_path"])
        self.assertEqual(1, len(self.store.list_devices_for_owner(OWNER)))

        completed = await self.client.post("/remote/v1/link/complete", headers=headers)
        self.assertEqual(204, completed.status)

    async def test_body_query_and_missing_auth_are_rejected(self) -> None:
        missing = await self.client.post("/remote/v1/link/begin")
        self.assertEqual(401, missing.status)
        self.assertEqual("Linking", missing.headers.get("WWW-Authenticate"))

        with_body = await self.client.post(
            "/remote/v1/link/begin",
            headers={"Authorization": f"Linking {SETUP}"},
            json={"discord_user_id": OWNER},
        )
        self.assertEqual(400, with_body.status)

        with_query = await self.client.post(
            "/remote/v1/link/begin?discord_user_id=" + OWNER,
            headers={"Authorization": f"Linking {SETUP}"},
        )
        self.assertEqual(400, with_query.status)
        self.assertEqual((), self.store.list_devices_for_owner(OWNER))

    async def test_invalid_setup_secret_is_non_sensitive(self) -> None:
        response = await self.client.post(
            "/remote/v1/link/begin",
            headers={"Authorization": "Linking not-a-secret"},
        )
        self.assertEqual(401, response.status)
        payload = await response.json()
        self.assertEqual("LINK_SECRET_INVALID", payload["error"]["code"])
        self.assertNotIn("not-a-secret", str(payload))
        self.assertIn("no-store", response.headers.get("Cache-Control", ""))


if __name__ == "__main__":
    unittest.main()
