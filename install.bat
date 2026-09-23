@echo off
REM Double-click entry point. Runs install.ps1 next to this file.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
