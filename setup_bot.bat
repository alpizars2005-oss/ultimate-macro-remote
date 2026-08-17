@echo off
cd /d "%~dp0"
py -m venv .venv
call .venv\Scripts\activate.bat
python -m pip install --upgrade pip
pip install -r requirements.txt
if not exist .env copy /Y .env.example .env >nul
echo.
echo Setup complete.
echo Edit .env on the central server and add your Discord bot token.
echo Optionally set DISCORD_GUILD_ID and a trusted public HTTPS origin.
pause
