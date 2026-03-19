@echo off
echo ============================================
echo  Focus Tracker - Build Installer
echo ============================================
echo.

cd /d "%~dp0"

REM -- 1. Verificar que existe el EXE publicado
if not exist "..\publish\FocusTracker.exe" (
    echo ERROR: No se encontro publish\FocusTracker.exe
    echo Primero ejecuta publish.bat desde la raiz del proyecto.
    echo.
    pause
    exit /b 1
)

REM -- 2. Buscar NSIS en ubicaciones comunes
set MAKENSIS=""
if exist "C:\Program Files (x86)\NSIS\makensis.exe" set MAKENSIS="C:\Program Files (x86)\NSIS\makensis.exe"
if exist "C:\Program Files\NSIS\makensis.exe"       set MAKENSIS="C:\Program Files\NSIS\makensis.exe"

if %MAKENSIS%=="" (
    echo ERROR: NSIS no encontrado.
    echo.
    echo Descarga NSIS desde: https://nsis.sourceforge.io/Download
    echo Instala y volvé a ejecutar este script.
    echo.
    pause
    exit /b 1
)

echo NSIS encontrado: %MAKENSIS%
echo.
echo Compilando instalador...
%MAKENSIS% FocusTracker.nsi

if %errorlevel% neq 0 (
    echo.
    echo ERROR: El instalador no pudo compilarse.
    pause
    exit /b 1
)

echo.
echo ============================================
echo  Instalador creado: 
echo  installer\FocusTracker_Setup_v1.4.exe
echo ============================================
echo.
pause
