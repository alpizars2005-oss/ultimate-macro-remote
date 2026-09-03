from __future__ import annotations

import asyncio
import html
import logging
import ssl

from aiohttp import WSMsgType, web

from .config import RemoteConfig
from .linking import LinkingError, LinkingService
from .onboarding import OnboardingError, OnboardingService
from .pairing import PairingError, PairingService
from .protocol import (
    CommandUpdateMessage,
    HeartbeatMessage,
    HelloMessage,
    MAX_MESSAGE_BYTES,
    PROTOCOL_VERSION,
    ProtocolError,
    encode_welcome,
    parse_agent_message,
    utc_now,
)
from .service import AgentSession, RemoteService, RemoteServiceError
from .store import RemoteStore
from .store import StoreError


LOGGER = logging.getLogger("ultimate_remote.central")
_PAIRING_HTTP_MESSAGES = {
    "PAIRING_INVALID": "Pairing ticket is invalid or no longer usable.",
    "PAIRING_RATE_LIMITED": "Too many pairing attempts. Try again later.",
    "PAIRING_UNAVAILABLE": "Pairing is temporarily unavailable.",
}
CONFIG_KEY = web.AppKey("remote_config", RemoteConfig)
SERVICE_KEY = web.AppKey("remote_service", RemoteService)
STORE_KEY = web.AppKey("remote_store", RemoteStore)
PAIRING_KEY = web.AppKey("pairing_service", PairingService)
LINKING_KEY = web.AppKey("linking_service", LinkingService)
ONBOARDING_KEY = web.AppKey("onboarding_service", OnboardingService)


def create_app(
    config: RemoteConfig,
    *,
    store: RemoteStore | None = None,
    service: RemoteService | None = None,
    pairing_service: PairingService | None = None,
    linking_service: LinkingService | None = None,
    onboarding_service: OnboardingService | None = None,
) -> web.Application:
    config.validate()
    if service is not None:
        if store is not None and service.store is not store:
            raise ValueError("Injected RemoteService and RemoteStore must match.")
        remote_service = service
        remote_store = service.store
    else:
        remote_store = store or RemoteStore(config.database_path)
        remote_service = RemoteService(
            remote_store,
            command_delivery_ttl_seconds=config.command_delivery_ttl_seconds,
        )
    if pairing_service is not None and pairing_service.store is not remote_store:
        raise ValueError("Injected PairingService and RemoteStore must match.")
    if linking_service is not None and linking_service.store is not remote_store:
        raise ValueError("Injected LinkingService and RemoteStore must match.")
    if onboarding_service is not None and onboarding_service.store is not remote_store:
        raise ValueError("Injected OnboardingService and RemoteStore must match.")
    pairing = pairing_service or PairingService(remote_store)
    linking = linking_service or LinkingService(remote_store)
    app = web.Application(client_max_size=MAX_MESSAGE_BYTES)
    app[CONFIG_KEY] = config
    app[STORE_KEY] = remote_store
    app[SERVICE_KEY] = remote_service
    app[PAIRING_KEY] = pairing
    app[LINKING_KEY] = linking
    if onboarding_service is not None:
        app[ONBOARDING_KEY] = onboarding_service
    app.router.add_get("/healthz", _health)
    app.router.add_post("/remote/v1/pair", _redeem_pairing_ticket)
    app.router.add_post("/remote/v1/link/begin", _link_begin)
    app.router.add_post("/remote/v1/link/status", _link_status)
    app.router.add_post("/remote/v1/link/complete", _link_complete)
    if onboarding_service is not None:
        app.router.add_post("/remote/v1/onboarding/begin", _onboarding_begin)
        app.router.add_post("/remote/v1/onboarding/status", _onboarding_status)
        app.router.add_post("/remote/v1/onboarding/complete", _onboarding_complete)
        app.router.add_get(
            "/remote/v1/onboarding/discord/callback",
            _onboarding_discord_callback,
        )
    app.router.add_get("/remote/v1/agent", _agent_websocket)
    app.on_cleanup.append(_cleanup)
    return app


async def _health(_: web.Request) -> web.Response:
    return web.json_response({"status": "ok", "protocol": PROTOCOL_VERSION})


async def _redeem_pairing_ticket(request: web.Request) -> web.Response:
    """Redeem a Discord-issued development ticket without accepting identity data."""

    has_forbidden_body = request.can_read_body
    ticket = _pairing_credential(request)
    if has_forbidden_body or request.query_string:
        ticket = ""
    try:
        redemption = request.app[PAIRING_KEY].redeem(
            ticket or "", peer_source=_peer_source(request)
        )
    except PairingError as exc:
        headers = _no_store_headers()
        if exc.retry_after_seconds is not None:
            headers["Retry-After"] = str(exc.retry_after_seconds)
        if exc.http_status == 401:
            headers["WWW-Authenticate"] = "Pairing"
        response = web.json_response(
            {
                "error": {
                    "code": exc.code,
                    "message": _PAIRING_HTTP_MESSAGES.get(
                        exc.code, "Pairing could not be completed."
                    ),
                }
            },
            status=exc.http_status,
            headers=headers,
        )
        if has_forbidden_body:
            response.force_close()
        return response
    return web.json_response(
        {
            "protocol": PROTOCOL_VERSION,
            "device_credential": redemption.device_credential,
            "agent_websocket_path": "/remote/v1/agent",
        },
        status=201,
        headers=_no_store_headers(),
    )


async def _link_begin(request: web.Request) -> web.Response:
    if request.can_read_body or request.query_string:
        return _link_error_response(
            LinkingError(
                "LINK_REQUEST_INVALID",
                "Remote linking request was invalid.",
                http_status=400,
            ),
            force_close=request.can_read_body,
        )
    secret = _linking_credential(request)
    if secret is None:
        return _link_unauthorized()
    try:
        started = await request.app[LINKING_KEY].begin(
            secret,
            peer_source=_peer_source(request),
        )
    except LinkingError as exc:
        return _link_error_response(exc)
    return web.json_response(
        {
            "protocol": PROTOCOL_VERSION,
            "link_code": started.code,
            "expires_at": started.expires_at,
        },
        status=201,
        headers=_no_store_headers(),
    )


async def _link_status(request: web.Request) -> web.Response:
    if request.can_read_body or request.query_string:
        return _link_error_response(
            LinkingError(
                "LINK_REQUEST_INVALID",
                "Remote linking request was invalid.",
                http_status=400,
            ),
            force_close=request.can_read_body,
        )
    secret = _linking_credential(request)
    if secret is None:
        return _link_unauthorized()
    try:
        ready = await request.app[LINKING_KEY].poll(secret)
    except LinkingError as exc:
        return _link_error_response(exc)
    if ready is None:
        return web.json_response(
            {"protocol": PROTOCOL_VERSION, "status": "pending"},
            status=202,
            headers=_no_store_headers(),
        )
    return web.json_response(
        {
            "protocol": PROTOCOL_VERSION,
            "status": "ready",
            "device_credential": ready.device_credential,
            "agent_websocket_path": "/remote/v1/agent",
        },
        status=201,
        headers=_no_store_headers(),
    )


async def _link_complete(request: web.Request) -> web.Response:
    if request.can_read_body or request.query_string:
        return _link_error_response(
            LinkingError(
                "LINK_REQUEST_INVALID",
                "Remote linking request was invalid.",
                http_status=400,
            ),
            force_close=request.can_read_body,
        )
    secret = _linking_credential(request)
    if secret is None:
        return _link_unauthorized()
    try:
        await request.app[LINKING_KEY].complete(secret)
    except LinkingError as exc:
        return _link_error_response(exc)
    return web.Response(status=204, headers=_no_store_headers())


def _link_unauthorized() -> web.Response:
    response = web.json_response(
        {
            "error": {
                "code": "LINK_AUTH_REQUIRED",
                "message": "Remote linking authentication is required.",
            }
        },
        status=401,
        headers=_no_store_headers(),
    )
    response.headers["WWW-Authenticate"] = "Linking"
    return response


def _link_error_response(
    exc: LinkingError,
    *,
    force_close: bool = False,
) -> web.Response:
    headers = _no_store_headers()
    if exc.retry_after_seconds is not None:
        headers["Retry-After"] = str(exc.retry_after_seconds)
    if exc.http_status == 401:
        headers["WWW-Authenticate"] = "Linking"
    response = web.json_response(
        {"error": {"code": exc.code, "message": exc.user_message}},
        status=exc.http_status,
        headers=headers,
    )
    if force_close:
        response.force_close()
    return response


async def _onboarding_begin(request: web.Request) -> web.Response:
    if request.can_read_body or request.query_string:
        return _onboarding_error_response(
            OnboardingError(
                "ONBOARDING_REQUEST_INVALID",
                "Remote setup request was invalid.",
                http_status=400,
            ),
            force_close=request.can_read_body,
        )
    secret = _onboarding_credential(request)
    if secret is None:
        return _onboarding_unauthorized()
    try:
        started = await request.app[ONBOARDING_KEY].begin(
            secret,
            peer_source=_peer_source(request),
        )
    except OnboardingError as exc:
        return _onboarding_error_response(exc)
    return web.json_response(
        {
            "protocol": PROTOCOL_VERSION,
            "authorization_url": started.authorization_url,
            "expires_at": started.expires_at,
        },
        status=201,
        headers=_no_store_headers(),
    )


async def _onboarding_status(request: web.Request) -> web.Response:
    if request.can_read_body or request.query_string:
        return _onboarding_error_response(
            OnboardingError(
                "ONBOARDING_REQUEST_INVALID",
                "Remote setup request was invalid.",
                http_status=400,
            ),
            force_close=request.can_read_body,
        )
    secret = _onboarding_credential(request)
    if secret is None:
        return _onboarding_unauthorized()
    try:
        ready = await request.app[ONBOARDING_KEY].poll(secret)
    except OnboardingError as exc:
        return _onboarding_error_response(exc)
    if ready is None:
        return web.json_response(
            {"protocol": PROTOCOL_VERSION, "status": "pending"},
            status=202,
            headers=_no_store_headers(),
        )
    return web.json_response(
        {
            "protocol": PROTOCOL_VERSION,
            "status": "ready",
            "device_credential": ready.device_credential,
            "agent_websocket_path": "/remote/v1/agent",
        },
        status=201,
        headers=_no_store_headers(),
    )


async def _onboarding_complete(request: web.Request) -> web.Response:
    if request.can_read_body or request.query_string:
        return _onboarding_error_response(
            OnboardingError(
                "ONBOARDING_REQUEST_INVALID",
                "Remote setup request was invalid.",
                http_status=400,
            ),
            force_close=request.can_read_body,
        )
    secret = _onboarding_credential(request)
    if secret is None:
        return _onboarding_unauthorized()
    try:
        await request.app[ONBOARDING_KEY].complete(secret)
    except OnboardingError as exc:
        return _onboarding_error_response(exc)
    return web.Response(status=204, headers=_no_store_headers())


async def _onboarding_discord_callback(request: web.Request) -> web.Response:
    service = request.app[ONBOARDING_KEY]
    state = request.query.get("state", "")
    oauth_error = request.query.get("error", "")
    code = request.query.get("code", "")
    if oauth_error:
        await service.deny_callback(state=state)
        return _onboarding_browser_page(
            "Remote setup not authorized",
            "Discord authorization was declined. You can close this tab and return to Ultimate Macro.",
            success=False,
        )
    try:
        await service.authorize_callback(state=state, code=code)
    except OnboardingError as exc:
        return _onboarding_browser_page(
            "Remote setup could not be completed",
            exc.user_message,
            success=False,
            status=exc.http_status,
        )
    return _onboarding_browser_page(
        "Discord connected",
        "Ultimate Macro Remote is linked. You can close this tab and return to the macro.",
        success=True,
    )


def _onboarding_browser_page(
    title: str,
    message: str,
    *,
    success: bool,
    status: int = 200,
) -> web.Response:
    safe_title = html.escape(title, quote=True)
    safe_message = html.escape(message, quote=True)
    accent = "#3ba55d" if success else "#ed4245"
    body = (
        "<!doctype html><html><head><meta charset='utf-8'>"
        "<meta name='viewport' content='width=device-width,initial-scale=1'>"
        f"<title>{safe_title}</title>"
        "<style>body{font-family:system-ui,sans-serif;background:#111827;color:#f9fafb;"
        "display:grid;place-items:center;min-height:100vh;margin:0}main{max-width:34rem;"
        "padding:2rem;border:1px solid #374151;border-radius:1rem;background:#1f2937}"
        f"h1{{color:{accent};margin-top:0}}p{{line-height:1.55}}</style></head>"
        f"<body><main><h1>{safe_title}</h1><p>{safe_message}</p></main></body></html>"
    )
    return web.Response(
        text=body,
        content_type="text/html",
        status=status,
        headers=_no_store_headers(),
    )


def _onboarding_unauthorized() -> web.Response:
    response = web.json_response(
        {
            "error": {
                "code": "ONBOARDING_AUTH_REQUIRED",
                "message": "Remote setup authentication is required.",
            }
        },
        status=401,
        headers=_no_store_headers(),
    )
    response.headers["WWW-Authenticate"] = "Onboarding"
    return response


def _onboarding_error_response(
    exc: OnboardingError,
    *,
    force_close: bool = False,
) -> web.Response:
    response = web.json_response(
        {"error": {"code": exc.code, "message": exc.user_message}},
        status=exc.http_status,
        headers=_no_store_headers(),
    )
    if force_close:
        response.force_close()
    return response


async def _agent_websocket(request: web.Request) -> web.StreamResponse:
    config = request.app[CONFIG_KEY]
    service = request.app[SERVICE_KEY]
    credential = _bearer_credential(request)
    if credential is None:
        raise web.HTTPUnauthorized(
            text="Agent authentication required.",
            headers={"WWW-Authenticate": "Bearer"},
        )
    device = service.store.authenticate(credential)
    if device is None:
        raise web.HTTPUnauthorized(
            text="Agent authentication failed.",
            headers={"WWW-Authenticate": "Bearer"},
        )

    socket = web.WebSocketResponse(
        max_msg_size=MAX_MESSAGE_BYTES,
        autoping=True,
        compress=False,
    )
    await socket.prepare(request)
    session: AgentSession | None = None
    try:
        first = await socket.receive(timeout=config.first_message_timeout_seconds)
        hello = _parse_text_message(first)
        if not isinstance(hello, HelloMessage):
            raise ProtocolError("HELLO_REQUIRED", "First Agent message must be HELLO.")
        session = await service.prepare_agent(device, hello, socket)
        await session.send(
            encode_welcome(
                config.heartbeat_interval_seconds,
                utc_now(),
                service.reconciliation_commands(device.device_id),
            )
        )
        await service.activate_agent(session)

        while not socket.closed:
            try:
                message = await socket.receive(
                    timeout=config.heartbeat_timeout_seconds
                )
            except TimeoutError:
                await socket.close(code=1001, message=b"heartbeat_timeout")
                break
            if message.type in {WSMsgType.CLOSE, WSMsgType.CLOSING, WSMsgType.CLOSED}:
                break
            if message.type is WSMsgType.ERROR:
                break
            parsed = _parse_text_message(message)
            if not isinstance(parsed, (HeartbeatMessage, CommandUpdateMessage)):
                raise ProtocolError("UNEXPECTED_MESSAGE", "HELLO may be sent only once.")
            await service.handle_agent_message(session, parsed)
    except (ProtocolError, RemoteServiceError) as exc:
        code = exc.code
        LOGGER.warning("Closing Agent connection: %s", code)
        await socket.close(code=1008, message=code.encode("ascii")[:120])
    except (StoreError, ConnectionError, OSError):
        LOGGER.warning("Closing Agent connection: backend_or_connection_error")
        await socket.close(code=1011, message=b"backend_or_connection_error")
    except TimeoutError:
        await socket.close(code=1008, message=b"hello_timeout")
    finally:
        if session is not None:
            await service.unregister_agent(session)
    return socket


def _parse_text_message(message: object):
    message_type = getattr(message, "type", None)
    if message_type is not WSMsgType.TEXT:
        raise ProtocolError("TEXT_REQUIRED", "Agent messages must be JSON text.")
    return parse_agent_message(getattr(message, "data"))


def _bearer_credential(request: web.Request) -> str | None:
    return _authorization_credential(request, "Bearer", 160)


def _pairing_credential(request: web.Request) -> str | None:
    return _authorization_credential(request, "Pairing", 96)


def _linking_credential(request: web.Request) -> str | None:
    return _authorization_credential(request, "Linking", 96)


def _onboarding_credential(request: web.Request) -> str | None:
    return _authorization_credential(request, "Onboarding", 96)


def _authorization_credential(
    request: web.Request,
    scheme: str,
    maximum_length: int,
) -> str | None:
    header = request.headers.get("Authorization", "")
    prefix = f"{scheme} "
    if not header.startswith(prefix) or header.count(" ") != 1:
        return None
    credential = header[len(prefix) :]
    if not credential or len(credential) > maximum_length:
        return None
    return credential


def _peer_source(request: web.Request) -> str:
    transport = request.transport
    peer = transport.get_extra_info("peername") if transport is not None else None
    if isinstance(peer, tuple) and peer and isinstance(peer[0], str):
        return peer[0]
    return "unknown"


def _no_store_headers() -> dict[str, str]:
    return {"Cache-Control": "no-store", "Pragma": "no-cache"}


async def _cleanup(app: web.Application) -> None:
    await app[SERVICE_KEY].close()
    app[STORE_KEY].close()


def build_ssl_context(config: RemoteConfig) -> ssl.SSLContext | None:
    if config.tls_certificate is None or config.tls_private_key is None:
        return None
    context = ssl.create_default_context(ssl.Purpose.CLIENT_AUTH)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.load_cert_chain(config.tls_certificate, config.tls_private_key)
    return context


def main() -> None:
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s %(message)s",
    )
    config = RemoteConfig.from_environment()
    web.run_app(
        create_app(config),
        host=config.bind_host,
        port=config.bind_port,
        ssl_context=build_ssl_context(config),
        access_log=None,
    )


if __name__ == "__main__":
    main()
