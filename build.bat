@echo off
echo ============================================
echo  Focus Tracker - Build Script
echo ============================================

where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: .NET SDK no encontrado.
    echo Descargar desde: https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

echo .NET SDK:
dotnet --version
echo.

echo Descargando fuentes (Fraunces + DM Sans)...
powershell -ExecutionPolicy Bypass -File "%~dp0download-fonts.ps1"
echo.

echo Restaurando paquetes NuGet...
dotnet restore FocusTracker\FocusTracker.csproj
if %errorlevel% neq 0 ( echo ERROR en restore. & pause & exit /b 1 )

echo.
echo Compilando Release...
dotnet build FocusTracker\FocusTracker.csproj -c Release --no-restore
if %errorlevel% neq 0 ( echo ERROR en build. & pause & exit /b 1 )

echo.
echo ============================================
echo  Build exitoso!
echo  Salida: FocusTracker\bin\Release\net8.0-windows\
echo ============================================
echo.

set /p RUN="Ejecutar ahora? (s/n): "
if /i "%RUN%"=="s" start FocusTracker\bin\Release\net8.0-windows\FocusTracker.exe

pause
