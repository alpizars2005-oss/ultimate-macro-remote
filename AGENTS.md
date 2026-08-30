# Agent development guide

This repository mixes AutoHotkey runtime behavior with remote-management code. Changes must prioritize gameplay timing, compatibility, and security.

## Workflow

1. Read `PLAN.md`, `README.md`, `.env.example`, CI, and the specific runtime modules involved before editing.
2. Use symbol/reference-aware navigation where available, especially before touching large AHK entry points.
3. Verify Discord/network/library APIs against current official documentation before changing integration code; current-doc retrieval tools may assist, but official docs remain authoritative.
4. Keep runtime-path changes minimal. Do not add blocking work, network waits, image analysis, or file I/O to timing-sensitive gameplay paths unless explicitly required and measured.
5. Add or update focused tests/contracts for changed behavior and run existing CI checks.
6. Treat Discord messages, IDs, commands, file paths, strategy files, and environment values as untrusted input. Validate and fail closed where appropriate.
7. Never commit tokens, webhook URLs, user IDs intended to be secret, or machine-specific credentials.
8. Browser automation is optional and should only be introduced for a real web dashboard or browser flow; it is not appropriate for native AutoHotkey GUI verification.

## Review roles

For non-trivial work, perform separate implementation, test, security, and runtime-performance reviews. These are process roles rather than new runtime dependencies.

## Completion gate

Do not call a change complete until relevant CI/tests pass or an exact Windows/Roblox environment limitation is documented. Preserve rollback notes for runtime-adjacent changes.
