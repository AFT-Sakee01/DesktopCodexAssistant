@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall.ps1"
echo.
echo Log: %LOCALAPPDATA%\DesktopCodexAssistant\install.log
pause
