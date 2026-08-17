@echo off
rem Inner-loop build script: linux-x64 self-contained single-file publish only.
rem The full cross-platform build is build.bat.
rem
rem Written by Derek Pascarella (ateam)

setlocal EnableDelayedExpansion

set /p VERSION=<version.txt
if /i "%VERSION:~0,1%"=="v" set "VERSION=%VERSION:~1%"

echo ================================================
echo Building xStation Menu Refiner v%VERSION% for linux-x64
echo ================================================
echo.

set OUT=_releases\xStationMenuRefiner.v%VERSION%-linux-x64

if exist "%OUT%" rd /s /q "%OUT%"

dotnet publish src\xStationMenuRefiner.App\xStationMenuRefiner.App.csproj ^
    -c Release -r linux-x64 --self-contained true ^
    -o "%OUT%"
if %ERRORLEVEL% neq 0 goto :error

copy /Y LICENSE "%OUT%\" >nul 2>&1

rem Tar from inside WSL so the binary keeps its exec bit.
wsl bash -c "chmod +x '_releases/xStationMenuRefiner.v%VERSION%-linux-x64/xStationMenuRefiner' && cd _releases && tar -czf xStationMenuRefiner.v%VERSION%-linux-x64.tar.gz xStationMenuRefiner.v%VERSION%-linux-x64" < NUL
if %ERRORLEVEL% neq 0 echo Warning: failed to tar.gz linux-x64

echo.
echo Built: _releases\xStationMenuRefiner.v%VERSION%-linux-x64.tar.gz
goto :end

:error
echo Build failed with code %ERRORLEVEL%
pause
exit /b %ERRORLEVEL%

:end
