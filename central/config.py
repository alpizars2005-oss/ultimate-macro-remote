from __future__ import annotations

import ipaddress
import os
from dataclasses import dataclass
from pathlib import Path


class ConfigurationError(ValueError):
    """Raised when central backend configuration is unsafe or invalid."""


def _is_loopback_host(host: str) -> bool:
    try:
        return ipaddress.ip_address(host).is_loopback
    except ValueError:
        return False


@dataclass(frozen=True, slots=True)
class RemoteConfig:
    bind_host: str = "127.0.0.1"
    bind_port: int = 8765
    database_path: Path = Path("runtime/central/remote.db")
    tls_certificate: Path | None = None
    tls_private_key: Path | None = None
    first_message_timeout_seconds: float = 10.0
    heartbeat_timeout_seconds: float = 90.0
    heartbeat_interval_seconds: int = 30
    command_delivery_ttl_seconds: int = 30

    @classmethod
    def from_environment(cls) -> "RemoteConfig":
        cert = os.getenv("ULTIMATE_REMOTE_TLS_CERT_FILE", "").strip()
        key = os.getenv("ULTIMATE_REMOTE_TLS_KEY_FILE", "").strip()
        config = cls(
            bind_host=os.getenv("ULTIMATE_REMOTE_BIND_HOST", "127.0.0.1").strip(),
            bind_port=int(os.getenv("ULTIMATE_REMOTE_BIND_PORT", "8765")),
            database_path=Path(
                os.getenv(
                    "ULTIMATE_REMOTE_DATABASE_PATH", "runtime/central/remote.db"
                )
            ).expanduser(),
            tls_certificate=Path(cert).expanduser() if cert else None,
            tls_private_key=Path(key).expanduser() if key else None,
        )
        config.validate()
        return config

    def validate(self) -> None:
        if not self.bind_host:
            raise ConfigurationError("ULTIMATE_REMOTE_BIND_HOST cannot be empty.")
        if not 1 <= self.bind_port <= 65535:
            raise ConfigurationError("ULTIMATE_REMOTE_BIND_PORT must be 1-65535.")
        if bool(self.tls_certificate) != bool(self.tls_private_key):
            raise ConfigurationError(
                "TLS certificate and private key must be configured together."
            )
        if not _is_loopback_host(self.bind_host) and not self.tls_certificate:
            raise ConfigurationError(
                "Plain HTTP/WebSocket may bind only to loopback. Configure direct TLS "
                "or bind to loopback behind a TLS reverse proxy."
            )
        if self.first_message_timeout_seconds <= 0:
            raise ConfigurationError("First-message timeout must be positive.")
        if self.heartbeat_timeout_seconds <= self.heartbeat_interval_seconds:
            raise ConfigurationError(
                "Heartbeat timeout must exceed the advertised heartbeat interval."
            )
        if self.command_delivery_ttl_seconds <= 0:
            raise ConfigurationError("Command delivery TTL must be positive.")
