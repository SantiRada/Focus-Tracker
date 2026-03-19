@echo off
cd /d "%~dp0"
set DOTNET_ENVIRONMENT=Development
FocusTracker\bin\Release\net8.0-windows\FocusTracker.exe 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: El programa termino con codigo %errorlevel%
    pause
)
