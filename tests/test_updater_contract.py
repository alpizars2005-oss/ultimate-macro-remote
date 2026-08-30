from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
AHK = (ROOT / "submacros" / "updater.ahk").read_text(encoding="utf-8")
PS1 = (ROOT / "submacros" / "update.ps1").read_text(encoding="utf-8")
BAT = (ROOT / "submacros" / "update.bat").read_text(encoding="utf-8")
MAIN = (ROOT / "Main.ahk").read_text(encoding="utf-8-sig")
REMOTE = (ROOT / "Main_Remote.ahk").read_text(encoding="utf-8-sig")


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def main() -> None:
    # Both user-facing entry points must keep Darksen's original attribution and
    # include the same updater.
    for name, source in (("Main.ahk", MAIN), ("Main_Remote.ahk", REMOTE)):
        require("Ultimate Macro (macro for TDS) by Darksen" in source, f"{name}: Darksen attribution missing")
        require('submacros\\updater.ahk' in source, f"{name}: updater include missing")
        require("CheckForUpdate(ver)" in source, f"{name}: startup update check missing")

    # Runtime discovery is scoped to this maintained repository and an exact
    # commit, not the previous upstream release feed or an arbitrary URL.
    require(
        "api.github.com/repos/alpizars2005-oss/ultimate-macro-remote/commits/main" in AHK,
        "updater must resolve the maintained repo main commit",
    )
    require("DarksenDev/tds-macro/releases/latest" not in AHK, "legacy upstream release feed must not return")
    require("archive/\" latestSha \".zip" in AHK, "archive URL must be commit-specific")
    require("DirExist(A_ScriptDir \"\\.git\")" in AHK, "development clones must be protected")
    require("SetTimeouts(" in AHK, "network check must have finite timeouts")

    # PowerShell accepts only the exact repository's immutable SHA archive and a
    # fixed entry point. It stages and validates before the live directory moves.
    require("ultimate-macro-remote/archive/[0-9a-f]{40}" in PS1, "archive URL allowlist missing")
    require("ValidatePattern('^[0-9a-f]{40}$')" in PS1, "commit SHA validation missing")
    require("ValidateSet('Main.ahk', 'Main_Remote.ahk')" in PS1, "entry-point allowlist missing")

    required_stage_checks = ["'Main.ahk'", "'Main_Remote.ahk'", "'lib'", "'submacros'", "'Resources'"]
    for token in required_stage_checks:
        require(token in PS1, f"staging validation missing {token}")

    stage_validation = PS1.index("foreach ($required in")
    backup_move = PS1.index("Move-Item -LiteralPath $macroPath -Destination $backupPath")
    activate_move = PS1.index("Move-Item -LiteralPath $stageRoot -Destination $macroPath")
    marker_write = PS1.index("Set-Content -LiteralPath $marker")
    require(stage_validation < backup_move < activate_move < marker_write, "stage/swap/marker order is unsafe")

    # User-local data survives updates, and rollback remains available until the
    # new tree and marker are active.
    require("'.env'" in PS1 and "'remote_service.url'" in PS1, "local remote config preservation missing")
    require("-Filter '*.strat'" in PS1, "custom strategy preservation missing")
    require("Rollback restored the previous installation" in PS1, "rollback path missing")
    require("Move-Item -LiteralPath $backupPath -Destination $macroPath" in PS1, "rollback restore missing")

    # The legacy batch file must remain only a compatibility wrapper: never wipe
    # the live macro directory itself.
    lowered_bat = BAT.casefold()
    require("del /f /s /q" not in lowered_bat, "legacy destructive DEL returned")
    require("rd /s /q" not in lowered_bat, "legacy destructive RD returned")
    require("update.ps1" in lowered_bat, "batch wrapper must delegate to safe updater")

    print("zero-touch updater safety contracts: OK")


if __name__ == "__main__":
    main()
