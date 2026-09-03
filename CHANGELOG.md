# Changelog

## Unreleased — macro-first Discord linking

### Added

- One-time `ULT-XXXXX-XXXXX` Discord linking-code service with 50-bit CSPRNG display codes, 10-minute expiry, single-owner semantics, and rate limiting.
- Header-only Agent endpoints for link begin/status/complete without client-supplied Discord identity.
- Native top-most Windows Agent popup showing the linking code and `/macro link` command.
- Official-bot handoff contract in `docs/discord-link-code-contract.md` and inside packaged preview ZIPs.
- Focused Python and .NET protocol tests for linking identity, expiry, HTTP trust boundaries, and response validation.

### Changed

- Packaged Remote setup now defaults to macro-first link-code onboarding rather than browser OAuth.
- Preview Terms text now describes the code-based Discord linking path.
- CI also runs on `feature/ultimate-remote-agent` pushes and uploads the link-code preview ZIP.

### Preserved for rollback

- Legacy Discord OAuth onboarding code remains in-tree as a reference/rollback path.
- Legacy `/macro pair` development ticket flow remains available.
- Existing Remote safe-boundary gameplay controls are unchanged.

### Compatibility note

- The linking contract is ready for bot integration, but the private branch's large AutoHotkey Remote gameplay entry point must still be separately rebased/tested before the whole package can be labeled an official Ultimate Macro 1.3.4 build.
