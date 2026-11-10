@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0unity-recompile.ps1" %*
exit /b %ERRORLEVEL%
