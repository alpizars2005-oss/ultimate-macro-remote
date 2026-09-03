from __future__ import annotations

import asyncio
import hashlib
import ipaddress
import re
import secrets
from dataclasses import dataclass, field
from datetime import datetime, timedelta
from typing import Callable

from .protocol import format_utc, utc_now, validate_discord_user_id
from .store import (
    PairingRateRule,
    RemoteStore,
    StoreConflict,
    StoreError,
    StoreNotFound,
    StoreRateLimited,
)


LINK_CODE_ALPHABET = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ"
_LINK_CODE_RE = re.compile(r"ULT-([23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{5})-([23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{5})\Z")
_SETUP_SECRET_RE = re.compile(r"urlink_v1\.([A-Za-z0-9_-]{43})\Z")
_LINK_CODE_DIGEST_DOMAIN = b"ultimate-remote-link-code-v1\0"
_SETUP_DIGEST_DOMAIN = b"ultimate-remote-link-setup-v1\0"
_RATE_DIGEST_DOMAIN = b"ultimate-remote-link-rate-v1\0"


class LinkingConfigurationError(ValueError):
    pass


class LinkingError(RuntimeError):
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
class LinkingOptions:
    session_ttl_seconds: int = 600
    begin_source_limit: int = 20
    claim_owner_limit: int = 12
    claim_global_limit: int = 500
    rate_window_seconds: int = 600

    def validate(self) -> None:
        if not 120 <= self.session_ttl_seconds <= 1800:
            raise LinkingConfigurationError("Link session TTL must be 120-1800 seconds.")
        if not 1 <= self.begin_source_limit <= 1000:
            raise LinkingConfigurationError("Link begin source limit must be 1-1000.")
        if not 1 <= self.claim_owner_limit <= 1000:
            raise LinkingConfigurationError("Link claim owner limit must be 1-1000.")
        if not 1 <= self.claim_global_limit <= 10000:
            raise LinkingConfigurationError("Link claim global limit must be 1-10000.")
        if not 60 <= self.rate_window_seconds <= 86400:
            raise LinkingConfigurationError("Link rate window must be 60-86400 seconds.")


@dataclass(frozen=True, slots=True)
class LinkStart:
    code: str
    expires_at: str


@dataclass(frozen=True, slots=True)
class LinkReady:
    device_credential: str = field(repr=False)


@dataclass(frozen=True, slots=True)
class LinkClaim:
    expires_at: str


@dataclass(slots=True)
class _Session:
    setup_digest: bytes
    code_digest: bytes
    code: str = field(repr=False)
    created_at: datetime
    expires_at: datetime
    owner_discord_user_id: str | None = None
    device_id: str | None = None
    device_credential: str | None = field(default=None, repr=False)


class LinkingService:
    """Macro-first, one-time Discord linking-code service.

    The Agent owns a high-entropy setup secret and receives only a short display code.
    Discord identity is supplied exclusively by the bot interaction when `claim` is
    called. No client request can choose its Discord owner.
    """

    def __init__(
        self,
        store: RemoteStore,
        options: LinkingOptions | None = None,
        *,
        clock: Callable[[], datetime] = utc_now,
    ) -> None:
        self.store = store
        self.options = options or LinkingOptions()
        self.options.validate()
        self._clock = clock
        self._sessions_by_setup: dict[bytes, _Session] = {}
        self._setup_by_code: dict[bytes, bytes] = {}
        self._completed: dict[bytes, datetime] = {}
        self._lock = asyncio.Lock()

    async def begin(self, setup_secret: str, *, peer_source: str) -> LinkStart:
        setup_digest = self._validate_setup_secret(setup_secret)
        now = self._clock()
        self._record_rate_attempt(
            now,
            (
                self._rate_rule(
                    "link_begin_source",
                    _canonical_peer_source(peer_source),
                    now,
                    self.options.begin_source_limit,
                ),
            ),
        )

        async with self._lock:
            self._cleanup_locked(now)
            existing = self._sessions_by_setup.get(setup_digest)
            if existing is not None:
                return LinkStart(existing.code, format_utc(existing.expires_at))

            for _ in range(8):
                code = generate_link_code()
                code_digest = _link_code_digest(code)
                if code_digest in self._setup_by_code:
                    continue
                session = _Session(
                    setup_digest=setup_digest,
                    code_digest=code_digest,
                    code=code,
                    created_at=now,
                    expires_at=now + timedelta(seconds=self.options.session_ttl_seconds),
                )
                self._sessions_by_setup[setup_digest] = session
                self._setup_by_code[code_digest] = setup_digest
                return LinkStart(code, format_utc(session.expires_at))

        raise LinkingError(
            "LINK_UNAVAILABLE",
            "Remote linking is temporarily unavailable.",
            http_status=503,
        )

    async def claim(self, discord_user_id: str | int, code: str) -> LinkClaim:
        owner = validate_discord_user_id(discord_user_id)
        canonical = normalize_link_code(code)
        now = self._clock()
        self._record_rate_attempt(
            now,
            (
                self._rate_rule(
                    "link_claim_owner",
                    owner,
                    now,
                    self.options.claim_owner_limit,
                ),
                self._rate_rule(
                    "link_claim_global",
                    "all",
                    now,
                    self.options.claim_global_limit,
                ),
            ),
        )

        async with self._lock:
            self._cleanup_locked(now)
            if canonical is None:
                raise _invalid_code()
            setup_digest = self._setup_by_code.get(_link_code_digest(canonical))
            session = (
                self._sessions_by_setup.get(setup_digest)
                if setup_digest is not None
                else None
            )
            if session is None or session.expires_at <= now:
                raise _invalid_code()

            if session.owner_discord_user_id is not None:
                if session.owner_discord_user_id != owner:
                    raise _invalid_code()
                return LinkClaim(format_utc(session.expires_at))

            if self.store.list_devices_for_owner(owner):
                raise LinkingError(
                    "DEVICE_ALREADY_LINKED",
                    "A Remote device is already linked to this Discord account.",
                    http_status=409,
                )

            try:
                provisioned = self.store.provision_device(owner)
            except StoreConflict as exc:
                raise LinkingError(
                    "LINK_UNAVAILABLE",
                    "Remote linking is temporarily unavailable.",
                    http_status=503,
                ) from exc

            session.owner_discord_user_id = owner
            session.device_id = provisioned.device.device_id
            session.device_credential = provisioned.credential
            return LinkClaim(format_utc(session.expires_at))

    async def poll(self, setup_secret: str) -> LinkReady | None:
        setup_digest = self._validate_setup_secret(setup_secret)
        now = self._clock()
        async with self._lock:
            self._cleanup_locked(now)
            session = self._sessions_by_setup.get(setup_digest)
            if session is None:
                if setup_digest in self._completed:
                    raise LinkingError(
                        "LINK_ALREADY_COMPLETED",
                        "Remote linking is already complete.",
                        http_status=409,
                    )
                raise LinkingError(
                    "LINK_SESSION_INVALID",
                    "Remote linking session is invalid or expired.",
                    http_status=404,
                )
            if session.owner_discord_user_id is None:
                return None
            if session.device_id is None or session.device_credential is None:
                raise LinkingError(
                    "LINK_PROVISION_FAILED",
                    "Remote linking could not provision this device.",
                    http_status=503,
                )
            return LinkReady(session.device_credential)

    async def complete(self, setup_secret: str) -> None:
        setup_digest = self._validate_setup_secret(setup_secret)
        now = self._clock()
        async with self._lock:
            self._cleanup_locked(now)
            if setup_digest in self._completed:
                return
            session = self._sessions_by_setup.get(setup_digest)
            if (
                session is None
                or session.owner_discord_user_id is None
                or session.device_id is None
                or session.device_credential is None
            ):
                raise LinkingError(
                    "LINK_NOT_READY",
                    "Remote linking is not ready to complete.",
                    http_status=409,
                )
            self._remove_session_locked(session)
            self._completed[setup_digest] = now + timedelta(minutes=10)

    def _record_rate_attempt(
        self,
        now: datetime,
        rules: tuple[PairingRateRule, ...],
    ) -> None:
        try:
            self.store.record_pairing_rate_attempt(
                occurred_at=format_utc(now),
                rate_rules=rules,
            )
        except StoreRateLimited as exc:
            raise LinkingError(
                "LINK_RATE_LIMITED",
                "Too many Remote linking attempts. Try again later.",
                http_status=429,
                retry_after_seconds=exc.retry_after_seconds,
            ) from None

    def _rate_rule(
        self,
        scope: str,
        subject: str,
        now: datetime,
        limit: int,
    ) -> PairingRateRule:
        return PairingRateRule(
            scope=scope,
            subject_hash=_rate_subject_digest(scope, subject),
            window_started_at=format_utc(
                now - timedelta(seconds=self.options.rate_window_seconds)
            ),
            window_seconds=self.options.rate_window_seconds,
            limit=limit,
        )

    def _cleanup_locked(self, now: datetime) -> None:
        expired = [
            session
            for session in self._sessions_by_setup.values()
            if session.expires_at <= now
        ]
        for session in expired:
            if session.device_id is not None:
                try:
                    self.store.revoke_device(session.device_id)
                except (StoreError, StoreNotFound):
                    pass
            self._remove_session_locked(session)

        for digest, expires_at in list(self._completed.items()):
            if expires_at <= now:
                self._completed.pop(digest, None)

    def _remove_session_locked(self, session: _Session) -> None:
        self._sessions_by_setup.pop(session.setup_digest, None)
        self._setup_by_code.pop(session.code_digest, None)
        session.device_credential = None
        session.code = ""

    @staticmethod
    def _validate_setup_secret(setup_secret: str) -> bytes:
        if not isinstance(setup_secret, str) or not _SETUP_SECRET_RE.fullmatch(setup_secret):
            raise LinkingError(
                "LINK_SECRET_INVALID",
                "Remote linking authentication is invalid.",
                http_status=401,
            )
        return _setup_digest(setup_secret)


def generate_link_code() -> str:
    payload = "".join(secrets.choice(LINK_CODE_ALPHABET) for _ in range(10))
    return f"ULT-{payload[:5]}-{payload[5:]}"


def normalize_link_code(value: str) -> str | None:
    if not isinstance(value, str):
        return None
    compact = re.sub(r"[\s-]+", "", value.strip().upper())
    if not compact.startswith("ULT"):
        return None
    payload = compact[3:]
    if len(payload) != 10 or any(character not in LINK_CODE_ALPHABET for character in payload):
        return None
    canonical = f"ULT-{payload[:5]}-{payload[5:]}"
    return canonical if _LINK_CODE_RE.fullmatch(canonical) else None


def _invalid_code() -> LinkingError:
    return LinkingError(
        "LINK_CODE_INVALID",
        "Linking code is invalid or no longer usable.",
        http_status=401,
    )


def _link_code_digest(code: str) -> bytes:
    return hashlib.sha256(_LINK_CODE_DIGEST_DOMAIN + code.encode("ascii")).digest()


def _setup_digest(setup_secret: str) -> bytes:
    return hashlib.sha256(_SETUP_DIGEST_DOMAIN + setup_secret.encode("ascii")).digest()


def _rate_subject_digest(scope: str, subject: str) -> bytes:
    return hashlib.sha256(
        _RATE_DIGEST_DOMAIN + f"{scope}\0{subject}".encode("utf-8")
    ).digest()


def _canonical_peer_source(value: str) -> str:
    try:
        return ipaddress.ip_address(value).compressed
    except ValueError:
        return "unknown"
