# Security policy

## Sensitive information

Never commit or publish:

- Discord bot tokens;
- populated `.env` files;
- private server identifiers together with credentials;
- screenshots/logs that expose tokens or local sensitive paths.

If a Discord bot token is exposed, revoke/rotate it in the Discord Developer Portal before doing anything else.

## Remote-control boundary

The supported remote design is the Discord bot plus the local command-file handoff documented in `docs/REMOTE_ARCHITECTURE.md`. It does not require an inbound web server or router port forwarding.

Changes that add a new network listener, bypass `ALLOWED_USER_ID`, execute arbitrary paths/commands, or consume gameplay-changing commands during strategy playback are security-sensitive and must be reviewed separately.

## Reporting

Do not place live credentials or private Discord information in a public issue. Reports should use synthetic IDs/tokens and describe the minimum steps required to reproduce the problem.

## Supported version

Security fixes target the current `main` branch. The stock `Main.ahk` file is intentionally retained as a rollback path for the remote layer.
