# Discord bot integration — macro-first link code

This is the handoff contract for the official Ultimate Macro Discord bot.

## User flow

1. The user opens the packaged `Main_Remote.ahk`.
2. Ultimate Macro shows the one-time Remote notice.
3. The user chooses **Generate Link Code**.
4. The Windows Agent starts a short-lived link session with the Remote service.
5. Ultimate Macro shows a top-most popup with a code such as:

   ```text
   ULT-7KQ3M-P9R2X
   ```

   and the exact command:

   ```text
   /macro link ULT-7KQ3M-P9R2X
   ```

6. The user runs that command in the official Discord server.
7. The bot treats `interaction.user.id` as the authoritative Discord identity and claims the code through the shared `LinkingService`.
8. The Agent notices the claim, receives a one-time device credential, stores it with Windows DPAPI CurrentUser protection, acknowledges completion, and closes the popup.
9. Normal Remote commands can then use the linked device.

There is **no browser/website step** in the default flow, and the client never asks the user to type a Discord ID, bot token, OAuth client secret, or device credential.

## Code formula / format

The display code is deliberately simple enough to type but strong enough to avoid treating a short decimal PIN as authentication material.

```text
prefix  = "ULT-"
alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ"
random_symbols = 10
format = ULT-XXXXX-XXXXX
```

Each symbol is selected independently with the operating-system cryptographic random source (`secrets.choice` in the reference server implementation). The alphabet contains 32 symbols, so ten random symbols provide **50 bits of entropy**. `0`, `1`, `I`, and `O` are excluded to reduce copy/typing mistakes.

The service accepts case-insensitive user input and normalizes spaces/hyphens, but always displays the canonical uppercase `ULT-XXXXX-XXXXX` form.

The code expires after 10 minutes by default and is single-use for ownership. It is not the long-term Agent credential.

## Important identity rule

**Do not derive the Discord owner from the code and do not accept a Discord ID from the macro.**

The only owner input for the bot claim is the authenticated Discord interaction:

```python
owner = interaction.user.id
await linking_service.claim(owner, code)
```

That call is the reference integration point. In this repository the same central runtime exposes the exact `LinkingService` instance used by the Agent HTTP endpoints as `RuntimeComponents.linking` / `app[LINKING_KEY]`.

If the official bot runs in a different process or service, use an authenticated private service-to-service adapter around the same claim operation. Do **not** add an unauthenticated public HTTP endpoint that accepts arbitrary Discord IDs.

## Suggested slash command

Reference command shape:

```text
/macro link code:<ULT-XXXXX-XXXXX>
```

Equivalent names such as `/remote link` are fine if the official bot already has a preferred command group. The ZIP currently displays `/macro link`, so change the client text and bot command together if the final name differs.

The response should be ephemeral.

Suggested result messages:

- success: `Ultimate Macro is linked to your Discord account.`
- `LINK_CODE_INVALID`: `That linking code is invalid or expired. Generate a new code in Ultimate Macro.`
- `DEVICE_ALREADY_LINKED`: `A Remote device is already linked to this Discord account.`
- `LINK_RATE_LIMITED`: `Too many linking attempts. Try again later.`
- `LINK_UNAVAILABLE`: `Remote linking is temporarily unavailable.`

Do not echo raw backend exceptions, stack traces, device credentials, setup secrets, or authorization headers into Discord.

## Agent ↔ service protocol

The bot does not need to implement these endpoints, but they explain what the ZIP expects from the Remote service.

All three Agent calls are empty-body HTTPS POST requests with:

```text
Authorization: Linking urlink_v1.<43-char base64url setup secret>
Cache-Control: no-store
```

### Begin

```http
POST /remote/v1/link/begin
```

Successful response:

```json
{
  "protocol": 1,
  "link_code": "ULT-7KQ3M-P9R2X",
  "expires_at": "2026-09-03T01:28:00.000Z"
}
```

### Status

```http
POST /remote/v1/link/status
```

Pending:

```json
{
  "protocol": 1,
  "status": "pending"
}
```

Ready after the Discord claim:

```json
{
  "protocol": 1,
  "status": "ready",
  "device_credential": "urad_v1.<device uuid>.<secret>",
  "agent_websocket_path": "/remote/v1/agent"
}
```

### Complete

```http
POST /remote/v1/link/complete
```

Returns HTTP `204` after the Agent has durably saved the DPAPI-protected enrollment.

If a claimed session expires before the Agent acknowledges it, the provisional device is revoked instead of leaving an orphan usable credential.

## Security properties Yoshi can rely on

- The macro/Agent never submits `discord_user_id`.
- The bot interaction is the authoritative owner identity.
- Link codes are short-lived and rate-limited before lookup.
- Wrong, expired, already-owned-by-another-user, and unknown codes share the same non-enumerating public failure.
- The Agent setup secret is 256-bit and never displayed to the user.
- The final device credential is returned only to the Agent, never to Discord.
- Device credentials are stored on Windows through DPAPI CurrentUser.
- Agent networking remains outbound HTTPS/WSS; the client PC opens no inbound listener.
- The existing Remote command allowlist and safe-boundary behavior are unchanged by linking.

## Minimal implementation checklist for the official bot

1. Add an ephemeral `/macro link code` command.
2. Normalize/forward the user-entered code to the shared linking service.
3. Pass only `interaction.user.id` as owner identity.
4. Map the sanitized error codes above to user-facing responses.
5. Never log the full link code at info level or expose Agent credentials/setup secrets.
6. After a successful claim, the Agent should close its popup automatically within roughly one polling interval.

## Compatibility note

This contract defines the **linking interface** requested for the bot handoff. It does not by itself certify the large AutoHotkey Remote gameplay entry point against a newer upstream macro release. The current private Remote branch should be runtime-rebased/tested separately before anyone labels the whole Remote package as an official Ultimate Macro 1.3.4 build.
