#Requires AutoHotkey v2.0
#SingleInstance Force
#NoTrayIcon

ListLines(False)
KeyHistory(0)

CoordMode("Mouse", "Client")
CoordMode("Pixel", "Client")

SetWorkingDir(A_ScriptDir "\..\")

#Include  "%A_LineFile%\..\..\lib\ImageSearch\ImageSearch.ahk"
#Include "%A_LineFile%\..\..\lib\Gdip_All.ahk"
#Include "%A_LineFile%\..\..\lib\OCR.ahk"
#Include "%A_LineFile%\..\..\lib\Roblox.ahk"

Opt := A_AppData "\Ultimate_Macro\Options"
SettingsFile := Opt "\Settings.tds"
StateFile := A_AppData "\Ultimate_Macro\state.ini"

global WebhookLink := IniRead(SettingsFile, "Webhook", "Link", "")
tempWebhook := IniRead(SettingsFile, "Webhook", "Enabled", "OFF")
WebhookEnabled := (tempWebhook = "ON" || tempWebhook = "1") ? true : false
SendCurrenciesEnabled := IniRead(SettingsFile, "Webhook", "SendCurrencies", "1")
global WebhookScreenshots := IniRead(SettingsFile, "Webhook", "WebhookScreenshots", "1")
global WebhookTriumphScreenshots := IniRead(SettingsFile, "Webhook", "WebhookTriumphScreenshots", 1)
global WebhookSepatateTriumphScreenshots := IniRead(SettingsFile, "Webhook", "WebhookSepatateTriumphScreenshots", 0)
global WebhookLink2 := IniRead(SettingsFile, "Webhook", "Link2", "")

global ResourcesDir := A_WorkingDir "\Resources"
global TriumphImg1 := ResourcesDir "\triumph.png"
global TriumphImg2 := ResourcesDir "\PlayAgain.png"
global YouLostImg := ResourcesDir "\YouLost.png"
global ReviveIMG := ResourcesDir "\use_revive_ticket.png"
global RestartImg := ResourcesDir "\Restart.png"
global RestartImg2 := ResourcesDir "\Restart2.png"
global cancel := ResourcesDir "\cancel.png"

global WatchdogLogDir := A_AppData "\Ultimate_Macro\logs"
global WatchdogLogFile := WatchdogLogDir "\watchdog.log"
global MainPID := 0
global MainScriptPath := ""
global MainScriptDir := ""
global ParentClosedByWatchdog := false

pToken := Gdip_Startup()

OnExit(CleanupGdip)

if (A_Args.Length != 2) {
    WriteWatchdogLog("validation_failed", "expected_parent_pid_and_script_path")
    ExitApp(2)
}

rawMainPID := Trim(A_Args[1])
rawMainScriptPath := Trim(A_Args[2])

try {
    parentIdentity := ValidateWatchdogParent(rawMainPID, rawMainScriptPath)
    MainPID := parentIdentity.pid
    MainScriptPath := parentIdentity.scriptPath
    MainScriptDir := parentIdentity.scriptDir
} catch Error as err {
    WriteWatchdogLog("validation_failed", err.Message, rawMainPID)
    ExitApp(2)
}

WriteWatchdogLog("watchdog_started", "identity_validated", MainPID, MainScriptPath)

if (WebhookEnabled && WebhookLink != "" && WebhookScreenshots = "1") {
    screenshotDelay := Random(25000, 300000)
    SetTimer(TakeRandomScreenshot, screenshotDelay)
}

Sleep(15000)
WinWait("ahk_exe RobloxPlayerBeta.exe", , 30)

loopCounter := 0

Loop {
    getRobloxPos(,,&w,&h)

    w := Max(1, w)
    h := Max(1, h)

    loopCounter++ 
     
    if (!ParentProcessIsAlive()) {
        WriteWatchdogLog("parent_missing", "parent_process_exited", MainPID, MainScriptPath)
        ExitApp()
    }

    if WinExist("Roblox Crash") {
        if (WebhookEnabled && WebhookLink != "") {
            SendScreenshot(,"Roblox has crashed!")
        }
        RestartMain("roblox_crash")
        return
    }

    if !WinExist("ahk_exe RobloxPlayerBeta.exe") {
        if (WebhookEnabled && WebhookLink != "") {
            SendScreenshot(,"Roblox is not running!")
        }
        RestartMain("roblox_missing")
        return
    }

    if (Mod(loopCounter, 3) == 0) {
        CoordMode("Pixel", "Screen")
        
        sw := A_ScreenWidth
        sh := A_ScreenHeight
        
        try {
            if ImageSearch(&FoundX, &FoundY, 0, 0, sw, sh, "*26 Resources/Disconnected.png") {
                CoordMode("Pixel", "Client")
                if (WebhookEnabled && WebhookLink != "") {
                    SendScreenshot(, "Disconnected, rejoining")
                }
                RestartMain("roblox_disconnect_primary")
                ExitApp()
            } else if ImageSearch(&FoundX, &FoundY, 0, 0, sw, sh, "*26 Resources/disconnected2.png") {
                CoordMode("Pixel", "Client")
                if (WebhookEnabled && WebhookLink != "") {
                    SendScreenshot(, "Disconnected, rejoining")
                }
                RestartMain("roblox_disconnect_secondary")
                ExitApp()
            }
        } catch Error as err {
            CoordMode("Pixel", "Client")
        }
        
        CoordMode("Pixel", "Client")
    }


    if (Mod(loopCounter, 2) == 0) {
        resTriumph1 := AdvImageSearch(TriumphImg1, Integer(w * 0.2), Integer(h * 0.2), Integer(w * 0.6), Integer(h * 0.7), 0.5, 1.5)
        
        if (resTriumph1.status == "success" && resTriumph1.score > 0.7) {
            if (WebhookEnabled && WebhookLink != "") {
                CloseMain("match_triumph")
                Sleep 1300
                SendInfo("Triumph")
            }
            RestartMain("match_triumph")
            ExitApp()
        }
    } else {
        resTriumph2 := AdvImageSearch(TriumphImg2, Integer(w * 0.2), Integer(h * 0.2), Integer(w * 0.6), Integer(h * 0.7), 0.5, 1.5)
        Sleep 200
        resLost := AdvImageSearch(YouLostImg, Integer(w * 0.2), Integer(h * 0.2), Integer(w * 0.6), Integer(h * 0.7), 0.5, 1.5)

        if (resTriumph2.status == "success" && resTriumph2.score > 0.7) {
            if (WebhookEnabled && WebhookLink != "") {
                CloseMain("match_triumph")
                Sleep 1300
                SendInfo("Triumph")
            }
            RestartMain("match_triumph")
            ExitApp()
        }
        
        if (resLost.status == "success" && resLost.score > 0.7) {
            if (WebhookEnabled && WebhookLink != "") {
                CloseMain("match_loss")
                Sleep 1300
                SendInfo("Loss")
            }
            RestartMain("match_loss")
            ExitApp()
        }
    }

    if (Mod(loopCounter, 6) == 0) {
        resRevive := AdvImageSearch(ReviveIMG, Integer(w * 0.2), Integer(h * 0.2), Integer(w * 0.6), Integer(h * 0.7))

        if (resRevive.status == "success" && resRevive.score > 0.7) {
            resCancel := AdvImageSearch(cancel, Integer(w * 0.2), Integer(h * 0.2), Integer(w * 0.6), Integer(h * 0.7))
            
            if (resCancel.status == "success" && resCancel.score > 0.7) {
                WinActivate("ahk_exe RobloxPlayerBeta.exe")
                WinWaitActive("ahk_exe RobloxPlayerBeta.exe", , 1)
                Click(resCancel.x, resCancel.y)
            }
        }
    }
    Sleep(300)
}

sX(baseX, Width := 1920) {
    getRobloxPos(&pX, &pY, &currentWidth, &currentHeight)
    return Round(baseX * (currentWidth / Width))
}

sY(baseY, Height := 1009) {
    getRobloxPos(&pX, &pY, &currentWidth, &currentHeight)
    return Round(baseY * (currentHeight / Height))
}

SendInfo(matchResult := "") {
    global WebhookLink, StateFile, SendCurrenciesEnabled, WebhookEnabled, WebhookSepatateTriumphScreenshots, WebhookLink2

    if (!WebhookEnabled || WebhookLink = "") {
        return
    }

    if (WebhookSepatateTriumphScreenshots = 1) {
        WebhookLink := WebhookLink2
    }

    mapName := "Unknown"
    timeInSeconds := 0
    coinVal := 0
    gemVal := 0
    expVal := 0

    timeCompleted_T := IniRead(StateFile, "State", "TimeWhenStartedPlaying", "Failed")

    if (timeCompleted_T != "Failed") {
        ms := A_TickCount - timeCompleted_T

        total_seconds := ms // 1000
        timeInSeconds := total_seconds
        hours := total_seconds // 3600
        minutes := (total_seconds // 60) - (hours * 60)
        seconds := Mod(total_seconds, 60)
        
        timeCompleted := ""
        if (hours > 0)
            timeCompleted .= hours "h "
        if (minutes > 0 || hours > 0)
            timeCompleted .= minutes "m "
        timeCompleted .= seconds "s"
    } else {
        timeCompleted := "Failed"
        return
    }

    IniDelete(StateFile, "State", "TimeWhenStartedPlaying")

    getRobloxPos(&pX, &pY, &w, &h)

    MouseMove(Round(w*0.5), Round(h*0.1))

    Play_Again := AdvImageSearch(TriumphImg2, Integer(w * 0.2), Integer(h * 0.2), Integer(w * 0.6), Integer(h * 0.7))
    Restart := AdvImageSearch(RestartImg, Integer(w * 0.2), Integer(h * 0.2), Integer(w * 0.6), Integer(h * 0.7))
    Restart2 := AdvImageSearch(RestartImg2, Integer(w * 0.2), Integer(h * 0.2), Integer(w * 0.6), Integer(h * 0.7))

    FoundX := 0
    FoundY := 0

    if (Play_Again.status = "success" && Play_Again.score > 0.7) {
        FoundX := Play_Again.x
        FoundY := Play_Again.y
    } else if (Restart.status = "success" && Restart.score > 0.7) {
        FoundX := Restart.x
        FoundY := Restart.y
    } else if (Restart2.status = "success" && Restart2.score > 0.7) {
        FoundX := Restart2.x
        FoundY := Restart2.y
    } else {
        pBitmap := Gdip_BitmapFromScreen()
        if (pBitmap) {
            headerTitle := (matchResult = "Triumph") ? "### :trophy: TRIUMPH!" : "### :skull: YOU LOST!"
            color := (matchResult = "Triumph") ? 3066993 : 12434877
            SendScreenshot(pBitmap, headerTitle, color)
            Gdip_DisposeImage(pBitmap)
        }
    }

    if (SendCurrenciesEnabled = "1") {
        targetX := FoundX - sX(180)
        targetY := FoundY - sY(250)
        AreaW := sX(340)
        AreaH := sY(230)

        ocrTarget := ""
        pBitmapArea := Gdip_BitmapFromScreen(targetX . "|" . targetY . "|" . AreaW . "|" . AreaH)
        if (pBitmapArea) {
            pBitmapResized := Gdip_CreateBitmap(AreaW * 3, AreaH * 3)
            if (pBitmapResized) {
                G1 := Gdip_GraphicsFromImage(pBitmapResized)
                if (G1) {
                    DllCall("gdiplus\GdipSetInterpolationMode", "Ptr", G1, "Int", 7)
                    Gdip_DrawImage(G1, pBitmapArea, 0, 0, AreaW * 3, AreaH * 3, 0, 0, AreaW, AreaH)
                    BinarizeTargetBitmap(pBitmapResized)
                    try ocrTarget := OCR.FromBitmap(pBitmapResized, {lang:"en-US"}).Text
                    Gdip_DeleteGraphics(G1)
                }
                Gdip_DisposeImage(pBitmapResized)
            }
            Gdip_DisposeImage(pBitmapArea)
        }

        infoX := FoundX + sX(200)
        infoY := FoundY - sY(350) 
        InfoW := sX(320)
        InfoH := sY(240)

        ocrInfo := ""
        pBitmapInfo := Gdip_BitmapFromScreen(infoX . "|" . infoY . "|" . InfoW . "|" . InfoH)
        if (pBitmapInfo) {
            pBitmapInfoResized := Gdip_CreateBitmap(InfoW * 3, InfoH * 3)
            if (pBitmapInfoResized) {
                G2 := Gdip_GraphicsFromImage(pBitmapInfoResized)
                if (G2) {
                    DllCall("gdiplus\GdipSetInterpolationMode", "Ptr", G2, "Int", 7)
                    Gdip_DrawImage(G2, pBitmapInfo, 0, 0, InfoW * 3, InfoH * 3, 0, 0, InfoW, InfoH)
                    try ocrInfo := OCR.FromBitmap(pBitmapInfoResized, {lang:"en-US", scale:1.5}).Text
                    Gdip_DeleteGraphics(G2)
                }
                Gdip_DisposeImage(pBitmapInfoResized)
            }
        }

        xpX := FoundX - sX(180)
        xpY := FoundY - sY(250)
        xpW := sX(340)
        xpH := sY(230)

        if RegExMatch(ocrTarget, "i)(\d[\d,]*)\s*c[o0]ins?", &coinsMatch)
            coinVal := Integer(StrReplace(coinsMatch[1], ",", ""))
        
        if RegExMatch(ocrTarget, "i)(\d[\d,]*)\s*(?:[g6c]\s*[e30c]ms?|[c]\s*[c]\s*ms?)", &gemsMatch)
            gemVal := Integer(StrReplace(gemsMatch[1], ",", ""))

        if RegExMatch(ocrTarget, "i)(?<![\+\d])(\d[\d,]*)\s*xp", &expMatch)
            expVal := Integer(StrReplace(expMatch[1], ",", ""))

        mapName := "Unknown"

        mapList := [
        "Abandoned City", "Area 52", "Autumn Falling", 
        "Badlands II", "Black Spot Exchange", "Candy Valley", "Cataclysm", "Chess Board", 
        "Construction Crazy", "Coral Deep", "Crossroads", "Crystal Cave", 
        "Cyber City", "Dead Ahead", "Derelict Outpost", "Deserted Village", "Dusty Bridges", 
        "Enchanted Forest", "Farm Lands", "Forest Camp", "Forgetten Docks", "Four Seasons", 
        "Fungi Island", "Grass Isle", "Simplicity", "Happy Home of Robloxia", "Harbor", "Honey Valley", 
        "Hot Spot", "Iceville", "Infernal Abyss", "Lay By", "Lighthaos", "Marshlands", "Mason Arch", "Medieval Times", "Meltdown", 
        "Midnight Issue", "Moon Base", "Musaceae Kingdom", "Necropolis", "Nether", "Night Station", 
        "Northern Lights", "Outskirts Commune", "Pier Pressure", "Pizza Party", "Polluted Wasteland II", 
        "Portland", "Retro Crossroads", "Retro Lighthouse", "Retro Rocket Arena", "Retro Stained Temple", 
        "Retro The Heights", "Retro Zone", "Rocket Arena", "Ruby Escort", "Sacred Mountains", 
        "Sky Islands", "Space City", "Spring Fever", "Stained Temple", "Sugar Rush", 
        "The Heavens", "The Heights", "Toyboard", "Tropical Industries", "Tropical Isles", "U-Turn", 
        "Unknown Garden", "Winter Abyss", "Winter Bridges", "Winter Stronghold", "Wrecked Battlefield", 
        "Wrecked Battlefield II", "Wretched Front"
    ]

        for currentMap in mapList {
            if InStr(ocrInfo, currentMap) {
                mapName := currentMap
                break
            }
        }

    }

    totalTriumphs := IniRead(StateFile, "State", "TotalTriumphs", 0)
    totalLosses := IniRead(StateFile, "State", "TotalLosses", 0)
    
    if (matchResult = "Triumph") {
        totalTriumphs += 1
        IniWrite(totalTriumphs, StateFile, "State", "TotalTriumphs")
    } else if (matchResult = "Loss") {
        totalLosses += 1
        IniWrite(totalLosses, StateFile, "State", "TotalLosses")
    }
    
    savedCoins := IniRead(StateFile, "State", "Coins", 0)
    savedGems := IniRead(StateFile, "State", "Gems", 0)
    savedExp := IniRead(StateFile, "State", "EXP", 0)
    savedTime := IniRead(StateFile, "State", "TotalTimeSeconds", 0)
    
    totalCoins := savedCoins + coinVal
    totalGems := savedGems + gemVal
    totalExp := savedExp + expVal
    totalTime := savedTime + timeInSeconds
    
    IniWrite(totalCoins, StateFile, "State", "Coins")
    IniWrite(totalGems, StateFile, "State", "Gems")
    IniWrite(totalExp, StateFile, "State", "EXP")
    IniWrite(totalTime, StateFile, "State", "TotalTimeSeconds")

    autorunStart := IniRead(StateFile, "State", "StartTime", 0)
    coinsPerHour := 0, gemsPerHour := 0, expPerHour := 0
    if (autorunStart > 0) {
        elapsedMs := A_TickCount - autorunStart
        elapsedHours := elapsedMs / 3600000
        if (elapsedHours > 0.001) {
            coinsPerHour := Round(totalCoins / elapsedHours)
            gemsPerHour := Round(totalGems / elapsedHours)
            expPerHour := Round(totalExp / elapsedHours)
        }
    }
    
    totalMatches := totalTriumphs + totalLosses
    winrate := (totalMatches > 0) ? Round((totalTriumphs / totalMatches) * 100) : 0
    wlRatio := (totalLosses > 0) ? Round(totalTriumphs / totalLosses, 1) : totalTriumphs
    wlRatioStr := StrReplace(String(wlRatio), ".", ".")

    avgTimeStr := "0s"
    if (totalMatches > 0 && totalTime > 0) {
        avgSeconds := Round(totalTime / totalMatches)
        avgMinutes := Floor(avgSeconds / 60)
        avgRemSeconds := Mod(avgSeconds, 60)
        avgTimeStr := (avgMinutes > 0) ? avgMinutes "m " avgRemSeconds "s" : avgRemSeconds "s"
    }

    description := ""
    color := 12434877

    if (matchResult = "Triumph") {
        description := "### :trophy: TRIUMPH!"
        color := 3066993
    } else {
        description := "### :skull: YOU LOST!"
        color := 0xFF322E
    }

    if (SendCurrenciesEnabled = "1") {
        description .= "`n"
        description .= "Map: **" mapName "**  Time Completed: **" timeCompleted "**`n"
        description .= "+" expVal " EXP (+" totalExp ")`n"
        description .= "+" coinVal " Coins (+" totalCoins ")  +" gemVal " Gems (+" totalGems ")`n"
        description .= "-# Total Matches: " totalMatches ", wins: " totalTriumphs ", losses: " totalLosses ", W/R: " winrate "%, W/L ratio: " wlRatioStr ", " coinsPerHour " coins/h, " gemsPerHour " gems/h, " expPerHour " exp/h, avg. time: " avgTimeStr
    }

    pBitmap := Gdip_BitmapFromScreen()
    if (pBitmap) {
        SendScreenshot(pBitmap, description, color, WebhookTriumphScreenshots)
        If IsSet(pBitmapInfo){
            Gdip_DisposeImage(pBitmapInfo)
        }
        Gdip_DisposeImage(pBitmap)
    }
    if (WebhookLink = WebhookLink2) {
        WebhookLink := IniRead(SettingsFile, "Webhook", "Link", "")
    }
}

BinarizeTargetBitmap(pBitmap) {
    Gdip_GetImageDimensions(pBitmap, &w, &h)
    Rect := Buffer(16, 0)
    NumPut("int", 0, Rect, 0), NumPut("int", 0, Rect, 4)
    NumPut("int", w, Rect, 8), NumPut("int", h, Rect, 12)
    
    BitmapData := Buffer(A_PtrSize = 8 ? 32 : 24, 0)
    if DllCall("gdiplus\GdipBitmapLockBits", "Ptr", pBitmap, "Ptr", Rect, "UInt", 3, "Int", 0x26200A, "Ptr", BitmapData)
        return
        
    Scan0 := NumGet(BitmapData, A_PtrSize = 8 ? 16 : 12, "Ptr")
    Stride := NumGet(BitmapData, 8, "Int")
    
    Loop h {
        y := A_Index - 1
        Loop w {
            x := A_Index - 1
            offset := (y * Stride) + (x * 4)
            b := NumGet(Scan0 + offset, 0, "UChar")
            g := NumGet(Scan0 + offset, 1, "UChar")
            r := NumGet(Scan0 + offset, 2, "UChar")
            
            brightness := (r + g + b) / 3
            if (brightness > 200) { 
                NumPut("UChar", 255, Scan0 + offset, 0)
                NumPut("UChar", 255, Scan0 + offset, 1)
                NumPut("UChar", 255, Scan0 + offset, 2)
            } else {
                NumPut("UChar", 0, Scan0 + offset, 0)
                NumPut("UChar", 0, Scan0 + offset, 1)
                NumPut("UChar", 0, Scan0 + offset, 2)
            }
        }
    }
    DllCall("gdiplus\GdipBitmapUnlockBits", "Ptr", pBitmap, "Ptr", BitmapData)
}


TakeRandomScreenshot() {
    global WebhookEnabled, WebhookLink
    if (!WebhookEnabled || WebhookLink = "")
        return
    
    if (WebhookLink = WebhookLink2)
        WebhookLink := IniRead(SettingsFile, "Webhook", "Link", "")
    
    pBitmap := Gdip_BitmapFromScreen()
    if (pBitmap > 0) {
        SendScreenshot(pBitmap, "Automatic screenshot", 3447003)
        Gdip_DisposeImage(pBitmap)
    }
    
    screenshotDelay := Random(180000, 360000)
    SetTimer(TakeRandomScreenshot, screenshotDelay)
}

SendScreenshot(pBitmap := Gdip_BitmapFromScreen(), description := "", color := 12434877, screenshot := WebhookScreenshots) {
    global WebhookLink

    escapedDescription := StrReplace(description, "\", "\\")
    escapedDescription := StrReplace(escapedDescription, '"', '\"')
    escapedDescription := StrReplace(escapedDescription, "`n", "\n")

    fields := []

    if (screenshot == "0" || screenshot == 0) {
        payload_json := '{"embeds": [{"description": "' escapedDescription '", "color": ' color '}]}'
        fields.Push(Map("name", "payload_json", "content-type", "application/json", "content", payload_json))
    } 
    else {
        payload_json := '{"embeds": [{"description": "' escapedDescription '", "color": ' color ', "image": {"url": "attachment://screenshot.png"}}]}'
        fields.Push(Map("name", "payload_json", "content-type", "application/json", "content", payload_json))
        fields.Push(Map("name", "files[0]", "filename", "screenshot.png", "content-type", "image/png", "pBitmap", pBitmap))
    }
    
    CreateFormData(&postdata, &contentType, fields)

    if (screenshot != "0" && screenshot != 0) {
        try Gdip_DisposeImage(pBitmap)
    }

    try {
        whr := ComObject("WinHttp.WinHttpRequest.5.1")
        whr.Open("POST", WebhookLink "?wait=true", false)
        whr.SetRequestHeader("Content-Type", contentType)
        whr.SetTimeouts(5000, 5000, 60000, 60000) 
        whr.Send(postdata)
    }
}

CreateFormData(&retData, &contentType, fields) {
    charArray := StrSplit("0123456789abcdefghijklmnopqrstuvwxyz")
    boundary := ""
    Loop 12 {
        boundary .= charArray[Random(1, charArray.Length)]
    }
    
    hData := DllCall("GlobalAlloc", "UInt", 0x2, "UPtr", 0, "Ptr")
    DllCall("ole32\CreateStreamOnHGlobal", "Ptr", hData, "Int", 0, "PtrP", &pStream := 0, "UInt")
    
    for index, field in fields {
        str := "`r`n------------------------------" boundary "`r`n"
        str .= 'Content-Disposition: form-data; name="' field["name"] '"'
        
        if field.Has("filename")
            str .= '; filename="' field["filename"] '"'
        
        str .= "`r`n"
        str .= "Content-Type: " field["content-type"] "`r`n`r`n"
        
        if field.Has("content")
            str .= field["content"] "`r`n"
        
        length := StrPut(str, "UTF-8") - 1
        utf8 := Buffer(length)
        StrPut(str, utf8, "UTF-8")
        DllCall("shlwapi\IStream_Write", "Ptr", pStream, "Ptr", utf8.Ptr, "UInt", length, "UInt")
        
        if field.Has("pBitmap") {
            try {
                pFileStream := Gdip_SaveBitmapToStream(field["pBitmap"])
                DllCall("shlwapi\IStream_Size", "Ptr", pFileStream, "UInt64P", &size := 0, "UInt")
                DllCall("shlwapi\IStream_Reset", "Ptr", pFileStream, "UInt")
                DllCall("shlwapi\IStream_Copy", "Ptr", pFileStream, "Ptr", pStream, "UInt", size, "UInt")
                DllCall("ole32\IUnknown_Release", "Ptr", pFileStream)
            }
        }
    }
    
    str := "`r`n------------------------------" boundary "--`r`n"
    length := StrPut(str, "UTF-8") - 1
    utf8 := Buffer(length)
    StrPut(str, utf8, "UTF-8")
    DllCall("shlwapi\IStream_Write", "Ptr", pStream, "Ptr", utf8.Ptr, "UInt", length, "UInt")
    
    pStream := ""
    
    pData := DllCall("GlobalLock", "Ptr", hData, "Ptr")
    size := DllCall("GlobalSize", "Ptr", pData, "UPtr")
    
    retData := ComObjArray(0x11, size)  
    pvData := NumGet(ComObjValue(retData), 8 + A_PtrSize, "Ptr")
    DllCall("RtlMoveMemory", "Ptr", pvData, "Ptr", pData, "Ptr", size)
    
    DllCall("GlobalUnlock", "Ptr", hData)
    DllCall("GlobalFree", "Ptr", hData, "Ptr")
    
    contentType := "multipart/form-data; boundary=----------------------------" boundary
}

CanonicalWatchdogPath(path) {
    path := Trim(path)
    if (path = "" || InStr(path, '"'))
        return ""

    requiredChars := DllCall("Kernel32\GetFullPathNameW", "Str", path, "UInt", 0, "Ptr", 0, "Ptr", 0, "UInt")
    if (!requiredChars)
        return ""

    pathBuffer := Buffer(requiredChars * 2, 0)
    writtenChars := DllCall("Kernel32\GetFullPathNameW", "Str", path, "UInt", requiredChars, "Ptr", pathBuffer.Ptr, "Ptr", 0, "UInt")
    if (!writtenChars || writtenChars >= requiredChars)
        return ""

    return StrGet(pathBuffer.Ptr, writtenChars, "UTF-16")
}

WatchdogPathsEqual(leftPath, rightPath) {
    return (leftPath != "" && rightPath != "" && StrLower(leftPath) = StrLower(rightPath))
}

WindowsCommandLineArgs(commandLine) {
    args := []
    argCount := 0
    argv := DllCall("Shell32\CommandLineToArgvW", "Str", commandLine, "IntP", &argCount, "Ptr")
    if (!argv)
        return args

    try {
        Loop argCount {
            argPtr := NumGet(argv, (A_Index - 1) * A_PtrSize, "Ptr")
            args.Push(StrGet(argPtr, "UTF-16"))
        }
    } finally {
        DllCall("Kernel32\LocalFree", "Ptr", argv, "Ptr")
    }

    return args
}

ProcessCommandLineHasScript(processID, expectedScriptPath) {
    try {
        wmi := ComObjGet("winmgmts:")
        query := "SELECT ProcessId, CommandLine FROM Win32_Process WHERE ProcessId = " processID
        for process in wmi.ExecQuery(query) {
            commandLine := String(process.CommandLine)
            if (commandLine = "")
                return false

            for argument in WindowsCommandLineArgs(commandLine) {
                candidatePath := CanonicalWatchdogPath(argument)
                if (WatchdogPathsEqual(candidatePath, expectedScriptPath))
                    return true
            }
            return false
        }
    } catch Error {
        return false
    }

    return false
}

ValidateWatchdogParent(rawPID, rawScriptPath) {
    if (!RegExMatch(rawPID, "^[1-9]\d{0,9}$"))
        throw Error("invalid_parent_pid")

    parentPID := Integer(rawPID)
    if (parentPID > 0xFFFFFFFF)
        throw Error("parent_pid_out_of_range")
    if (ProcessExist(parentPID) != parentPID)
        throw Error("parent_process_not_running")

    parentScriptPath := CanonicalWatchdogPath(rawScriptPath)
    if (parentScriptPath = "")
        throw Error("invalid_parent_script_path")

    attributes := FileExist(parentScriptPath)
    if (!attributes || InStr(attributes, "D"))
        throw Error("parent_script_not_found")

    SplitPath(parentScriptPath, , &parentScriptDir, &parentExtension)
    if (StrLower(parentExtension) != "ahk")
        throw Error("parent_script_must_be_ahk")

    parentScriptDir := CanonicalWatchdogPath(parentScriptDir)
    macroRoot := CanonicalWatchdogPath(A_WorkingDir)
    if (!WatchdogPathsEqual(parentScriptDir, macroRoot))
        throw Error("parent_script_outside_macro_root")
    if (!ProcessCommandLineHasScript(parentPID, parentScriptPath))
        throw Error("parent_pid_script_mismatch")

    return {pid: parentPID, scriptPath: parentScriptPath, scriptDir: parentScriptDir}
}

WatchdogLogValue(value) {
    value := String(value)
    value := StrReplace(value, "`r", " ")
    value := StrReplace(value, "`n", " ")
    return StrReplace(value, "`t", " ")
}

WriteWatchdogLog(eventName, reason := "", parentPID := "", parentScript := "", relaunchedPID := "") {
    global WatchdogLogDir, WatchdogLogFile

    try {
        if (!DirExist(WatchdogLogDir))
            DirCreate(WatchdogLogDir)

        line := "timestamp=" A_NowUTC
        line .= "`tevent=" WatchdogLogValue(eventName)
        line .= "`treason=" WatchdogLogValue(reason)
        line .= "`tparent_pid=" WatchdogLogValue(parentPID)
        line .= "`tparent_script=" WatchdogLogValue(parentScript)
        line .= "`trelaunched_pid=" WatchdogLogValue(relaunchedPID)
        FileAppend(line "`n", WatchdogLogFile, "UTF-8")
    } catch Error {
    }
}

ParentProcessIsAlive() {
    global MainPID
    return (MainPID > 0 && ProcessExist(MainPID) = MainPID)
}

ParentProcessMatchesIdentity() {
    global MainPID, MainScriptPath
    return (ParentProcessIsAlive() && ProcessCommandLineHasScript(MainPID, MainScriptPath))
}

CloseValidatedParent(reason) {
    global MainPID, MainScriptPath, ParentClosedByWatchdog

    if (!ParentProcessMatchesIdentity()) {
        WriteWatchdogLog("restart_aborted", "parent_identity_mismatch", MainPID, MainScriptPath)
        return false
    }

    try {
        closedPID := ProcessClose(MainPID)
        if (closedPID != MainPID) {
            WriteWatchdogLog("restart_aborted", "parent_close_not_confirmed", MainPID, MainScriptPath)
            return false
        }

        stillRunningPID := ProcessWaitClose(MainPID, 5)
        if (stillRunningPID != 0) {
            WriteWatchdogLog("restart_aborted", "parent_still_running", MainPID, MainScriptPath)
            return false
        }
    } catch Error {
        WriteWatchdogLog("restart_aborted", "parent_close_failed", MainPID, MainScriptPath)
        return false
    }

    if (ProcessExist(MainPID)) {
        WriteWatchdogLog("restart_aborted", "parent_still_running", MainPID, MainScriptPath)
        return false
    }

    ParentClosedByWatchdog := true
    WriteWatchdogLog("parent_closed", reason, MainPID, MainScriptPath)
    return true
}

CloseMain(reason := "match_result") {
    return CloseValidatedParent(reason)
}

RestartMain(reason := "unspecified") {
    global MainPID, MainScriptPath, MainScriptDir, ParentClosedByWatchdog

    WriteWatchdogLog("restart_requested", reason, MainPID, MainScriptPath)

    if (!ParentClosedByWatchdog && !CloseValidatedParent(reason)) {
        ExitApp()
    }

    scriptAttributes := FileExist(MainScriptPath)
    if (!scriptAttributes || InStr(scriptAttributes, "D") || !WatchdogPathsEqual(CanonicalWatchdogPath(MainScriptPath), MainScriptPath)) {
        WriteWatchdogLog("restart_aborted", "parent_script_no_longer_valid", MainPID, MainScriptPath)
        ExitApp()
    }

    try {
        Run('"' A_AhkPath '" "' MainScriptPath '"', MainScriptDir, , &relaunchedPID)
        WriteWatchdogLog("parent_relaunched", reason, MainPID, MainScriptPath, relaunchedPID)
    } catch Error {
        WriteWatchdogLog("relaunch_failed", reason, MainPID, MainScriptPath)
    }

    ExitApp()
}

CleanupGdip(exitReason, exitCode) {
    global pToken
    Gdip_Shutdown(pToken)
}
