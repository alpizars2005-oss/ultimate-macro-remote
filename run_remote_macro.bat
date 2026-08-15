@echo off
cd /d "%~dp0"
if exist "submacros\AutoHotkey64.exe" (
  start "" "submacros\AutoHotkey64.exe" "Main_Remote.ahk"
) else (
  start "" "Main_Remote.ahk"
)
