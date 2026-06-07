@echo off
setlocal
cd /d "%~dp0"
"%~dp0DesktopCodexAssistant.exe" --stop
timeout /t 1 /nobreak >nul
"%~dp0DesktopCodexAssistant.exe"
echo.
echo Started in stable visible desktop mode.
echo Runtime log: %LOCALAPPDATA%\DesktopCodexAssistant\DesktopCodexAssistant.log
pause
