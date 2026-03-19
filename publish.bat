@echo off
echo ============================================
echo  Focus Tracker - Publish (Single EXE)
echo ============================================

where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: .NET SDK not found.
    echo Download from: https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

echo Publishing self-contained single-file EXE...
dotnet publish FocusTracker\FocusTracker.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:PublishReadyToRun=false ^
    -o publish

if %errorlevel% neq 0 (
    echo ERROR: Publish failed.
    pause
    exit /b 1
)

echo.
echo ============================================
echo  Publicado en: publish\FocusTracker.exe
echo  Tamano: 
for %%A in (publish\FocusTracker.exe) do echo  %%~zA bytes
echo ============================================
echo.
pause
