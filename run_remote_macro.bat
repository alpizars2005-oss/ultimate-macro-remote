@echo off
cd /d "%~dp0"
if exist "submacros\AutoHotkey64.exe" (
  start "" "%~dp0submacros\AutoHotkey64.exe" "%~dp0Main_Remote.ahk"
) else (
  start "" "%~dp0Main_Remote.ahk"
)
