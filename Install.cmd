@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
echo.
echo Log: %LOCALAPPDATA%\DesktopCodexAssistant\install.log
echo Runtime log: %LOCALAPPDATA%\DesktopCodexAssistant\DesktopCodexAssistant.log
pause
