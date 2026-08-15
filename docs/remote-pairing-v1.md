# Temporary development pairing

R2 pairing is an isolated bootstrap mechanism, not a protocol-1 operation and not the
final Discord OAuth flow. It exists so a simulated Agent can obtain the existing R1
device bearer without any manual Discord-user or device-ID input.

## Issuance

Only the in-process Discord command surface can issue a ticket. `/macro pair` passes
`interaction.user.id` directly to `PairingService`; the slash command has no owner,
guild, or device argument. The bot response is ephemeral and disables all allowed
mentions.

A ticket has the form `urpair_v1.<base64url>` and contains 32 bytes (256 bits) from the
operating-system cryptographic random source. It expires after ten minutes by default,
is single-use, and is shown only once. SQLite stores a domain-separated SHA-256 digest,
the authoritative Discord owner, and lifecycle timestamps—never the raw ticket. A new
ticket invalidates an older unconsumed ticket for that owner. Issuance is persistently
rate-limited per Discord account, and an already-linked owner cannot issue another
ticket.

## Redemption request

```http
POST /remote/v1/pair HTTP/1.1
Authorization: Pairing urpair_v1.<ticket>
Content-Length: 0
```

The request has no JSON, form, query, cookie, Discord ID, device ID, capability,
strategy, path, or other identity field. Tokens supplied in a URL, body, or a different
authorization scheme are rejected without consuming a valid ticket. The server derives
the source from the direct socket peer and never trusts `X-Forwarded-For` by default.
When a loopback TLS proxy is used, all clients therefore conservatively share its source
bucket unless a future explicitly trusted-proxy design replaces this behavior.

Every attempt is rate-limited before ticket parsing or lookup, using persistent hashed
source and global sliding-window buckets. Unknown, malformed, expired, redeemed,
superseded, and owner-conflicting tickets receive the same non-enumerating response:

```json
{
  "error": {
    "code": "PAIRING_INVALID",
    "message": "Pairing ticket is invalid or no longer usable."
  }
}
```

Rate limiting returns HTTP 429 with a bounded `Retry-After`. Pairing responses always
carry `Cache-Control: no-store` and `Pragma: no-cache`.

## Successful response

Ticket consumption and device creation occur in one SQLite transaction. Concurrent
redemptions can create only one device credential, and the stored ticket owner becomes
the device owner without request-side override.

```json
{
  "protocol": 1,
  "device_credential": "urad_v1.<device UUID>.<256-bit secret>",
  "agent_websocket_path": "/remote/v1/agent"
}
```

The HTTP status is 201. The bearer is returned once; SQLite stores only its SHA-256
digest. The simulator then uses it in `Authorization: Bearer …` on the unchanged R1
WebSocket. Pairing tickets are rejected by the WebSocket credential parser, and device
credentials are rejected by the pairing parser.

If the HTTP response is lost after the transaction commits, replay remains forbidden
because the server does not retain plaintext device credentials. An operator must
explicitly revoke the orphan device before issuing another ticket. R2 intentionally
adds no public unlink, replacement, or arbitrary-device administration command.

## Transport and logging boundary

The pairing endpoint is safe for cross-PC use only through direct trusted TLS or a
trusted loopback reverse proxy/tunnel. A proxy must preserve `Authorization`, POST, and
WebSocket Upgrade traffic, and must redact authorization headers and pairing response
bodies from logs. The ticket necessarily traverses Discord and is shown ephemerally to
the invoking user before its one-time handoff to the R3 Agent. Bot tokens and backend
secrets remain central-server-only; the device bearer is returned only to the redeeming
Agent. The Agent requires a normally trusted HTTPS/WSS origin and offers no
certificate-bypass or plaintext enrollment mode.
