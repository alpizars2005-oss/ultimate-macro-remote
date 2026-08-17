from __future__ import annotations

import hashlib
import ipaddress
import os
import re
import secrets
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timedelta
from typing import Callable
from urllib.parse import urlsplit

from .protocol import format_utc, utc_now, validate_discord_user_id
from .store import (
    PairingRateRule,
    ProvisionedDevice,
    RemoteStore,
    StoreConflict,
    StorePairingAlreadyLinked,
    StorePairingInvalid,
    StoreRateLimited,
)


_PAIRING_TOKEN_RE = re.compile(r"urpair_v1\.([A-Za-z0-9_-]{43})\Z")
_PAIRING_DIGEST_DOMAIN = b"ultimate-remote-pairing-ticket-v1\0"
_RATE_DIGEST_DOMAIN = b"ultimate-remote-pairing-rate-v1\0"


class PairingConfigurationError(ValueError):
    pass


class PairingError(RuntimeError):
    def __init__(
        self,
        code: str,
        user_message: str,
        *,
        http_status: int,
        retry_after_seconds: int | None = None,
    ) -> None:
        super().__init__(user_message)
        self.code = code
        self.user_message = user_message
        self.http_status = http_status
        self.retry_after_seconds = retry_after_seconds


@dataclass(frozen=True, slots=True)
class PairingOptions:
    ticket_ttl_seconds: int = 600
    issue_limit: int = 5
    issue_window_seconds: int = 3600
    redemption_source_limit: int = 10
    redemption_global_limit: int = 100
    redemption_window_seconds: int = 600
    public_https_origin: str | None = None

    @classmethod
    def from_environment(cls) -> "PairingOptions":
        origin = os.getenv("ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN", "").strip()
        options = cls(
            ticket_ttl_seconds=int(
                os.getenv("ULTIMATE_REMOTE_PAIRING_TTL_SECONDS", "600")
            ),
            issue_limit=int(os.getenv("ULTIMATE_REMOTE_PAIRING_ISSUE_LIMIT", "5")),
            issue_window_seconds=int(
                os.getenv("ULTIMATE_REMOTE_PAIRING_ISSUE_WINDOW_SECONDS", "3600")
            ),
            redemption_source_limit=int(
                os.getenv("ULTIMATE_REMOTE_PAIRING_SOURCE_LIMIT", "10")
            ),
            redemption_global_limit=int(
                os.getenv("ULTIMATE_REMOTE_PAIRING_GLOBAL_LIMIT", "100")
            ),
            redemption_window_seconds=int(
                os.getenv("ULTIMATE_REMOTE_PAIRING_WINDOW_SECONDS", "600")
            ),
            public_https_origin=origin or None,
        )
        options.validate()
        return options

    def validate(self) -> None:
        if not 60 <= self.ticket_ttl_seconds <= 1800:
            raise PairingConfigurationError(
                "Pairing ticket TTL must be between 60 and 1800 seconds."
            )
        if not 1 <= self.issue_limit <= 100:
            raise PairingConfigurationError("Pairing issue limit must be 1-100.")
        if not 60 <= self.issue_window_seconds <= 86400:
            raise PairingConfigurationError(
                "Pairing issue window must be between 60 and 86400 seconds."
            )
        if not 1 <= self.redemption_source_limit <= 1000:
            raise PairingConfigurationError(
                "Pairing source redemption limit must be 1-1000."
            )
        if not 1 <= self.redemption_global_limit <= 10000:
            raise PairingConfigurationError(
                "Pairing global redemption limit must be 1-10000."
            )
        if not 60 <= self.redemption_window_seconds <= 86400:
            raise PairingConfigurationError(
                "Pairing redemption window must be between 60 and 86400 seconds."
            )
        if self.public_https_origin is not None:
            parsed = urlsplit(self.public_https_origin)
            if (
                parsed.scheme != "https"
                or not parsed.netloc
                or parsed.username is not None
                or parsed.password is not None
                or parsed.query
                or parsed.fragment
                or parsed.path not in {"", "/"}
            ):
                raise PairingConfigurationError(
                    "Public Remote origin must be an HTTPS origin without a path, "
                    "query, credentials, or fragment."
                )


@dataclass(frozen=True, slots=True)
class IssuedPairingTicket:
    ticket: str = field(repr=False)
    expires_at: str
    redemption_url: str | None


@dataclass(frozen=True, slots=True)
class PairingRedemption:
    device_credential: str = field(repr=False)


class PairingService:
    """Isolated, temporary development enrollment service.

    Discord is the only ticket issuer. The public HTTP surface can redeem a ticket,
    but it cannot choose or override the Discord owner stored at issuance.
    """

    def __init__(
        self,
        store: RemoteStore,
        options: PairingOptions | None = None,
        *,
        clock: Callable[[], datetime] = utc_now,
    ) -> None:
        self.store = store
        self.options = options or PairingOptions()
        self.options.validate()
        self._clock = clock

    def issue_for_discord_user(
        self, discord_user_id: str | int
    ) -> IssuedPairingTicket:
        owner = validate_discord_user_id(discord_user_id)
        now = self._clock()
        expires_at = now + timedelta(seconds=self.options.ticket_ttl_seconds)
        rule = self._rate_rule(
            "issue_owner",
            owner,
            now,
            self.options.issue_window_seconds,
            self.options.issue_limit,
        )
        try:
            self.store.record_pairing_rate_attempt(
                occurred_at=format_utc(now), rate_rules=(rule,)
            )
        except StoreRateLimited as exc:
            raise PairingError(
                "PAIRING_RATE_LIMITED",
                "Too many pairing attempts. Try again later.",
                http_status=429,
                retry_after_seconds=exc.retry_after_seconds,
            ) from None
        for _ in range(3):
            ticket = f"urpair_v1.{secrets.token_urlsafe(32)}"
            try:
                self.store.issue_pairing_ticket(
                    ticket_id=str(uuid.uuid4()),
                    owner_discord_user_id=owner,
                    token_digest=_ticket_digest(ticket),
                    created_at=format_utc(now),
                    expires_at=format_utc(expires_at),
                )
            except StorePairingAlreadyLinked:
                raise PairingError(
                    "DEVICE_ALREADY_LINKED",
                    "A Remote device is already linked to this account.",
                    http_status=409,
                ) from None
            except StoreConflict:
                continue
            origin = self.options.public_https_origin
            redemption_url = (
                f"{origin.rstrip('/')}/remote/v1/pair" if origin is not None else None
            )
            return IssuedPairingTicket(
                ticket=ticket,
                expires_at=format_utc(expires_at),
                redemption_url=redemption_url,
            )
        raise PairingError(
            "PAIRING_UNAVAILABLE",
            "Pairing is temporarily unavailable.",
            http_status=503,
        )

    def redeem(self, ticket: str, *, peer_source: str) -> PairingRedemption:
        now = self._clock()
        rules = (
            self._rate_rule(
                "redeem_source",
                _canonical_peer_source(peer_source),
                now,
                self.options.redemption_window_seconds,
                self.options.redemption_source_limit,
            ),
            self._rate_rule(
                "redeem_global",
                "all",
                now,
                self.options.redemption_window_seconds,
                self.options.redemption_global_limit,
            ),
        )
        try:
            self.store.record_pairing_rate_attempt(
                occurred_at=format_utc(now), rate_rules=rules
            )
        except StoreRateLimited as exc:
            raise PairingError(
                "PAIRING_RATE_LIMITED",
                "Too many pairing attempts. Try again later.",
                http_status=429,
                retry_after_seconds=exc.retry_after_seconds,
            ) from None

        if not isinstance(ticket, str) or not _PAIRING_TOKEN_RE.fullmatch(ticket):
            raise _invalid_ticket()
        try:
            provisioned: ProvisionedDevice = self.store.redeem_pairing_ticket(
                token_digest=_ticket_digest(ticket), consumed_at=format_utc(now)
            )
        except (StorePairingInvalid, StorePairingAlreadyLinked):
            raise _invalid_ticket() from None
        except StoreConflict:
            raise PairingError(
                "PAIRING_UNAVAILABLE",
                "Pairing is temporarily unavailable.",
                http_status=503,
            ) from None
        return PairingRedemption(device_credential=provisioned.credential)

    @staticmethod
    def _rate_rule(
        scope: str,
        subject: str,
        now: datetime,
        window_seconds: int,
        limit: int,
    ) -> PairingRateRule:
        return PairingRateRule(
            scope=scope,
            subject_hash=_rate_subject_digest(scope, subject),
            window_started_at=format_utc(now - timedelta(seconds=window_seconds)),
            window_seconds=window_seconds,
            limit=limit,
        )


def _invalid_ticket() -> PairingError:
    return PairingError(
        "PAIRING_INVALID",
        "Pairing ticket is invalid or no longer usable.",
        http_status=401,
    )


def _ticket_digest(ticket: str) -> bytes:
    return hashlib.sha256(_PAIRING_DIGEST_DOMAIN + ticket.encode("ascii")).digest()


def _rate_subject_digest(scope: str, subject: str) -> bytes:
    payload = f"{scope}\0{subject}".encode("utf-8")
    return hashlib.sha256(_RATE_DIGEST_DOMAIN + payload).digest()


def _canonical_peer_source(value: str) -> str:
    try:
        return ipaddress.ip_address(value).compressed
    except ValueError:
        return "unknown"
