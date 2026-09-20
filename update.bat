@echo off
REM Double-click this to update GorillaNextBots to the newest release.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0update.ps1" %*
echo.
pause
