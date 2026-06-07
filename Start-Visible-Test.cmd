@echo off
setlocal
cd /d "%~dp0"
"%~dp0CodexDeveloperAssistantWindowOnWOA.exe" --stop
timeout /t 1 /nobreak >nul
"%~dp0CodexDeveloperAssistantWindowOnWOA.exe"
echo.
echo Started in stable visible desktop mode.
echo Runtime log: %LOCALAPPDATA%\CodexDeveloperAssistantWindowOnWOA\CodexDeveloperAssistantWindowOnWOA.log
pause
