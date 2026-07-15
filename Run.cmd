@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "APP_PROJECT=%CD%\APP\BootSequence.csproj"
set "TEST_PROJECT=%CD%\test\BootSequence.Tests.csproj"
set "BUILD_DIR=%CD%\APP\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64"
set "APP=%BUILD_DIR%\BootSequence.exe"
set "ARTIFACTS=%CD%\artifacts"
set "PORTABLE_DIR=%ARTIFACTS%\Portable"
set "PORTABLE_EXE=%PORTABLE_DIR%\BootSequence.exe"

if /i "%BOOTSEQUENCE_BUILD_ONLY%"=="1" goto run_build
if /i "%~1"=="run" goto run_build
if /i "%~1"=="portable" goto build_portable

:menu
cls
echo BootSequence
echo.
echo [1] Run
echo [2] Build Portable
echo [3] Exit
echo.
choice /c 123 /n /m "Select: "
if errorlevel 3 exit /b 0
if errorlevel 2 goto build_portable
goto run_build

:check_sdk
where dotnet >nul 2>&1
if errorlevel 1 (
    echo.
    echo .NET 10 SDK not found
    exit /b 1
)
exit /b 0

:run_tests
echo.
echo Checking BootSequence
dotnet test "%TEST_PROJECT%" -c Release -p:Platform=x64 --nologo
exit /b %errorlevel%

:check_closed
tasklist /FI "IMAGENAME eq BootSequence.exe" /NH 2>nul | find /I "BootSequence.exe" >nul
if not errorlevel 1 (
    echo.
    echo Close BootSequence first
    exit /b 1
)
exit /b 0

:run_build
call :check_sdk
if errorlevel 1 goto failed
call :check_closed
if errorlevel 1 goto failed
call :run_tests
if errorlevel 1 goto failed

echo.
echo Building BootSequence
dotnet build "%APP_PROJECT%" -c Release -p:Platform=x64 -p:SelfContained=false -p:WindowsAppSDKSelfContained=false --nologo
if errorlevel 1 goto failed

if not exist "%APP%" (
    echo.
    echo BootSequence.exe not found
    goto failed
)

if /i "%BOOTSEQUENCE_BUILD_ONLY%"=="1" exit /b 0

echo.
echo Starting BootSequence
start "" "%APP%"
exit /b 0

:build_portable
call :check_sdk
if errorlevel 1 goto failed
call :check_closed
if errorlevel 1 goto failed
call :run_tests
if errorlevel 1 goto failed

echo.
echo Building portable app
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$path=[IO.Path]::GetFullPath('%PORTABLE_DIR%'); $root=[IO.Path]::GetFullPath('%ARTIFACTS%')+[IO.Path]::DirectorySeparatorChar; if(-not $path.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){exit 2}; if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Recurse -Force}; New-Item -ItemType Directory -Path $path -Force | Out-Null"
if errorlevel 1 goto failed

dotnet publish "%APP_PROJECT%" -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:WindowsAppSDKSelfContained=true -p:EnableMsixTooling=true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -p:PublishReadyToRun=false -o "%PORTABLE_DIR%" --nologo
if errorlevel 1 goto failed

if not exist "%PORTABLE_EXE%" (
    echo.
    echo Portable app was not created
    goto failed
)

echo.
echo Portable app created successfully
echo File: %PORTABLE_EXE%
powershell -NoProfile -Command "$bytes=(Get-Item -LiteralPath '%PORTABLE_EXE%').Length; Write-Host ('Size: {0:N1} MB' -f ($bytes / 1MB))"
echo.
pause
exit /b 0

:failed
echo.
echo Operation failed
echo.
pause
exit /b 1
