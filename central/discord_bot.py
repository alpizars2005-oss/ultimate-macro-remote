from __future__ import annotations

import logging
import os
import time
import unicodedata
from collections import OrderedDict
from dataclasses import dataclass, field
from typing import Awaitable, Callable

import discord
from discord import app_commands

from .pairing import PairingError, PairingService
from .protocol import CommandStatus, Operation, ProtocolError
from .service import RemoteService, RemoteServiceError
from .store import CommandRecord


LOGGER = logging.getLogger("ultimate_remote.discord")
NO_MENTIONS = discord.AllowedMentions.none()

_SERVICE_ERROR_MESSAGES = {
    "DEVICE_NOT_LINKED": "No Remote device is linked. Use `/macro pair` to create a development pairing ticket.",
    "DEVICE_OFFLINE": "The linked device is offline or not connected.",
    "MULTIPLE_DEVICES": "More than one device is linked. Device selection is not available in this milestone.",
    "OPERATION_UNSUPPORTED": "The connected Agent does not support that Remote operation.",
    "COMMAND_IN_PROGRESS": "Another gameplay-changing command is already in progress or awaiting reconciliation.",
    "COMMAND_NOT_FOUND": "That Remote command is no longer available.",
}
_INPUT_ERROR_MESSAGES = {
    "INVALID_STRATEGY_ID": "Select a strategy from the current Remote strategy catalog.",
    "INVALID_ARGUMENTS": "The Remote command arguments were invalid.",
    "INVALID_DISCORD_USER_ID": "Discord could not provide a valid account identity.",
}
_AGENT_ERROR_MESSAGES = {
    "STRATEGY_NOT_FOUND": "The selected strategy is no longer installed on the Remote device.",
    "NOT_PRESENT_LOCALLY": "The Agent could not reconcile this command from its durable local journal. It was not replayed; verify Remote state before retrying.",
    "MACRO_ALREADY_RUNNING": "The macro is already running. Use `/macro switch` instead.",
    "MACRO_NOT_RUNNING": "The macro is not currently running.",
    "MAILBOX_BUSY": "The device is already processing another safe-boundary request.",
    "DEVICE_REVOKED_OUTCOME_UNKNOWN": "The device was revoked while this command was unresolved. Its local outcome is unknown.",
    "SERVER_RESTART": "The central service restarted before the read completed.",
    "SERVER_RESTART_READ_FAILED": "The central service restarted before the read completed.",
    "CONNECTION_LOST_READ_FAILED": "The Agent disconnected before the read completed.",
    "DELIVERY_TIMEOUT_READ_FAILED": "The Agent did not acknowledge the read before its delivery deadline.",
    "SERVER_SHUTDOWN_READ_FAILED": "The central service stopped before the read completed.",
}
_PAIRING_ERROR_MESSAGES = {
    "PAIRING_RATE_LIMITED": "Too many pairing attempts. Try again later.",
    "DEVICE_ALREADY_LINKED": "A Remote device is already linked to this Discord account.",
    "PAIRING_UNAVAILABLE": "Development pairing is temporarily unavailable.",
}


@dataclass(frozen=True, slots=True)
class DiscordBotOptions:
    token: str = field(default="", repr=False)
    guild_id: int | None = None
    read_wait_seconds: float = 8.0
    action_wait_seconds: float = 1.5
    strategy_cache_ttl_seconds: float = 60.0

    @classmethod
    def from_environment(cls) -> "DiscordBotOptions":
        guild_text = os.getenv(
            "DISCORD_GUILD_ID", os.getenv("GUILD_ID", "")
        ).strip()
        options = cls(
            token=os.getenv("DISCORD_TOKEN", "").strip(),
            guild_id=int(guild_text) if guild_text else None,
        )
        options.validate()
        return options

    def validate(self) -> None:
        if self.guild_id is not None and self.guild_id <= 0:
            raise ValueError("DISCORD_GUILD_ID must be a positive snowflake.")
        if self.read_wait_seconds <= 0 or self.action_wait_seconds <= 0:
            raise ValueError("Discord command wait times must be positive.")
        if not 5 <= self.strategy_cache_ttl_seconds <= 600:
            raise ValueError("Strategy cache TTL must be between 5 and 600 seconds.")


@dataclass(frozen=True, slots=True)
class SafeReply:
    content: str


class StrategyCatalogCache:
    def __init__(
        self,
        ttl_seconds: float,
        *,
        monotonic: Callable[[], float] = time.monotonic,
    ) -> None:
        self._ttl_seconds = ttl_seconds
        self._monotonic = monotonic
        self._entries: OrderedDict[
            str, tuple[float, tuple[tuple[str, str], ...]]
        ] = OrderedDict()

    def put(self, owner: str, strategies: list[tuple[str, str]]) -> None:
        self._entries.pop(owner, None)
        self._entries[owner] = (
            self._monotonic() + self._ttl_seconds,
            tuple(strategies),
        )
        while len(self._entries) > 256:
            self._entries.popitem(last=False)

    def get(self, owner: str) -> tuple[tuple[str, str], ...]:
        entry = self._entries.get(owner)
        if entry is None:
            return ()
        expires_at, strategies = entry
        if self._monotonic() >= expires_at:
            self._entries.pop(owner, None)
            return ()
        self._entries.move_to_end(owner)
        return strategies


class MacroCommandController:
    """Safe presentation layer between Discord interactions and RemoteService."""

    def __init__(
        self,
        service: RemoteService,
        pairing: PairingService,
        options: DiscordBotOptions | None = None,
    ) -> None:
        self.service = service
        self.pairing = pairing
        self.options = options or DiscordBotOptions()
        self.options.validate()
        self.catalog = StrategyCatalogCache(
            self.options.strategy_cache_ttl_seconds
        )

    async def pair(self, discord_user_id: int) -> SafeReply:
        try:
            issued = self.pairing.issue_for_discord_user(discord_user_id)
        except PairingError as exc:
            return SafeReply(
                _PAIRING_ERROR_MESSAGES.get(
                    exc.code, "Development pairing could not be completed."
                )
            )
        endpoint = (
            f"\nRedeem with an HTTPS POST to <{issued.redemption_url}>."
            if issued.redemption_url is not None
            else (
                "\nNo public HTTPS origin is configured; this ticket is for "
                "same-host simulator testing against the configured literal-loopback "
                "listener only (default `http://127.0.0.1:8765/remote/v1/pair`). "
                "Configure a trusted HTTPS origin before any cross-PC use."
            )
        )
        return SafeReply(
            "Development pairing ticket (shown once):\n"
            f"```\n{issued.ticket}\n```"
            f"Expires at `{issued.expires_at}`.{endpoint}\n"
            "Send it only in the `Authorization: Pairing …` header; never put it in a URL."
        )

    async def status(self, discord_user_id: int) -> SafeReply:
        return await self._dispatch(
            discord_user_id, Operation.GET_STATUS, wait=self.options.read_wait_seconds
        )

    async def strategies(self, discord_user_id: int) -> SafeReply:
        return await self._dispatch(
            discord_user_id,
            Operation.LIST_STRATEGIES,
            wait=self.options.read_wait_seconds,
        )

    async def start(self, discord_user_id: int, strategy_id: str) -> SafeReply:
        return await self._dispatch(
            discord_user_id,
            Operation.START_STRATEGY,
            strategy_id=strategy_id,
            wait=self.options.action_wait_seconds,
        )

    async def stop(self, discord_user_id: int) -> SafeReply:
        return await self._dispatch(
            discord_user_id,
            Operation.STOP_SAFE,
            wait=self.options.action_wait_seconds,
        )

    async def switch(self, discord_user_id: int, strategy_id: str) -> SafeReply:
        return await self._dispatch(
            discord_user_id,
            Operation.SWITCH_STRATEGY,
            strategy_id=strategy_id,
            wait=self.options.action_wait_seconds,
        )

    def autocomplete(
        self, discord_user_id: int, current: str
    ) -> list[app_commands.Choice[str]]:
        owner = str(discord_user_id)
        needle = current.casefold().strip()[:100]
        choices: list[app_commands.Choice[str]] = []
        for strategy_id, raw_name in self.catalog.get(owner):
            if needle and needle not in raw_name.casefold():
                continue
            choices.append(
                app_commands.Choice(
                    name=_safe_display_text(raw_name, 100), value=strategy_id
                )
            )
            if len(choices) == 25:
                break
        return choices

    async def _dispatch(
        self,
        discord_user_id: int,
        operation: Operation,
        *,
        strategy_id: str | None = None,
        wait: float,
    ) -> SafeReply:
        try:
            command = await self.service.dispatch_for_user(
                discord_user_id=discord_user_id,
                operation=operation,
                strategy_id=strategy_id,
            )
        except RemoteServiceError as exc:
            if exc.command_id is not None:
                try:
                    command = self.service.get_command_for_user(
                        discord_user_id=discord_user_id,
                        command_id=exc.command_id,
                    )
                    return self._render_command(str(discord_user_id), command)
                except RemoteServiceError:
                    if operation in {
                        Operation.START_STRATEGY,
                        Operation.STOP_SAFE,
                        Operation.SWITCH_STRATEGY,
                    }:
                        return SafeReply(_unconfirmed_mutation_message())
                except Exception as lookup_exc:
                    LOGGER.error(
                        "Discord command lookup failed for %s (%s)",
                        operation.value,
                        type(lookup_exc).__name__,
                    )
                    if operation in {
                        Operation.START_STRATEGY,
                        Operation.STOP_SAFE,
                        Operation.SWITCH_STRATEGY,
                    }:
                        return SafeReply(_unconfirmed_mutation_message())
                    return SafeReply("The Remote request could not be completed.")
            return SafeReply(
                _SERVICE_ERROR_MESSAGES.get(
                    exc.code, "The Remote request could not be completed."
                )
            )
        except ProtocolError as exc:
            return SafeReply(
                _INPUT_ERROR_MESSAGES.get(
                    exc.code, "The Remote request was rejected as invalid."
                )
            )
        except Exception as exc:  # fail closed without rendering exception details
            LOGGER.error(
                "Discord dispatch failed for %s (%s)",
                operation.value,
                type(exc).__name__,
            )
            if operation in {
                Operation.START_STRATEGY,
                Operation.STOP_SAFE,
                Operation.SWITCH_STRATEGY,
            }:
                return SafeReply(_unconfirmed_mutation_message())
            return SafeReply("The Remote request could not be completed.")

        try:
            command = await self.service.wait_for_terminal(
                discord_user_id=discord_user_id,
                command_id=command.command_id,
                timeout_seconds=wait,
            )
        except RemoteServiceError as exc:
            try:
                current = self.service.get_command_for_user(
                    discord_user_id=discord_user_id,
                    command_id=command.command_id,
                )
                return self._render_command(str(discord_user_id), current)
            except RemoteServiceError:
                pass
            except Exception as lookup_exc:
                LOGGER.error(
                    "Discord command lookup failed for %s (%s)",
                    operation.value,
                    type(lookup_exc).__name__,
                )
            if operation in {
                Operation.START_STRATEGY,
                Operation.STOP_SAFE,
                Operation.SWITCH_STRATEGY,
            }:
                return SafeReply(_unconfirmed_mutation_message())
            return SafeReply(
                _SERVICE_ERROR_MESSAGES.get(
                    exc.code, "The Remote request could not be completed."
                )
            )
        except Exception as exc:
            LOGGER.error(
                "Discord command wait failed for %s (%s)",
                operation.value,
                type(exc).__name__,
            )
            if operation in {
                Operation.START_STRATEGY,
                Operation.STOP_SAFE,
                Operation.SWITCH_STRATEGY,
            }:
                return SafeReply(_unconfirmed_mutation_message())
            return SafeReply("The Remote request could not be completed.")
        return self._render_command(str(discord_user_id), command)

    def _render_command(self, owner: str, command: CommandRecord) -> SafeReply:
        if command.status is CommandStatus.FAILED:
            return SafeReply(
                _AGENT_ERROR_MESSAGES.get(
                    command.error_code or "",
                    "The Agent reported a failure. No technical details were exposed.",
                )
            )
        if command.status is CommandStatus.RECONCILING:
            return SafeReply(
                "The connection was lost and this command's local outcome is unknown. "
                "Do not retry until the Agent reconnects and reconciles it."
            )
        if command.status is not CommandStatus.COMPLETED:
            state_text = _nonterminal_state_text(command)
            return SafeReply(
                f"The `{command.operation.value}` request is {state_text}; this is not completion."
            )

        result = command.result or {}
        if command.operation is Operation.LIST_STRATEGIES:
            raw_items = result.get("strategies")
            items: list[tuple[str, str]] = []
            if isinstance(raw_items, list):
                for item in raw_items:
                    if not isinstance(item, dict):
                        continue
                    strategy_id = item.get("strategy_id")
                    name = item.get("name")
                    if isinstance(strategy_id, str) and isinstance(name, str):
                        items.append((strategy_id, name))
            self.catalog.put(owner, items)
            return SafeReply(_render_strategy_catalog(items))
        if command.operation is Operation.GET_STATUS:
            snapshot = result.get("snapshot")
            return SafeReply(self._render_snapshot(owner, snapshot))
        if command.operation is Operation.START_STRATEGY:
            return SafeReply(
                "Start confirmed: the requested strategy lifecycle and Roblox are running."
            )
        if command.operation is Operation.STOP_SAFE:
            return SafeReply("The macro stopped safely at its validated boundary.")
        if command.operation is Operation.SWITCH_STRATEGY:
            return SafeReply(
                "The strategy switch was applied at the safe boundary. This does not assert that a new match finished joining."
            )
        return SafeReply("The Remote command completed.")

    def _render_snapshot(self, owner: str, value: object) -> str:
        if not isinstance(value, dict):
            return "Status was returned in an unexpected form."
        state = {
            "not_running": "Not running",
            "idle": "Idle",
            "running": "Running",
            "unknown": "Unknown",
        }.get(value.get("macro_state"), "Unknown")
        roblox = "Running" if value.get("roblox_running") is True else "Not running"
        current_id = value.get("current_strategy_id")
        strategy = "None or outside the approved Remote catalog"
        if isinstance(current_id, str):
            for strategy_id, raw_name in self.catalog.get(owner):
                if strategy_id == current_id:
                    strategy = _safe_display_text(raw_name, 200)
                    break
        return f"Macro: **{state}**\nRoblox: **{roblox}**\nStrategy: {strategy}"


class RemoteDiscordClient(discord.Client):
    def __init__(
        self,
        service: RemoteService,
        pairing: PairingService,
        options: DiscordBotOptions,
    ) -> None:
        super().__init__(
            intents=discord.Intents.none(), allowed_mentions=NO_MENTIONS
        )
        self.options = options
        self.controller = MacroCommandController(service, pairing, options)
        self.tree = app_commands.CommandTree(self)
        self.macro_group = app_commands.Group(
            name="macro", description="Control a linked Ultimate Macro device"
        )
        self._register_commands()
        self.tree.add_command(self.macro_group)

    async def setup_hook(self) -> None:
        if self.options.guild_id is not None:
            guild = discord.Object(id=self.options.guild_id)
            self.tree.copy_global_to(guild=guild)
            await self.tree.sync(guild=guild)
        else:
            await self.tree.sync()

    def _register_commands(self) -> None:
        @self.macro_group.command(
            name="pair", description="Create a short-lived development pairing ticket"
        )
        async def pair(interaction: discord.Interaction) -> None:
            await self._respond(interaction, self.controller.pair)

        @self.macro_group.command(name="status", description="Show Remote macro status")
        async def status(interaction: discord.Interaction) -> None:
            await self._respond(interaction, self.controller.status)

        @self.macro_group.command(
            name="strategies", description="List strategies reported by the Agent"
        )
        async def strategies(interaction: discord.Interaction) -> None:
            await self._respond(interaction, self.controller.strategies)

        @self.macro_group.command(name="start", description="Start a Remote strategy")
        @app_commands.describe(strategy="Opaque strategy selection from /macro strategies")
        async def start(interaction: discord.Interaction, strategy: str) -> None:
            await self._respond_with_strategy(
                interaction, self.controller.start, strategy
            )

        @start.autocomplete("strategy")
        async def start_autocomplete(
            interaction: discord.Interaction, current: str
        ) -> list[app_commands.Choice[str]]:
            return self.controller.autocomplete(interaction.user.id, current)

        @self.macro_group.command(
            name="stop", description="Stop safely at the next validated boundary"
        )
        async def stop(interaction: discord.Interaction) -> None:
            await self._respond(interaction, self.controller.stop)

        @self.macro_group.command(
            name="switch", description="Switch strategy at a safe boundary"
        )
        @app_commands.describe(strategy="Opaque strategy selection from /macro strategies")
        async def switch(interaction: discord.Interaction, strategy: str) -> None:
            await self._respond_with_strategy(
                interaction, self.controller.switch, strategy
            )

        @switch.autocomplete("strategy")
        async def switch_autocomplete(
            interaction: discord.Interaction, current: str
        ) -> list[app_commands.Choice[str]]:
            return self.controller.autocomplete(interaction.user.id, current)

    async def _respond(
        self,
        interaction: discord.Interaction,
        callback: Callable[[int], Awaitable[SafeReply]],
    ) -> None:
        await interaction.response.defer(ephemeral=True, thinking=True)
        try:
            reply = await callback(interaction.user.id)
        except Exception as exc:
            LOGGER.error("Discord response failed (%s)", type(exc).__name__)
            reply = SafeReply("The Remote request could not be completed.")
        await interaction.edit_original_response(
            content=reply.content, allowed_mentions=NO_MENTIONS
        )

    async def _respond_with_strategy(
        self,
        interaction: discord.Interaction,
        callback: Callable[[int, str], Awaitable[SafeReply]],
        strategy_id: str,
    ) -> None:
        await interaction.response.defer(ephemeral=True, thinking=True)
        try:
            reply = await callback(interaction.user.id, strategy_id)
        except Exception as exc:
            LOGGER.error("Discord response failed (%s)", type(exc).__name__)
            reply = SafeReply("The Remote request could not be completed.")
        await interaction.edit_original_response(
            content=reply.content, allowed_mentions=NO_MENTIONS
        )


def _safe_display_text(value: str, limit: int) -> str:
    cleaned = "".join(
        character
        for character in value
        if not unicodedata.category(character).startswith("C")
    )
    cleaned = discord.utils.escape_mentions(cleaned)
    for marker in ("\\", "`", "*", "_", "~", "|", ">", "#", "[", "]"):
        cleaned = cleaned.replace(marker, f"\\{marker}")
    return cleaned[:limit] or "Unnamed strategy"


def _render_strategy_catalog(items: list[tuple[str, str]]) -> str:
    if not items:
        return "The Agent reported no available Remote strategies."
    lines = ["Available Remote strategies:"]
    shown = 0
    for _, raw_name in items:
        line = f"• {_safe_display_text(raw_name, 160)}"
        if len("\n".join(lines + [line])) > 1800:
            break
        lines.append(line)
        shown += 1
    if shown < len(items):
        lines.append(f"…and {len(items) - shown} more.")
    lines.append("Use autocomplete on `/macro start` or `/macro switch`.")
    return "\n".join(lines)


def _nonterminal_state_text(command: CommandRecord) -> str:
    if command.status is CommandStatus.QUEUED:
        return "queued for delivery"
    if command.status is CommandStatus.ACCEPTED:
        return "accepted by the Agent"
    if command.status is CommandStatus.EXECUTING:
        if command.operation in {Operation.STOP_SAFE, Operation.SWITCH_STRATEGY}:
            return "executing or waiting for the validated safe boundary"
        if command.operation is Operation.START_STRATEGY:
            return "executing while the requested strategy lifecycle is confirmed"
        return "executing while the Agent collects the requested information"
    return "still in progress"


def _unconfirmed_mutation_message() -> str:
    return (
        "The gameplay-changing request may have been dispatched, but central could "
        "not confirm its current state or outcome. Do not retry until the Agent "
        "reconnects and Remote state is verified."
    )
