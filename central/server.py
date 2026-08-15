from __future__ import annotations

import asyncio
import logging
import ssl

from aiohttp import WSMsgType, web

from .config import RemoteConfig
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


def create_app(
    config: RemoteConfig,
    *,
    store: RemoteStore | None = None,
    service: RemoteService | None = None,
    pairing_service: PairingService | None = None,
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
    pairing = pairing_service or PairingService(remote_store)
    app = web.Application(client_max_size=MAX_MESSAGE_BYTES)
    app[CONFIG_KEY] = config
    app[STORE_KEY] = remote_store
    app[SERVICE_KEY] = remote_service
    app[PAIRING_KEY] = pairing
    app.router.add_get("/healthz", _health)
    app.router.add_post("/remote/v1/pair", _redeem_pairing_ticket)
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
    header = request.headers.get("Authorization", "")
    if not header.startswith("Bearer ") or header.count(" ") != 1:
        return None
    credential = header[7:]
    if not credential or len(credential) > 160:
        return None
    return credential


def _pairing_credential(request: web.Request) -> str | None:
    header = request.headers.get("Authorization", "")
    if not header.startswith("Pairing ") or header.count(" ") != 1:
        return None
    credential = header[8:]
    if not credential or len(credential) > 96:
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
