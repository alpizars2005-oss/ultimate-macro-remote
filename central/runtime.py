from __future__ import annotations

import asyncio
import logging
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

from aiohttp import web
from dotenv import load_dotenv

from .config import RemoteConfig
from .discord_bot import DiscordBotOptions, RemoteDiscordClient
from .onboarding import OnboardingOptions, OnboardingService
from .pairing import PairingOptions, PairingService
from .server import build_ssl_context, create_app
from .service import RemoteService
from .store import RemoteStore


LOGGER = logging.getLogger("ultimate_remote.runtime")
PROJECT_ROOT = Path(__file__).resolve().parent.parent


@dataclass(slots=True)
class RuntimeComponents:
    store: RemoteStore
    service: RemoteService
    pairing: PairingService
    onboarding: OnboardingService | None
    app: web.Application
    discord_client: RemoteDiscordClient


def create_runtime_components(
    remote_config: RemoteConfig,
    discord_options: DiscordBotOptions,
    pairing_options: PairingOptions,
    onboarding_options: OnboardingOptions | None = None,
    *,
    client_factory: Callable[
        [RemoteService, PairingService, DiscordBotOptions], RemoteDiscordClient
    ] = RemoteDiscordClient,
) -> RuntimeComponents:
    remote_config.validate()
    discord_options.validate()
    pairing_options.validate()
    if onboarding_options is not None:
        onboarding_options.validate()
    store = RemoteStore(remote_config.database_path)
    try:
        service = RemoteService(
            store,
            command_delivery_ttl_seconds=remote_config.command_delivery_ttl_seconds,
        )
        pairing = PairingService(store, pairing_options)
        onboarding = (
            OnboardingService(store, onboarding_options)
            if onboarding_options is not None and onboarding_options.enabled
            else None
        )
        app = create_app(
            remote_config,
            store=store,
            service=service,
            pairing_service=pairing,
            onboarding_service=onboarding,
        )
        client = client_factory(service, pairing, discord_options)
    except Exception:
        store.close()
        raise
    return RuntimeComponents(store, service, pairing, onboarding, app, client)


async def run_runtime(
    remote_config: RemoteConfig,
    discord_options: DiscordBotOptions,
    pairing_options: PairingOptions,
    onboarding_options: OnboardingOptions | None = None,
) -> None:
    if not discord_options.token:
        raise RuntimeError("DISCORD_TOKEN is required on the central server.")
    components = create_runtime_components(
        remote_config,
        discord_options,
        pairing_options,
        onboarding_options,
    )
    runner = web.AppRunner(components.app, access_log=None)
    runner_ready = False
    try:
        await runner.setup()
        runner_ready = True
        site = web.TCPSite(
            runner,
            host=remote_config.bind_host,
            port=remote_config.bind_port,
            ssl_context=build_ssl_context(remote_config),
        )
        await site.start()
        LOGGER.info("Central Remote runtime started.")
        await components.discord_client.start(
            discord_options.token, reconnect=True
        )
        raise RuntimeError("Discord client stopped unexpectedly.")
    finally:
        try:
            if not components.discord_client.is_closed():
                await components.discord_client.close()
        finally:
            if runner_ready:
                await runner.cleanup()
            else:
                await components.service.close()
                components.store.close()


def _onboarding_options_from_environment() -> OnboardingOptions:
    return OnboardingOptions(
        client_id=os.getenv("DISCORD_CLIENT_ID", "").strip(),
        client_secret=os.getenv("DISCORD_CLIENT_SECRET", "").strip(),
        public_https_origin=os.getenv(
            "ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN", ""
        ).strip(),
        session_ttl_seconds=int(
            os.getenv("ULTIMATE_REMOTE_ONBOARDING_TTL_SECONDS", "600")
        ),
        source_begin_limit=int(
            os.getenv("ULTIMATE_REMOTE_ONBOARDING_SOURCE_LIMIT", "20")
        ),
        global_begin_limit=int(
            os.getenv("ULTIMATE_REMOTE_ONBOARDING_GLOBAL_LIMIT", "200")
        ),
        rate_window_seconds=int(
            os.getenv("ULTIMATE_REMOTE_ONBOARDING_WINDOW_SECONDS", "600")
        ),
    )


def main() -> None:
    load_dotenv(PROJECT_ROOT / ".env")
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s %(message)s",
    )
    remote_config = RemoteConfig.from_environment()
    discord_options = DiscordBotOptions.from_environment()
    pairing_options = PairingOptions.from_environment()
    onboarding_options = _onboarding_options_from_environment()
    onboarding_options.validate()
    try:
        asyncio.run(
            run_runtime(
                remote_config,
                discord_options,
                pairing_options,
                onboarding_options,
            )
        )
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
