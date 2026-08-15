from __future__ import annotations

import asyncio
import logging
import ssl

from aiohttp import WSMsgType, web

from .config import RemoteConfig
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
CONFIG_KEY = web.AppKey("remote_config", RemoteConfig)
SERVICE_KEY = web.AppKey("remote_service", RemoteService)
STORE_KEY = web.AppKey("remote_store", RemoteStore)


def create_app(
    config: RemoteConfig,
    *,
    store: RemoteStore | None = None,
    service: RemoteService | None = None,
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
    app = web.Application(client_max_size=MAX_MESSAGE_BYTES)
    app[CONFIG_KEY] = config
    app[STORE_KEY] = remote_store
    app[SERVICE_KEY] = remote_service
    app.router.add_get("/healthz", _health)
    app.router.add_get("/remote/v1/agent", _agent_websocket)
    app.on_cleanup.append(_cleanup)
    return app


async def _health(_: web.Request) -> web.Response:
    return web.json_response({"status": "ok", "protocol": PROTOCOL_VERSION})


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


async def _cleanup(app: web.Application) -> None:
    await app[SERVICE_KEY].close()
    app[STORE_KEY].close()


def _ssl_context(config: RemoteConfig) -> ssl.SSLContext | None:
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
        ssl_context=_ssl_context(config),
        access_log=None,
    )


if __name__ == "__main__":
    main()
