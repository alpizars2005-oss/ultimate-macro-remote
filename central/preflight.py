from __future__ import annotations

import os
from pathlib import Path
from urllib.parse import urlsplit

from dotenv import load_dotenv

from .runtime import PROJECT_ROOT, load_runtime_options


LEGACY_KEYS = (
    "ALLOWED_USER_ID",
    "GUILD_ID",
    "MACRO_DIR",
    "MAIN_AHK",
)


def run_preflight(*, require_oauth: bool = False) -> int:
    env_path = PROJECT_ROOT / ".env"
    if not env_path.is_file():
        print(f"[Remote] ERROR: missing {env_path}")
        print("[Remote] Copy .env.example to .env on the central server and fill only server-side values.")
        return 2

    load_dotenv(env_path, override=False)
    try:
        remote_config, discord_options, pairing_options, onboarding_options = (
            load_runtime_options()
        )
    except (ValueError, RuntimeError) as exc:
        print(f"[Remote] ERROR: {exc}")
        print("[Remote] No secret values were printed. Fix .env and run again.")
        return 2

    if not discord_options.token:
        print("[Remote] ERROR: DISCORD_TOKEN is missing on the central server.")
        return 2

    print("[Remote] Central configuration is internally compatible.")
    print(f"[Remote] Listener: {remote_config.bind_host}:{remote_config.bind_port}")

    origin = pairing_options.public_https_origin
    if origin:
        print(f"[Remote] Public origin: {origin}")
    else:
        print("[Remote] Public origin: not configured (same-host/loopback development only).")

    if onboarding_options.enabled:
        print("[Remote] Discord OAuth onboarding: ENABLED")
        print(f"[Remote] Discord Redirect URI: {onboarding_options.redirect_uri}")
    else:
        print("[Remote] Discord OAuth onboarding: DISABLED")
        print("[Remote] Development /macro pair remains available when a public origin is configured.")
        if require_oauth:
            print("[Remote] ERROR: this check requires R5 Connect Discord onboarding.")
            print("[Remote] Set DISCORD_CLIENT_ID and DISCORD_CLIENT_SECRET in .env.")
            return 2

    if origin:
        host = (urlsplit(origin).hostname or "").lower()
        if host.endswith(".trycloudflare.com"):
            print("[Remote] WARNING: Cloudflare Quick Tunnel detected.")
            print("[Remote] If this URL changes, update .env, the Discord OAuth Redirect URI, and rebuild the client ZIP from the same origin.")

    present_legacy = [key for key in LEGACY_KEYS if os.getenv(key, "").strip()]
    if present_legacy:
        print(
            "[Remote] WARNING: legacy variables are present and ignored/deprecated: "
            + ", ".join(present_legacy)
        )

    return 0


def main() -> None:
    import argparse

    parser = argparse.ArgumentParser(description="Validate Ultimate Macro Remote central configuration without printing secrets.")
    parser.add_argument(
        "--require-oauth",
        action="store_true",
        help="fail unless R5 Discord OAuth onboarding is fully configured",
    )
    args = parser.parse_args()
    raise SystemExit(run_preflight(require_oauth=args.require_oauth))


if __name__ == "__main__":
    main()
