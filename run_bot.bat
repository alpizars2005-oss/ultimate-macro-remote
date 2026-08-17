@echo off
setlocal
cd /d "%~dp0"

if not exist ".venv\Scripts\python.exe" (
  echo [Remote] ERROR: .venv\Scripts\python.exe was not found.
  echo [Remote] Create the central Python environment and install requirements.txt first.
  pause
  exit /b 2
)

if not exist ".env" (
  echo [Remote] ERROR: .env was not found in %CD%.
  echo [Remote] Copy .env.example to .env and fill the central-server values.
  pause
  exit /b 2
)

".venv\Scripts\python.exe" -m central.preflight
if errorlevel 1 (
  echo.
  echo [Remote] Startup stopped because the central configuration is not compatible yet.
  pause
  exit /b 2
)

echo.
echo [Remote] Starting central backend + Discord bot...
".venv\Scripts\python.exe" bot.py
set "REMOTE_EXIT=%ERRORLEVEL%"

echo.
if not "%REMOTE_EXIT%"=="0" echo [Remote] Central runtime exited with code %REMOTE_EXIT%.
pause
exit /b %REMOTE_EXIT%
