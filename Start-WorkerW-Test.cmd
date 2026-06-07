@echo off
setlocal
cd /d "%~dp0"
"%~dp0DesktopCodexAssistant.exe" --stop
timeout /t 1 /nobreak >nul
"%~dp0DesktopCodexAssistant.exe" --desktop-parent
echo.
echo Started in experimental WorkerW desktop-parent mode.
echo Runtime log: %LOCALAPPDATA%\DesktopCodexAssistant\DesktopCodexAssistant.log
pause
