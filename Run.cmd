@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "APP_PROJECT=%CD%\APP\BootSequence.csproj"
set "TEST_PROJECT=%CD%\test\BootSequence.Tests.csproj"
set "APP=%CD%\APP\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\BootSequence.exe"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo.
    echo .NET SDK not found
    echo Install Visual Studio 2022 or .NET 10 SDK
    echo.
    pause
    exit /b 1
)

echo Checking BootSequence
dotnet test "%TEST_PROJECT%" -c Release -p:Platform=x64 --nologo
if errorlevel 1 goto failed

echo Building BootSequence
dotnet build "%APP_PROJECT%" -c Release -p:Platform=x64 -p:WindowsAppSDKSelfContained=true --nologo
if errorlevel 1 goto failed

if not exist "%APP%" (
    echo.
    echo BootSequence.exe not found after build
    echo.
    pause
    exit /b 1
)

if /i "%BOOTSEQUENCE_BUILD_ONLY%"=="1" exit /b 0

echo Starting BootSequence
start "" "%APP%"
exit /b 0

:failed
echo.
echo Build failed
echo.
pause
exit /b 1
