#Requires AutoHotkey v2.0
#SingleInstance Force

if (A_LineFile = A_ScriptFullPath) {
    ExitApp()
}

CheckForUpdate(currentVer) {
    ; Development clones are never rewritten by the runtime updater. This keeps
    ; feature branches and local review work safe; packaged/extracted copies are
    ; updated automatically instead.
    if DirExist(A_ScriptDir "\.git") {
        return 0
    }

    markerDir := A_AppData "\Ultimate_Macro"
    markerPath := markerDir "\update_commit.txt"

    try {
        whr := ComObject("WinHttp.WinHttpRequest.5.1")
        whr.SetTimeouts(3000, 3000, 5000, 5000)
        whr.Open("GET", "https://api.github.com/repos/alpizars2005-oss/ultimate-macro-remote/commits/main", false)
        whr.SetRequestHeader("User-Agent", "Ultimate-Macro-Remote-Updater")
        whr.Send()

        if (whr.Status != 200) {
            return 0
        }

        json := whr.ResponseText
        if !RegExMatch(json, '"sha"\s*:\s*"([0-9a-fA-F]{40})"', &match) {
            return 0
        }
        latestSha := StrLower(match[1])

        localSha := ""
        if FileExist(markerPath) {
            try localSha := StrLower(Trim(FileRead(markerPath, "UTF-8")))
        }
        if (localSha = latestSha) {
            return 0
        }

        updateScript := A_ScriptDir "\submacros\update.ps1"
        if !FileExist(updateScript) {
            return 0
        }

        if !DirExist(markerDir) {
            DirCreate(markerDir)
        }

        tempScript := A_Temp "\UltimateMacro_update_" A_TickCount ".ps1"
        FileCopy(updateScript, tempScript, 1)

        downloadURL := "https://github.com/alpizars2005-oss/ultimate-macro-remote/archive/" latestSha ".zip"
        entryPoint := (A_ScriptName = "Main_Remote.ahk") ? "Main_Remote.ahk" : "Main.ahk"

        command := 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' tempScript '"'
        command .= ' -DownloadUrl "' downloadURL '"'
        command .= ' -MacroDir "' A_ScriptDir '"'
        command .= ' -CommitSha "' latestSha '"'
        command .= ' -MarkerPath "' markerPath '"'
        command .= ' -EntryPoint "' entryPoint '"'

        Run(command, A_Temp, "Hide")
        ExitApp()
    } catch {
        ; Update checks must never prevent the macro from opening. The updater's
        ; PowerShell stage writes a local log if activation/rollback itself fails.
        return 0
    }

    return 0
}
