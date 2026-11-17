@echo off
setlocal
cd /d "%~dp0"
title WinTune - build

rem Look for the .NET 10 SDK. If it is missing, install it with winget.
where dotnet >nul 2>nul
if errorlevel 1 goto install
dotnet --list-sdks 2>nul | findstr /b /c:"10." >nul
if errorlevel 1 goto install
goto build

:install
echo .NET 10 SDK not found. Installing it with winget...
echo.
winget install --id Microsoft.DotNet.SDK.10 --exact --accept-source-agreements --accept-package-agreements
if errorlevel 1 goto installfailed
set "PATH=%ProgramFiles%\dotnet;%PATH%"
echo.

:build
echo Building WinTune. The first time it downloads the components and can take a few minutes...
dotnet publish "src\WinTune\WinTune.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o "dist" > build.log 2>&1
if errorlevel 1 goto buildfailed

echo.
echo Done. The program is here: %~dp0dist\WinTune.exe
echo.
choice /c YN /m "Start WinTune now"
if errorlevel 2 exit /b 0
start "" "%~dp0dist\WinTune.exe"
exit /b 0

:installfailed
echo.
echo Installing the .NET 10 SDK failed.
echo Install it by hand with: winget install Microsoft.DotNet.SDK.10
echo.
pause
exit /b 1

:buildfailed
echo.
echo Build FAILED. If WinTune is open, close it and try again.
echo The errors are in the build.log file:
echo.
findstr /c:"error" build.log
echo.
pause
exit /b 1
