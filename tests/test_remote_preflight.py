from __future__ import annotations

import contextlib
import io
import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from central import preflight


class RemotePreflightTests(unittest.TestCase):
    def _run(self, env_text: str, *, require_oauth: bool = False) -> tuple[int, str]:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / ".env").write_text(env_text, encoding="utf-8")
            output = io.StringIO()
            with mock.patch.object(preflight, "PROJECT_ROOT", root), mock.patch.dict(
                os.environ, {}, clear=True
            ), contextlib.redirect_stdout(output):
                code = preflight.run_preflight(require_oauth=require_oauth)
            return code, output.getvalue()

    def test_complete_oauth_reports_callback_without_printing_secrets(self) -> None:
        secret = "server-only-client-secret-value"
        token = "server-only-bot-token-value"
        origin = "https://remote.example"
        code, output = self._run(
            "\n".join(
                (
                    f"DISCORD_TOKEN={token}",
                    "DISCORD_CLIENT_ID=123456789012345678",
                    f"DISCORD_CLIENT_SECRET={secret}",
                    f"ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN={origin}",
                )
            ),
            require_oauth=True,
        )
        self.assertEqual(0, code)
        self.assertIn("Discord OAuth onboarding: ENABLED", output)
        self.assertIn(
            origin + "/remote/v1/onboarding/discord/callback",
            output,
        )
        self.assertNotIn(secret, output)
        self.assertNotIn(token, output)

    def test_public_origin_only_keeps_development_pairing_usable(self) -> None:
        code, output = self._run(
            "\n".join(
                (
                    "DISCORD_TOKEN=placeholder-token",
                    "ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN=https://demo.trycloudflare.com",
                )
            )
        )
        self.assertEqual(0, code)
        self.assertIn("Discord OAuth onboarding: DISABLED", output)
        self.assertIn("Development /macro pair remains available", output)
        self.assertIn("Cloudflare Quick Tunnel detected", output)

    def test_require_oauth_fails_cleanly_when_only_pairing_is_configured(self) -> None:
        code, output = self._run(
            "\n".join(
                (
                    "DISCORD_TOKEN=placeholder-token",
                    "ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN=https://remote.example",
                )
            ),
            require_oauth=True,
        )
        self.assertEqual(2, code)
        self.assertIn("requires R5 Connect Discord onboarding", output)
        self.assertNotIn("Traceback", output)


if __name__ == "__main__":
    unittest.main()
