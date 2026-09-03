# Plan — Macro-first Discord linking code

Target branch: `feature/ultimate-remote-agent`

Goal: replace the normal browser/OAuth enrollment UX with the macro-first linking-code flow requested for official-bot integration, while preserving the existing safe Remote command boundary and keeping the legacy development pairing path available for rollback/debugging.

## Commit plan

1. **Plan macro-first Discord linking**
   - Record protocol, security, compatibility, testing, and rollback intent before runtime changes.

2. **Add one-time Remote link-code service**
   - Add a central, short-lived, single-use link session backed by CSPRNG-generated `ULT-XXXXX-XXXXX` codes.
   - Bind the code only to the Discord identity supplied by the bot interaction.
   - Add begin/status/complete HTTP endpoints for the Windows Agent and `/macro link <code>` reference integration for the preview bot.
   - Keep the code flow rate-limited, non-enumerating, no-store, and free of client-supplied Discord IDs.
   - Add focused Python tests.

3. **Show the link code from the Windows Agent**
   - Add an Agent client for the link protocol.
   - Replace the normal browser launch with a top-most native popup showing the code and exact Discord command.
   - Store the resulting device credential with the existing DPAPI path and preserve background-Agent behavior.
   - Add focused .NET tests.

4. **Package the code-first integration preview**
   - Update reviewer/client docs and package instructions for the no-website linking flow.
   - Keep central secrets out of the ZIP.
   - Add a bot-integration contract for Yoshi and a packaging smoke assertion for the linking instructions.

5. **Verify and deliver**
   - Run the existing Python and .NET CI suites, formatting checks, self-contained Windows publish, and ZIP smoke test.
   - Download the CI-built ZIP artifact for handoff.

## Linking-code contract

- Display format: `ULT-XXXXX-XXXXX`.
- Alphabet: `23456789ABCDEFGHJKLMNPQRSTUVWXYZ` (32 symbols; excludes ambiguous `0`, `1`, `I`, `O`).
- Entropy: 10 independently generated symbols = 50 bits.
- Generator: operating-system CSPRNG (`secrets` on Python; Agent setup secrets remain `RandomNumberGenerator`-backed).
- Lifetime: 10 minutes by default.
- Semantics: single-use; one active linked device per Discord account; a successful duplicate claim by the same owner is idempotent while the session remains pending completion.
- Storage: pending code lookup uses a domain-separated SHA-256 digest; no code becomes a durable device credential.
- Identity: `interaction.user.id` is authoritative. The macro/Agent never submits a Discord user ID.
- Transport: Agent enrollment remains outbound HTTPS/WSS only. No inbound listener is added to the client PC.

## Compatibility / risk

- No gameplay-timing loop receives network or linking work; enrollment remains startup-only and external to `PlayStrategy()`.
- Existing STOP/SWITCH safe-boundary behavior is unchanged.
- Legacy `/macro pair` and OAuth modules remain in-tree during this milestone as rollback/reference paths, but the packaged default becomes link-code onboarding.
- The current Remote gameplay entry point is still based on the private Remote branch compatibility baseline; a separate upstream 1.3.4 runtime rebase must not be silently claimed unless verified.

## Rollback

Revert the link-code commits after this plan. The legacy OAuth onboarding client/service and `/macro pair` fallback remain available, so rollback does not require reconstructing removed enrollment code.
