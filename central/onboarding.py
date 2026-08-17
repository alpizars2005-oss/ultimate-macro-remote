from __future__ import annotations

import asyncio
import hashlib
import re
import secrets
from collections import deque
from dataclasses import dataclass, field
from datetime import datetime, timedelta
from typing import Callable
from urllib.parse import urlencode, urlsplit

import aiohttp

from .protocol import format_utc, utc_now, validate_discord_user_id
from .store import ProvisionedDevice, RemoteStore, StoreError


_SETUP_SECRET_RE = re.compile(r"uron_v1\.[A-Za-z0-9_-]{43}\Z")
_STATE_RE = re.compile(r"urstate_v1\.[A-Za-z0-9_-]{43}\Z")
_CLIENT_ID_RE = re.compile(r"[1-9][0-9]{0,19}\Z")
_SETUP_DIGEST_DOMAIN = b"ultimate-remote-onboarding-secret-v1\0"
_STATE_DIGEST_DOMAIN = b"ultimate-remote-onboarding-state-v1\0"
DISCORD_AUTHORIZE_URL = "https://discord.com/oauth2/authorize"
DISCORD_TOKEN_URL = "https://discord.com/api/v10/oauth2/token"
DISCORD_ME_URL = "https://discord.com/api/v10/users/@me"


class OnboardingConfigurationError(ValueError):
    pass


class OnboardingError(RuntimeError):
    def __init__(self, code: str, user_message: str, *, http_status: int) -> None:
        super().__init__(user_message)
        self.code = code
        self.user_message = user_message
        self.http_status = http_status


@dataclass(frozen=True, slots=True)
class OnboardingOptions:
    client_id: str = ""
    client_secret: str = field(default="", repr=False)
    public_https_origin: str = ""
    session_ttl_seconds: int = 600
    source_begin_limit: int = 20
    global_begin_limit: int = 200
    rate_window_seconds: int = 600

    @property
    def enabled(self) -> bool:
        return bool(self.client_id and self.client_secret and self.public_https_origin)

    def validate(self) -> None:
        values_present = (
            bool(self.client_id),
            bool(self.client_secret),
            bool(self.public_https_origin),
        )
        if any(values_present) and not all(values_present):
            raise OnboardingConfigurationError(
                "Discord OAuth onboarding requires client ID, client secret, and public HTTPS origin together."
            )
        if not self.enabled:
            return
        if not _CLIENT_ID_RE.fullmatch(self.client_id):
            raise OnboardingConfigurationError("DISCORD_CLIENT_ID must be a Discord snowflake.")
        if len(self.client_secret) < 16 or len(self.client_secret) > 256:
            raise OnboardingConfigurationError("DISCORD_CLIENT_SECRET has an invalid length.")
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
            raise OnboardingConfigurationError(
                "OAuth onboarding public origin must be an HTTPS origin without path, query, credentials, or fragment."
            )
        if not 120 <= self.session_ttl_seconds <= 1800:
            raise OnboardingConfigurationError("Onboarding session TTL must be 120-1800 seconds.")
        if not 1 <= self.source_begin_limit <= 1000:
            raise OnboardingConfigurationError("Onboarding source rate limit must be 1-1000.")
        if not 1 <= self.global_begin_limit <= 10000:
            raise OnboardingConfigurationError("Onboarding global rate limit must be 1-10000.")
        if not 60 <= self.rate_window_seconds <= 86400:
            raise OnboardingConfigurationError("Onboarding rate window must be 60-86400 seconds.")

    @property
    def redirect_uri(self) -> str:
        return (
            self.public_https_origin.rstrip("/")
            + "/remote/v1/onboarding/discord/callback"
        )


@dataclass(frozen=True, slots=True)
class OnboardingBegin:
    authorization_url: str
    expires_at: str


@dataclass(frozen=True, slots=True)
class OnboardingReady:
    device_credential: str = field(repr=False)


@dataclass(slots=True)
class _Session:
    setup_digest: bytes
    state_digest: bytes
    state: str = field(repr=False)
    created_at: datetime
    expires_at: datetime
    owner_discord_user_id: str | None = None
    device_id: str | None = None
    device_credential: str | None = field(default=None, repr=False)
    error_code: str | None = None


class OnboardingService:
    """One-time browser OAuth enrollment for the Windows Agent.

    The client chooses a 256-bit setup secret and sends it only in an Authorization
    header. Discord identity is learned only from the OAuth `identify` response. A
    device credential is kept in memory only until the Agent acknowledges that its
    DPAPI enrollment was saved. Unacknowledged provisioned devices are revoked when
    their setup session expires.
    """

    def __init__(
        self,
        store: RemoteStore,
        options: OnboardingOptions,
        *,
        clock: Callable[[], datetime] = utc_now,
    ) -> None:
        self.store = store
        self.options = options
        self.options.validate()
        self._clock = clock
        self._sessions_by_setup: dict[bytes, _Session] = {}
        self._setup_by_state: dict[bytes, bytes] = {}
        self._completed: dict[bytes, datetime] = {}
        self._source_events: dict[str, deque[datetime]] = {}
        self._global_events: deque[datetime] = deque()
        self._lock = asyncio.Lock()

    async def begin(self, setup_secret: str, *, peer_source: str) -> OnboardingBegin:
        self._require_enabled()
        if not _SETUP_SECRET_RE.fullmatch(setup_secret or ""):
            raise OnboardingError(
                "ONBOARDING_SECRET_INVALID",
                "Remote setup could not be started.",
                http_status=401,
            )
        now = self._clock()
        async with self._lock:
            self._cleanup_locked(now)
            self._rate_limit_locked(peer_source, now)
            setup_digest = _setup_digest(setup_secret)
            existing = self._sessions_by_setup.get(setup_digest)
            if existing is not None:
                return OnboardingBegin(
                    self._authorization_url(existing.state),
                    format_utc(existing.expires_at),
                )

            state = f"urstate_v1.{secrets.token_urlsafe(32)}"
            state_digest = _state_digest(state)
            session = _Session(
                setup_digest=setup_digest,
                state_digest=state_digest,
                state=state,
                created_at=now,
                expires_at=now + timedelta(seconds=self.options.session_ttl_seconds),
            )
            self._sessions_by_setup[setup_digest] = session
            self._setup_by_state[state_digest] = setup_digest
            return OnboardingBegin(
                self._authorization_url(state),
                format_utc(session.expires_at),
            )

    async def authorize_callback(self, *, state: str, code: str) -> None:
        self._require_enabled()
        if not _STATE_RE.fullmatch(state or "") or not code or len(code) > 2048:
            raise OnboardingError(
                "OAUTH_CALLBACK_INVALID",
                "Discord authorization could not be verified.",
                http_status=400,
            )

        now = self._clock()
        async with self._lock:
            self._cleanup_locked(now)
            setup_digest = self._setup_by_state.get(_state_digest(state))
            session = (
                self._sessions_by_setup.get(setup_digest)
                if setup_digest is not None
                else None
            )
            if session is None or session.expires_at <= now:
                raise OnboardingError(
                    "OAUTH_STATE_INVALID",
                    "This Remote setup session expired. Start setup again from Ultimate Macro.",
                    http_status=400,
                )
            if session.owner_discord_user_id is not None:
                return
            if session.error_code is not None:
                raise OnboardingError(
                    session.error_code,
                    "Discord authorization could not be completed.",
                    http_status=409,
                )

        try:
            owner = await self._exchange_discord_identity(code)
        except OnboardingError as exc:
            async with self._lock:
                current = self._sessions_by_setup.get(session.setup_digest)
                if current is session:
                    current.error_code = exc.code
            raise

        async with self._lock:
            current = self._sessions_by_setup.get(session.setup_digest)
            if current is not session or current.expires_at <= self._clock():
                raise OnboardingError(
                    "OAUTH_STATE_INVALID",
                    "This Remote setup session expired. Start setup again from Ultimate Macro.",
                    http_status=400,
                )
            if self.store.list_devices_for_owner(owner):
                current.error_code = "DEVICE_ALREADY_LINKED"
                raise OnboardingError(
                    "DEVICE_ALREADY_LINKED",
                    "A Remote device is already linked to this Discord account.",
                    http_status=409,
                )

            # The browser must not report "connected" before a durable device row
            # exists. Provision immediately after authoritative Discord identity is
            # verified; keep the bearer only in this in-memory setup session until
            # the Agent polls and confirms DPAPI persistence. Expiry cleanup revokes
            # any provisioned-but-unacknowledged device.
            try:
                provisioned: ProvisionedDevice = self.store.provision_device(owner)
            except StoreError as exc:
                current.error_code = "ONBOARDING_PROVISION_FAILED"
                raise OnboardingError(
                    "ONBOARDING_PROVISION_FAILED",
                    "Remote setup could not provision this device.",
                    http_status=503,
                ) from exc

            current.owner_discord_user_id = owner
            current.device_id = provisioned.device.device_id
            current.device_credential = provisioned.credential

    async def poll(self, setup_secret: str) -> OnboardingReady | None:
        self._require_enabled()
        setup_digest = self._validate_setup_secret(setup_secret)
        now = self._clock()
        async with self._lock:
            self._cleanup_locked(now)
            session = self._sessions_by_setup.get(setup_digest)
            if session is None:
                if setup_digest in self._completed:
                    raise OnboardingError(
                        "ONBOARDING_ALREADY_COMPLETED",
                        "Remote setup is already complete.",
                        http_status=409,
                    )
                raise OnboardingError(
                    "ONBOARDING_SESSION_INVALID",
                    "Remote setup session is invalid or expired.",
                    http_status=404,
                )
            if session.error_code is not None:
                raise OnboardingError(
                    session.error_code,
                    _safe_error_message(session.error_code),
                    http_status=409,
                )
            if session.owner_discord_user_id is None:
                return None
            if session.device_id is None or session.device_credential is None:
                session.error_code = "ONBOARDING_PROVISION_FAILED"
                raise OnboardingError(
                    "ONBOARDING_PROVISION_FAILED",
                    "Remote setup could not provision this device.",
                    http_status=503,
                )
            return OnboardingReady(session.device_credential)

    async def complete(self, setup_secret: str) -> None:
        self._require_enabled()
        setup_digest = self._validate_setup_secret(setup_secret)
        now = self._clock()
        async with self._lock:
            self._cleanup_locked(now)
            if setup_digest in self._completed:
                return
            session = self._sessions_by_setup.get(setup_digest)
            if session is None or session.device_id is None or session.device_credential is None:
                raise OnboardingError(
                    "ONBOARDING_NOT_READY",
                    "Remote setup is not ready to complete.",
                    http_status=409,
                )
            self._remove_session_locked(session)
            self._completed[setup_digest] = now + timedelta(minutes=10)

    async def deny_callback(self, *, state: str) -> None:
        if not _STATE_RE.fullmatch(state or ""):
            return
        async with self._lock:
            setup_digest = self._setup_by_state.get(_state_digest(state))
            session = (
                self._sessions_by_setup.get(setup_digest)
                if setup_digest is not None
                else None
            )
            if session is not None:
                session.error_code = "OAUTH_DENIED"

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
                except StoreError:
                    pass
            self._remove_session_locked(session)

        for digest, expires_at in list(self._completed.items()):
            if expires_at <= now:
                self._completed.pop(digest, None)

        cutoff = now - timedelta(seconds=self.options.rate_window_seconds)
        while self._global_events and self._global_events[0] <= cutoff:
            self._global_events.popleft()
        for source, events in list(self._source_events.items()):
            while events and events[0] <= cutoff:
                events.popleft()
            if not events:
                self._source_events.pop(source, None)

    def _remove_session_locked(self, session: _Session) -> None:
        self._sessions_by_setup.pop(session.setup_digest, None)
        self._setup_by_state.pop(session.state_digest, None)
        session.device_credential = None

    def _rate_limit_locked(self, peer_source: str, now: datetime) -> None:
        source = (peer_source or "unknown")[:128]
        cutoff = now - timedelta(seconds=self.options.rate_window_seconds)
        source_events = self._source_events.setdefault(source, deque())
        while source_events and source_events[0] <= cutoff:
            source_events.popleft()
        while self._global_events and self._global_events[0] <= cutoff:
            self._global_events.popleft()
        if (
            len(source_events) >= self.options.source_begin_limit
            or len(self._global_events) >= self.options.global_begin_limit
        ):
            raise OnboardingError(
                "ONBOARDING_RATE_LIMITED",
                "Too many Remote setup attempts. Try again later.",
                http_status=429,
            )
        source_events.append(now)
        self._global_events.append(now)

    def _authorization_url(self, state: str) -> str:
        query = urlencode(
            {
                "response_type": "code",
                "client_id": self.options.client_id,
                "scope": "identify",
                "state": state,
                "redirect_uri": self.options.redirect_uri,
            }
        )
        return f"{DISCORD_AUTHORIZE_URL}?{query}"

    async def _exchange_discord_identity(self, code: str) -> str:
        timeout = aiohttp.ClientTimeout(total=20)
        try:
            async with aiohttp.ClientSession(timeout=timeout, raise_for_status=False) as client:
                async with client.post(
                    DISCORD_TOKEN_URL,
                    data={
                        "client_id": self.options.client_id,
                        "client_secret": self.options.client_secret,
                        "grant_type": "authorization_code",
                        "code": code,
                        "redirect_uri": self.options.redirect_uri,
                    },
                    headers={"Accept": "application/json"},
                ) as response:
                    if response.status != 200:
                        raise OnboardingError(
                            "OAUTH_EXCHANGE_FAILED",
                            "Discord authorization could not be exchanged.",
                            http_status=502,
                        )
                    payload = await response.json(content_type=None)
                access_token = payload.get("access_token") if isinstance(payload, dict) else None
                scope = payload.get("scope") if isinstance(payload, dict) else None
                if (
                    not isinstance(access_token, str)
                    or not access_token
                    or len(access_token) > 4096
                    or not isinstance(scope, str)
                    or "identify" not in scope.split()
                ):
                    raise OnboardingError(
                        "OAUTH_EXCHANGE_FAILED",
                        "Discord authorization response was invalid.",
                        http_status=502,
                    )

                async with client.get(
                    DISCORD_ME_URL,
                    headers={
                        "Authorization": f"Bearer {access_token}",
                        "Accept": "application/json",
                    },
                ) as response:
                    if response.status != 200:
                        raise OnboardingError(
                            "OAUTH_IDENTITY_FAILED",
                            "Discord identity could not be verified.",
                            http_status=502,
                        )
                    user = await response.json(content_type=None)
        except OnboardingError:
            raise
        except (aiohttp.ClientError, asyncio.TimeoutError, ValueError) as exc:
            raise OnboardingError(
                "OAUTH_NETWORK_FAILED",
                "Discord authorization is temporarily unavailable.",
                http_status=502,
            ) from exc

        if not isinstance(user, dict) or user.get("bot") is True:
            raise OnboardingError(
                "OAUTH_IDENTITY_FAILED",
                "Discord identity could not be verified.",
                http_status=502,
            )
        try:
            return validate_discord_user_id(user.get("id", ""))
        except Exception as exc:
            raise OnboardingError(
                "OAUTH_IDENTITY_FAILED",
                "Discord identity could not be verified.",
                http_status=502,
            ) from exc

    def _validate_setup_secret(self, setup_secret: str) -> bytes:
        if not _SETUP_SECRET_RE.fullmatch(setup_secret or ""):
            raise OnboardingError(
                "ONBOARDING_SECRET_INVALID",
                "Remote setup session is invalid.",
                http_status=401,
            )
        return _setup_digest(setup_secret)

    def _require_enabled(self) -> None:
        if not self.options.enabled:
            raise OnboardingError(
                "ONBOARDING_DISABLED",
                "Remote browser setup is not configured on this server.",
                http_status=503,
            )


def _setup_digest(secret: str) -> bytes:
    return hashlib.sha256(_SETUP_DIGEST_DOMAIN + secret.encode("ascii")).digest()


def _state_digest(state: str) -> bytes:
    return hashlib.sha256(_STATE_DIGEST_DOMAIN + state.encode("ascii")).digest()


def _safe_error_message(code: str) -> str:
    return {
        "OAUTH_DENIED": "Discord authorization was declined.",
        "DEVICE_ALREADY_LINKED": "A Remote device is already linked to this Discord account.",
        "OAUTH_EXCHANGE_FAILED": "Discord authorization could not be completed.",
        "OAUTH_IDENTITY_FAILED": "Discord identity could not be verified.",
        "OAUTH_NETWORK_FAILED": "Discord authorization is temporarily unavailable.",
        "ONBOARDING_PROVISION_FAILED": "Remote setup could not provision this device.",
    }.get(code, "Remote setup could not be completed.")
