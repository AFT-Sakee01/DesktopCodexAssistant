@echo off
setlocal
cd /d "%~dp0"
"%~dp0CodexDeveloperAssistantWindowOnWOA.exe" --stop
timeout /t 1 /nobreak >nul
"%~dp0CodexDeveloperAssistantWindowOnWOA.exe" --desktop-parent
echo.
echo Started in experimental WorkerW desktop-parent mode.
echo Runtime log: %LOCALAPPDATA%\CodexDeveloperAssistantWindowOnWOA\CodexDeveloperAssistantWindowOnWOA.log
pause
