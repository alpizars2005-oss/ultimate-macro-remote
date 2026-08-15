from __future__ import annotations

import os
import subprocess
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Dict

import discord
from discord import app_commands
from dotenv import load_dotenv

BASE_DIR = Path(__file__).resolve().parent
load_dotenv(BASE_DIR / ".env")

TOKEN = os.getenv("DISCORD_TOKEN", "").strip()
ALLOWED_USER_ID = int(os.getenv("ALLOWED_USER_ID", "0") or 0)
GUILD_ID = int(os.getenv("GUILD_ID", "0") or 0)

macro_dir_env = os.getenv("MACRO_DIR", "").strip()
MACRO_DIR = Path(macro_dir_env).expanduser().resolve() if macro_dir_env else BASE_DIR
MAIN_AHK_NAME = os.getenv("MAIN_AHK", "Main_Remote.ahk").strip()

MAIN_AHK = MACRO_DIR / MAIN_AHK_NAME
STRATS_DIR = MACRO_DIR / "Resources" / "Strats"
APPDATA = Path(os.environ.get("APPDATA", str(Path.home())))
STATE_FILE = APPDATA / "Ultimate_Macro" / "state.ini"
REMOTE_COMMAND_FILE = APPDATA / "Ultimate_Macro" / "remote_command.ini"


def _decode_text(path: Path) -> str:
    if not path.exists():
        return ""
    data = path.read_bytes()
    if not data:
        return ""
    for encoding in ("utf-16", "utf-8-sig", "utf-8", "cp1252"):
        try:
            return data.decode(encoding)
        except UnicodeDecodeError:
            pass
    return data.decode("utf-8", errors="replace")


def read_ini(path: Path) -> Dict[str, Dict[str, str]]:
    result: Dict[str, Dict[str, str]] = {}
    section = ""
    for raw_line in _decode_text(path).splitlines():
        line = raw_line.strip()
        if not line or line.startswith((";", "#")):
            continue
        if line.startswith("[") and line.endswith("]"):
            section = line[1:-1].strip().lower()
            result.setdefault(section, {})
            continue
        if "=" in line and section:
            key, value = line.split("=", 1)
            result.setdefault(section, {})[key.strip().lower()] = value.strip()
    return result


def ini_value(data: Dict[str, Dict[str, str]], section: str, key: str, default: str = "") -> str:
    return data.get(section.lower(), {}).get(key.lower(), default)


def queue_command(action: str, strategy: Path | None = None) -> str:
    REMOTE_COMMAND_FILE.parent.mkdir(parents=True, exist_ok=True)
    command_id = uuid.uuid4().hex
    lines = [
        "[Command]",
        f"Id={command_id}",
        f"Action={action}",
        f"RequestedAt={datetime.now(timezone.utc).isoformat()}",
    ]
    if strategy is not None:
        lines.append(f"Strategy={strategy.resolve()}")
    payload = "\r\n".join(lines) + "\r\n"

    temp = REMOTE_COMMAND_FILE.with_suffix(".tmp")
    temp.write_text(payload, encoding="utf-16")
    os.replace(temp, REMOTE_COMMAND_FILE)
    return command_id


def list_strategies() -> list[Path]:
    if not STRATS_DIR.exists():
        return []
    return sorted(STRATS_DIR.glob("*.strat"), key=lambda p: p.name.lower())


def resolve_strategy(query: str) -> Path | None:
    q = query.strip().lower()
    strategies = list_strategies()

    for path in strategies:
        if path.name.lower() == q or path.stem.lower() == q:
            return path

    matches = [p for p in strategies if q in p.stem.lower()]
    return matches[0] if len(matches) == 1 else None


def launch_macro() -> None:
    if not MAIN_AHK.exists():
        raise FileNotFoundError(f"Main AHK not found: {MAIN_AHK}")

    candidates = [
        MACRO_DIR / "submacros" / "AutoHotkey64.exe",
        MACRO_DIR / "submacros" / "AutoHotkey.exe",
        MACRO_DIR / "submacros" / "AutoHotkey32.exe",
    ]
    for ahk in candidates:
        if ahk.exists():
            creationflags = 0
            if os.name == "nt":
                creationflags = subprocess.CREATE_NEW_PROCESS_GROUP | subprocess.DETACHED_PROCESS
            subprocess.Popen(
                [str(ahk), str(MAIN_AHK)],
                cwd=str(MACRO_DIR),
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                creationflags=creationflags,
            )
            return

    if os.name == "nt":
        os.startfile(str(MAIN_AHK))  # type: ignore[attr-defined]
        return

    raise RuntimeError("AutoHotkey executable was not found.")


def macro_snapshot() -> dict[str, str | bool]:
    state = read_ini(STATE_FILE)
    running = ini_value(state, "State", "Running", "0") == "1"

    strategy_raw = ini_value(state, "State", "Strategy", "")
    strategy = Path(strategy_raw).name if strategy_raw and strategy_raw != "0" else "None"

    pending_data = read_ini(REMOTE_COMMAND_FILE)
    pending = (
        ini_value(pending_data, "Command", "Action", "")
        if REMOTE_COMMAND_FILE.exists()
        else ""
    )

    return {
        "running": running,
        "strategy": strategy,
        "runs": ini_value(state, "State", "CurrentRunCount", "0"),
        "wins": ini_value(state, "State", "TotalTriumphs", "0"),
        "losses": ini_value(state, "State", "TotalLosses", "0"),
        "coins": ini_value(state, "State", "Coins", "0"),
        "gems": ini_value(state, "State", "Gems", "0"),
        "exp": ini_value(state, "State", "EXP", "0"),
        "pending": pending or "None",
        "last_result": ini_value(state, "Remote", "LastResult", "None"),
        "last_details": ini_value(state, "Remote", "LastDetails", ""),
    }


async def authorized(interaction: discord.Interaction) -> bool:
    if ALLOWED_USER_ID and interaction.user.id != ALLOWED_USER_ID:
        message = "⛔ You are not authorized to control this PC."
        if interaction.response.is_done():
            await interaction.followup.send(message, ephemeral=True)
        else:
            await interaction.response.send_message(message, ephemeral=True)
        return False
    return True


class RemoteClient(discord.Client):
    def __init__(self) -> None:
        super().__init__(intents=discord.Intents.none())
        self.tree = app_commands.CommandTree(self)

    async def setup_hook(self) -> None:
        if GUILD_ID:
            guild = discord.Object(id=GUILD_ID)
            self.tree.copy_global_to(guild=guild)
            await self.tree.sync(guild=guild)
            print(f"Synced commands to guild {GUILD_ID}.")
        else:
            await self.tree.sync()
            print("Synced global commands.")


client = RemoteClient()
macro = app_commands.Group(name="macro", description="Remote control for Ultimate Macro")


@macro.command(name="status", description="Show Ultimate Macro status")
async def macro_status(interaction: discord.Interaction) -> None:
    if not await authorized(interaction):
        return

    s = macro_snapshot()
    embed = discord.Embed(
        title="🎮 Ultimate Macro Remote",
        description="🟢 Running" if s["running"] else "⚫ Stopped",
    )
    embed.add_field(name="Strategy", value=f"`{s['strategy']}`", inline=False)
    embed.add_field(name="Runs", value=str(s["runs"]), inline=True)
    embed.add_field(name="Wins", value=str(s["wins"]), inline=True)
    embed.add_field(name="Losses", value=str(s["losses"]), inline=True)
    embed.add_field(name="Coins", value=str(s["coins"]), inline=True)
    embed.add_field(name="Gems", value=str(s["gems"]), inline=True)
    embed.add_field(name="EXP", value=str(s["exp"]), inline=True)
    embed.add_field(name="Pending", value=f"`{s['pending']}`", inline=True)
    embed.add_field(name="Last remote result", value=f"`{s['last_result']}`", inline=True)

    await interaction.response.send_message(embed=embed, ephemeral=True)


async def strategy_autocomplete(
    interaction: discord.Interaction, current: str
) -> list[app_commands.Choice[str]]:
    needle = current.lower().strip()
    choices: list[app_commands.Choice[str]] = []

    for path in list_strategies():
        if not needle or needle in path.stem.lower():
            choices.append(app_commands.Choice(name=path.stem[:100], value=path.name))
        if len(choices) >= 25:
            break

    return choices


@macro.command(name="start", description="Start a strategy remotely")
@app_commands.describe(strategy="Strategy file from Resources/Strats")
@app_commands.autocomplete(strategy=strategy_autocomplete)
async def macro_start(interaction: discord.Interaction, strategy: str) -> None:
    if not await authorized(interaction):
        return

    s = macro_snapshot()
    if s["running"]:
        await interaction.response.send_message(
            "⚠️ The macro is already running. Use `/macro switch` instead.",
            ephemeral=True,
        )
        return

    path = resolve_strategy(strategy)
    if path is None:
        await interaction.response.send_message(
            "❌ I could not find that strategy file.", ephemeral=True
        )
        return

    try:
        command_id = queue_command("start", path)
        launch_macro()
    except Exception as exc:
        await interaction.response.send_message(
            f"❌ Could not start the macro: `{exc}`", ephemeral=True
        )
        return

    await interaction.response.send_message(
        f"▶️ Start sent for **{path.stem}**.\nCommand: `{command_id[:8]}`",
        ephemeral=True,
    )


@macro.command(name="switch", description="Switch strategy at the next safe between-match point")
@app_commands.describe(strategy="Strategy file from Resources/Strats")
@app_commands.autocomplete(strategy=strategy_autocomplete)
async def macro_switch(interaction: discord.Interaction, strategy: str) -> None:
    if not await authorized(interaction):
        return

    s = macro_snapshot()
    if not s["running"]:
        await interaction.response.send_message(
            "⚠️ The macro is stopped. Use `/macro start` instead.", ephemeral=True
        )
        return

    path = resolve_strategy(strategy)
    if path is None:
        await interaction.response.send_message(
            "❌ I could not find that strategy file.", ephemeral=True
        )
        return

    command_id = queue_command("switch", path)
    await interaction.response.send_message(
        f"🔄 **{path.stem}** queued. It will switch **between matches**.\n"
        f"Command: `{command_id[:8]}`",
        ephemeral=True,
    )


@macro.command(name="stop", description="Stop safely before the next match")
async def macro_stop(interaction: discord.Interaction) -> None:
    if not await authorized(interaction):
        return

    s = macro_snapshot()
    if not s["running"]:
        await interaction.response.send_message(
            "⚫ The macro is already stopped.", ephemeral=True
        )
        return

    command_id = queue_command("stop")
    await interaction.response.send_message(
        "⏹️ Safe stop queued. The current match will finish, then the macro will stop.\n"
        f"Command: `{command_id[:8]}`",
        ephemeral=True,
    )


@macro.command(name="cancel", description="Cancel a queued remote command")
async def macro_cancel(interaction: discord.Interaction) -> None:
    if not await authorized(interaction):
        return

    if REMOTE_COMMAND_FILE.exists():
        try:
            REMOTE_COMMAND_FILE.unlink()
            await interaction.response.send_message(
                "✅ Pending remote command cancelled.", ephemeral=True
            )
        except OSError as exc:
            await interaction.response.send_message(
                f"❌ Could not cancel it: `{exc}`", ephemeral=True
            )
    else:
        await interaction.response.send_message(
            "ℹ️ There is no pending remote command.", ephemeral=True
        )


@macro.command(name="strategies", description="List available strategy files")
async def macro_strategies(interaction: discord.Interaction) -> None:
    if not await authorized(interaction):
        return

    strategies = list_strategies()
    if not strategies:
        await interaction.response.send_message(
            "No `.strat` files were found.", ephemeral=True
        )
        return

    names = [f"• `{p.stem}`" for p in strategies]
    text = "\n".join(names[:40])
    if len(names) > 40:
        text += f"\n…and {len(names) - 40} more."

    await interaction.response.send_message(text, ephemeral=True)


client.tree.add_command(macro)


@client.event
async def on_ready() -> None:
    print(f"Logged in as {client.user} (ID: {client.user.id if client.user else 'unknown'})")
    print(f"Macro dir: {MACRO_DIR}")
    print(f"State file: {STATE_FILE}")


if __name__ == "__main__":
    if not TOKEN:
        raise SystemExit("DISCORD_TOKEN is missing. Edit .env and add the bot token.")
    if not ALLOWED_USER_ID:
        raise SystemExit("ALLOWED_USER_ID is missing. Add your Discord user ID to .env.")
    client.run(TOKEN, log_handler=None)
