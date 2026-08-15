@echo off
cd /d "%~dp0"
py -m venv .venv
call .venv\Scripts\activate.bat
python -m pip install --upgrade pip
pip install -r requirements.txt
if not exist .env copy /Y .env.example .env >nul
echo.
echo Setup complete.
echo Edit .env and add your Discord bot token, user ID, and server ID.
pause
