# Legacy development pairing fallback

This document describes the **temporary `/macro pair` fallback** that remains in the private development branch for diagnostics and compatibility. It is **not** the intended end-user enrollment flow in R5.

Normal users should use **Connect Discord** OAuth onboarding as described in `remote-preview-r5.md` and `remote-server-r5.md`.

## Why this still exists

The pairing path predates browser OAuth onboarding. It remains useful when a developer needs to test the central/Agent credential boundary without involving the OAuth callback flow.

It does not change protocol 1 and it does not grant any additional Agent capability.

## Issuance

Only the in-process Discord command surface can issue a ticket. `/macro pair` passes `interaction.user.id` directly to `PairingService`; the slash command has no owner, guild, device, capability, path, or strategy argument.

The Discord response is ephemeral and allowed mentions are disabled.

A ticket has the form:

```text
urpair_v1.<base64url secret>
```

The secret contains 256 random bits from the operating-system cryptographic random source. Tickets are short-lived, single-use, and shown once. SQLite stores a domain-separated digest plus authoritative owner/lifecycle metadata, never the raw ticket.

A newer live ticket supersedes an older unconsumed ticket for the same owner. Issuance and redemption are rate-limited.

An already-linked Discord owner cannot create a second active linked device through this fallback.

## Redemption request

```http
POST /remote/v1/pair HTTP/1.1
Authorization: Pairing urpair_v1.<ticket>
Content-Length: 0
```

The request intentionally has no JSON body, query identity, Discord ID, device ID, capability list, strategy, or local path.

A token placed in a URL/body or sent using the wrong authorization scheme is rejected.

The server derives its rate-limit source from the direct socket peer and does not trust arbitrary forwarded-client headers by default.

## Failure behavior

Malformed, unknown, expired, redeemed, superseded, and owner-conflicting tickets intentionally share a non-enumerating public failure:

```json
{
  "error": {
    "code": "PAIRING_INVALID",
    "message": "Pairing ticket is invalid or no longer usable."
  }
}
```

Rate limiting returns HTTP 429 with bounded retry guidance. Pairing responses are non-cacheable.

## Successful response

Ticket consumption and device creation happen atomically in SQLite. The stored ticket owner becomes the device owner without request-side override.

```json
{
  "protocol": 1,
  "device_credential": "urad_v1.<device UUID>.<secret>",
  "agent_websocket_path": "/remote/v1/agent"
}
```

The bearer is returned once. SQLite stores only its digest.

The Windows Agent stores the resulting enrollment with DPAPI CurrentUser and then uses the device bearer as:

```http
Authorization: Bearer <device credential>
```

on `/remote/v1/agent`.

Pairing tickets are not valid Agent WebSocket credentials, and device credentials are not valid pairing tickets.

## Manual Agent use

The fallback is exposed by the Agent only as a development command:

```powershell
.\UltimateRemoteAgent.exe pair https://remote.example "C:\path\to\Ultimate_Macro_Remote"
```

The ticket is entered through the Agent's hidden prompt rather than a command-line argument.

After a successful fallback enrollment:

```powershell
.\UltimateRemoteAgent.exe run
```

starts the Agent normally.

## Transport boundary

Cross-PC pairing requires a normally trusted HTTPS endpoint. A TLS reverse proxy/tunnel must preserve `Authorization`, POST, and WebSocket Upgrade traffic and should redact authorization headers/credential-bearing response bodies from logs.

The Agent intentionally has no certificate-bypass mode and no cross-machine plaintext enrollment mode.

## Lost-response behavior

If central commits successful redemption but the credential response is lost, the same ticket cannot be replayed because the server does not retain the plaintext device credential.

Operator recovery is to revoke/clean the orphan linked device and issue a new ticket. The fallback deliberately does not add a public arbitrary-device administration command.

## Relationship to R5 OAuth onboarding

OAuth onboarding is preferred because it removes manual pairing-ticket handling from the normal client UX:

```text
Agent bootstrap
  -> random setup secret
  -> browser Discord OAuth identify
  -> central authoritative Discord identity
  -> device credential
  -> DPAPI enrollment
  -> background Agent
```

The fallback remains isolated from the command protocol, so removing it later does not require changing HELLO/WELCOME/COMMAND schemas.
