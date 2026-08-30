# Zero-touch safe updates

Date: 2026-08-30

## Goal

Make both `Main.ahk` and `Main_Remote.ahk` refresh themselves from this repository's `main` branch when the macro starts, with no update confirmation or manual ZIP replacement.

## Problems in the previous updater

- It queried `DarksenDev/tds-macro` instead of this maintained repository.
- It selected the first release asset with a regex instead of pinning the exact source revision.
- It downloaded an archive and then deleted the entire current macro directory before proving the new tree could be extracted.
- A failed extraction could therefore leave no working macro.
- Local strategy files could be lost.

## Safety design

1. Query GitHub's public commits API for the exact current `main` commit SHA.
2. Compare that SHA against a local marker stored under `%APPDATA%\Ultimate_Macro\update_commit.txt`.
3. Download the immutable commit-specific GitHub archive, never the mutable branch ZIP.
4. Extract into a temporary staging directory and require `Main.ahk`, `Main_Remote.ahk`, `lib`, `submacros`, and `Resources` before touching the live install.
5. Preserve unique local `.strat` files and local `.env` / `remote_service.url` files when present.
6. Rename the current install to a backup, move the validated staged tree into place, and restore the backup automatically if activation fails.
7. Write the commit marker only after the new tree is active, then restart the same entry point that initiated the update.
8. Keep all existing Darksen attribution and GPL/source comments intact.

## Failure behavior

Network/API/download/extraction failures are silent and non-destructive during normal startup. The currently installed macro remains usable. No generic remote command execution or arbitrary update URL is introduced.

## Verification

- Static contract tests for repository URL, immutable SHA archive, staging-before-swap and backup rollback.
- Verify updater works for both normal and Remote entry points.
- Verify a custom strategy with a unique filename survives an update.
- Verify malformed/incomplete staging never replaces the live install.
