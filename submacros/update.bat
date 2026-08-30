@echo off
setlocal

rem Compatibility wrapper only. The runtime updater now copies update.ps1 to %%TEMP%%
rem and invokes it directly so the live macro directory is never used as the updater.
if "%~5"=="" (
  echo This updater requires DownloadUrl, MacroDir, CommitSha, MarkerPath and EntryPoint.
  exit /b 2
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0update.ps1" ^
  -DownloadUrl "%~1" ^
  -MacroDir "%~2" ^
  -CommitSha "%~3" ^
  -MarkerPath "%~4" ^
  -EntryPoint "%~5"

exit /b %ERRORLEVEL%
