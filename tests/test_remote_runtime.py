from __future__ import annotations

import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from central.config import RemoteConfig
from central.discord_bot import DiscordBotOptions
from central.onboarding import OnboardingConfigurationError
from central.pairing import PairingOptions
from central.runtime import (
    create_runtime_components,
    onboarding_options_from_environment,
    run_runtime,
)
from central.server import PAIRING_KEY, SERVICE_KEY, STORE_KEY


class RemoteRuntimeTests(unittest.IsolatedAsyncioTestCase):
    async def test_runtime_components_share_exactly_one_store_and_service(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            config = RemoteConfig(database_path=Path(directory) / "remote.db")
            options = DiscordBotOptions(token="central-secret")
            components = create_runtime_components(
                config, options, PairingOptions()
            )
            try:
                self.assertIs(components.store, components.service.store)
                self.assertIs(components.store, components.pairing.store)
                self.assertIs(components.store, components.app[STORE_KEY])
                self.assertIs(components.service, components.app[SERVICE_KEY])
                self.assertIs(components.pairing, components.app[PAIRING_KEY])
                self.assertIs(
                    components.service, components.discord_client.controller.service
                )
                self.assertIs(
                    components.pairing, components.discord_client.controller.pairing
                )
            finally:
                await components.discord_client.close()
                await components.service.close()
                components.store.close()

    async def test_runtime_rejects_missing_central_bot_token_before_opening_database(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            database_path = Path(directory) / "remote.db"
            with self.assertRaisesRegex(RuntimeError, "DISCORD_TOKEN"):
                await run_runtime(
                    RemoteConfig(database_path=database_path),
                    DiscordBotOptions(token=""),
                    PairingOptions(),
                )
            self.assertFalse(database_path.exists())

    async def test_bot_launcher_contains_no_local_macro_or_identity_configuration(self) -> None:
        source = (Path(__file__).resolve().parent.parent / "bot.py").read_text(
            encoding="utf-8"
        )
        self.assertIn("central.runtime", source)
        for forbidden in (
            "ALLOWED_USER_ID",
            "MACRO_DIR",
            "MAIN_AHK",
            "remote_command.ini",
            "subprocess",
            "cancel",
        ):
            self.assertNotIn(forbidden, source)

    async def test_secret_options_have_redacted_repr(self) -> None:
        options = DiscordBotOptions(token="super-secret-bot-token")
        self.assertNotIn("super-secret-bot-token", repr(options))

    async def test_blank_optional_guild_configuration_is_accepted(self) -> None:
        with mock.patch.dict(
            os.environ,
            {"DISCORD_TOKEN": "placeholder", "DISCORD_GUILD_ID": ""},
            clear=True,
        ):
            options = DiscordBotOptions.from_environment()
        self.assertIsNone(options.guild_id)

    def test_public_origin_alone_keeps_r3_pairing_compatible_and_oauth_disabled(self) -> None:
        origin = "https://remote.example"
        with mock.patch.dict(
            os.environ,
            {"ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN": origin},
            clear=True,
        ):
            pairing = PairingOptions.from_environment()
            onboarding = onboarding_options_from_environment()
            onboarding.validate()

        self.assertEqual(origin, pairing.public_https_origin)
        self.assertFalse(onboarding.enabled)
        self.assertEqual("", onboarding.public_https_origin)

    def test_complete_r5_oauth_uses_the_same_shared_public_origin(self) -> None:
        origin = "https://remote.example"
        with mock.patch.dict(
            os.environ,
            {
                "DISCORD_CLIENT_ID": "123456789012345678",
                "DISCORD_CLIENT_SECRET": "x" * 32,
                "ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN": origin,
            },
            clear=True,
        ):
            pairing = PairingOptions.from_environment()
            onboarding = onboarding_options_from_environment()
            onboarding.validate()

        self.assertEqual(origin, pairing.public_https_origin)
        self.assertTrue(onboarding.enabled)
        self.assertEqual(
            origin + "/remote/v1/onboarding/discord/callback",
            onboarding.redirect_uri,
        )

    def test_partial_r5_oauth_credentials_still_fail_closed(self) -> None:
        with mock.patch.dict(
            os.environ,
            {
                "DISCORD_CLIENT_ID": "123456789012345678",
                "ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN": "https://remote.example",
            },
            clear=True,
        ):
            onboarding = onboarding_options_from_environment()
            with self.assertRaises(OnboardingConfigurationError):
                onboarding.validate()


if __name__ == "__main__":
    unittest.main()
