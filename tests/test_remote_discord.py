from __future__ import annotations

import unittest
from types import SimpleNamespace

import discord

from central.discord_bot import (
    DiscordBotOptions,
    MacroCommandController,
    RemoteDiscordClient,
)
from central.pairing import IssuedPairingTicket, PairingError
from central.protocol import CommandStatus, Operation, ProtocolError
from central.service import RemoteServiceError
from central.store import CommandRecord


OWNER = 123456789012345678
OTHER_OWNER = 223456789012345678
STRATEGY_ID = "strat_dead_ahead_01"
OTHER_STRATEGY_ID = "strat_fallen_king_02"
STAMP = "2026-08-15T18:00:00.000Z"


def command(
    operation: Operation,
    status: CommandStatus,
    *,
    result: dict[str, object] | None = None,
    error_code: str | None = None,
    error_message: str | None = None,
) -> CommandRecord:
    arguments = (
        {"strategy_id": STRATEGY_ID}
        if operation in {Operation.START_STRATEGY, Operation.SWITCH_STRATEGY}
        else {}
    )
    return CommandRecord(
        command_id="11111111-1111-4111-8111-111111111111",
        device_id="22222222-2222-4222-8222-222222222222",
        owner_discord_user_id=str(OWNER),
        operation=operation,
        arguments=arguments,
        status=status,
        created_at=STAMP,
        updated_at=STAMP,
        expires_at="2026-08-15T18:00:30.000Z",
        result=result,
        error_code=error_code,
        error_message=error_message,
    )


class FakeService:
    def __init__(self, next_command: CommandRecord) -> None:
        self.next_command = next_command
        self.dispatch_calls: list[dict[str, object]] = []
        self.dispatch_error: Exception | None = None
        self.wait_error: Exception | None = None
        self.lookup_error: Exception | None = None

    async def dispatch_for_user(self, **kwargs):
        self.dispatch_calls.append(kwargs)
        if self.dispatch_error is not None:
            raise self.dispatch_error
        return self.next_command

    async def wait_for_terminal(self, **kwargs):
        if self.wait_error is not None:
            raise self.wait_error
        return self.next_command

    def get_command_for_user(self, **kwargs):
        if self.lookup_error is not None:
            raise self.lookup_error
        return self.next_command


class FakePairing:
    def __init__(self) -> None:
        self.owners: list[int] = []
        self.error: PairingError | None = None

    def issue_for_discord_user(self, owner: int) -> IssuedPairingTicket:
        self.owners.append(owner)
        if self.error is not None:
            raise self.error
        return IssuedPairingTicket(
            ticket="urpair_v1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            expires_at="2026-08-15T18:10:00.000Z",
            redemption_url="https://remote.example.test/remote/v1/pair",
        )


class FakeResponse:
    def __init__(self) -> None:
        self.defer_kwargs: dict[str, object] | None = None

    async def defer(self, **kwargs) -> None:
        self.defer_kwargs = kwargs


class FakeInteraction:
    def __init__(self, owner: int) -> None:
        self.user = SimpleNamespace(id=owner)
        self.response = FakeResponse()
        self.edits: list[dict[str, object]] = []

    async def edit_original_response(self, **kwargs) -> None:
        self.edits.append(kwargs)


class DiscordControllerTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self) -> None:
        self.pairing = FakePairing()

    async def test_authoritative_interaction_identity_and_opaque_strategy_are_forwarded(self) -> None:
        service = FakeService(command(Operation.START_STRATEGY, CommandStatus.QUEUED))
        controller = MacroCommandController(service, self.pairing)
        reply = await controller.start(OWNER, STRATEGY_ID)
        self.assertEqual(
            {
                "discord_user_id": OWNER,
                "operation": Operation.START_STRATEGY,
                "strategy_id": STRATEGY_ID,
            },
            service.dispatch_calls[0],
        )
        self.assertNotIn("device_id", service.dispatch_calls[0])
        self.assertIn("not completion", reply.content)

    async def test_raw_service_and_agent_error_messages_are_never_rendered(self) -> None:
        sentinel = "@everyone C:\\Users\\secret\\token.txt `raw detail`"
        service = FakeService(command(Operation.GET_STATUS, CommandStatus.QUEUED))
        service.dispatch_error = RemoteServiceError("DEVICE_OFFLINE", sentinel)
        controller = MacroCommandController(service, self.pairing)
        reply = await controller.status(OWNER)
        self.assertNotIn(sentinel, reply.content)
        self.assertNotIn("token.txt", reply.content)
        self.assertEqual("The linked device is offline or not connected.", reply.content)

        service.dispatch_error = None
        service.next_command = command(
            Operation.GET_STATUS,
            CommandStatus.FAILED,
            error_code="UNRECOGNIZED_AGENT_ERROR",
            error_message=sentinel,
        )
        reply = await controller.status(OWNER)
        self.assertNotIn(sentinel, reply.content)
        self.assertNotIn("UNRECOGNIZED_AGENT_ERROR", reply.content)
        self.assertEqual(
            "The Agent reported a failure. No technical details were exposed.",
            reply.content,
        )

    async def test_strategy_display_text_is_escaped_and_cache_is_owner_isolated(self) -> None:
        hostile = "@everyone **win** `now`"
        service = FakeService(
            command(
                Operation.LIST_STRATEGIES,
                CommandStatus.COMPLETED,
                result={
                    "strategies": [
                        {"strategy_id": STRATEGY_ID, "name": hostile},
                        {
                            "strategy_id": OTHER_STRATEGY_ID,
                            "name": "Normal strategy",
                        },
                    ]
                },
            )
        )
        controller = MacroCommandController(service, self.pairing)
        reply = await controller.strategies(OWNER)
        self.assertNotIn("@everyone", reply.content)
        self.assertNotIn("**win**", reply.content)
        self.assertIn("@\u200beveryone", reply.content)
        choices = controller.autocomplete(OWNER, "")
        self.assertEqual(STRATEGY_ID, choices[0].value)
        self.assertNotIn("@everyone", choices[0].name)
        self.assertEqual([], controller.autocomplete(OTHER_OWNER, ""))

    async def test_catalog_output_is_bounded(self) -> None:
        items = [
            {"strategy_id": f"strategy_{index:03d}", "name": "x" * 200}
            for index in range(200)
        ]
        service = FakeService(
            command(
                Operation.LIST_STRATEGIES,
                CommandStatus.COMPLETED,
                result={"strategies": items},
            )
        )
        reply = await MacroCommandController(service, self.pairing).strategies(OWNER)
        self.assertLessEqual(len(reply.content), 1900)
        self.assertIn("more", reply.content)

    async def test_invalid_path_like_strategy_gets_fixed_input_message(self) -> None:
        service = FakeService(command(Operation.START_STRATEGY, CommandStatus.QUEUED))
        service.dispatch_error = ProtocolError(
            "INVALID_STRATEGY_ID", "C:\\private\\path.strat"
        )
        reply = await MacroCommandController(service, self.pairing).start(
            OWNER, "C:\\private\\path.strat"
        )
        self.assertNotIn("private", reply.content)
        self.assertEqual(
            "Select a strategy from the current Remote strategy catalog.",
            reply.content,
        )

    async def test_lifecycle_copy_never_overstates_nonterminal_or_switch_completion(self) -> None:
        service = FakeService(command(Operation.STOP_SAFE, CommandStatus.EXECUTING))
        controller = MacroCommandController(service, self.pairing)
        executing = await controller.stop(OWNER)
        self.assertIn("not completion", executing.content)

        service.next_command = command(
            Operation.STOP_SAFE, CommandStatus.RECONCILING
        )
        reconciling = await controller.stop(OWNER)
        self.assertIn("outcome is unknown", reconciling.content)
        self.assertIn("Do not retry", reconciling.content)

        service.next_command = command(
            Operation.SWITCH_STRATEGY,
            CommandStatus.COMPLETED,
            result={"action_result": "switched_safe", "snapshot": {}},
        )
        switched = await controller.switch(OWNER, STRATEGY_ID)
        self.assertIn("safe boundary", switched.content)
        self.assertIn("does not assert", switched.content)

    async def test_dispatched_mutation_with_failed_lookup_is_reported_unconfirmed(self) -> None:
        service = FakeService(command(Operation.START_STRATEGY, CommandStatus.QUEUED))
        service.wait_error = RemoteServiceError(
            "COMMAND_NOT_FOUND", "hostile internal detail"
        )
        service.lookup_error = RuntimeError("C:\\private\\secret.txt")
        controller = MacroCommandController(service, self.pairing)
        with self.assertLogs("ultimate_remote.discord", level="ERROR") as captured:
            reply = await controller.start(OWNER, STRATEGY_ID)
            self.assertIn("may have been dispatched", reply.content)
            self.assertIn("Do not retry", reply.content)
            self.assertNotIn("private", reply.content)

            service.dispatch_error = RemoteServiceError(
                "DEVICE_OFFLINE",
                "hostile raw detail",
                command_id="11111111-1111-4111-8111-111111111111",
            )
            reply = await controller.start(OWNER, STRATEGY_ID)
            self.assertIn("may have been dispatched", reply.content)
            self.assertIn("Do not retry", reply.content)
        diagnostics = "\n".join(captured.output)
        self.assertNotIn("private", diagnostics)
        self.assertNotIn("hostile", diagnostics)

    async def test_pairing_is_bound_to_invoking_account_and_rate_error_is_fixed(self) -> None:
        service = FakeService(command(Operation.GET_STATUS, CommandStatus.QUEUED))
        controller = MacroCommandController(service, self.pairing)
        reply = await controller.pair(OWNER)
        self.assertEqual([OWNER], self.pairing.owners)
        self.assertIn("urpair_v1.", reply.content)

        self.pairing.error = PairingError(
            "PAIRING_RATE_LIMITED",
            "hostile raw pairing detail",
            http_status=429,
        )
        reply = await controller.pair(OWNER)
        self.assertNotIn("hostile", reply.content)
        self.assertEqual("Too many pairing attempts. Try again later.", reply.content)


class DiscordAdapterTests(unittest.IsolatedAsyncioTestCase):
    async def test_command_tree_has_five_allowlisted_controls_plus_isolated_pairing(self) -> None:
        service = FakeService(command(Operation.GET_STATUS, CommandStatus.QUEUED))
        client = RemoteDiscordClient(
            service,
            FakePairing(),
            DiscordBotOptions(strategy_cache_ttl_seconds=60),
        )
        names = {item.name for item in client.macro_group.commands}
        self.assertEqual(
            {"pair", "status", "strategies", "start", "stop", "switch"}, names
        )
        self.assertNotIn("cancel", names)
        self.assertFalse(client.allowed_mentions.everyone)
        self.assertFalse(client.allowed_mentions.users)
        self.assertFalse(client.allowed_mentions.roles)
        await client.close()

    async def test_adapter_defers_ephemerally_and_disables_mentions_on_edit(self) -> None:
        service = FakeService(
            command(
                Operation.GET_STATUS,
                CommandStatus.COMPLETED,
                result={
                    "snapshot": {
                        "macro_state": "idle",
                        "roblox_running": False,
                        "current_strategy_id": None,
                    }
                },
            )
        )
        client = RemoteDiscordClient(service, FakePairing(), DiscordBotOptions())
        interaction = FakeInteraction(OWNER)
        status_command = next(
            item for item in client.macro_group.commands if item.name == "status"
        )
        await status_command.callback(interaction)
        self.assertEqual(
            {"ephemeral": True, "thinking": True},
            interaction.response.defer_kwargs,
        )
        self.assertEqual(OWNER, service.dispatch_calls[0]["discord_user_id"])
        mentions = interaction.edits[0]["allowed_mentions"]
        self.assertIsInstance(mentions, discord.AllowedMentions)
        self.assertFalse(mentions.everyone)
        self.assertFalse(mentions.users)
        self.assertFalse(mentions.roles)
        await client.close()
