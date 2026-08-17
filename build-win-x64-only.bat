@echo off
rem Inner-loop build script: win-x64 self-contained single-file publish only.
rem The full cross-platform build is build.bat.
rem
rem Written by Derek Pascarella (ateam)

setlocal EnableDelayedExpansion

set /p VERSION=<version.txt
if /i "%VERSION:~0,1%"=="v" set "VERSION=%VERSION:~1%"

echo ================================================
echo Building xStation Menu Refiner v%VERSION% for win-x64
echo ================================================
echo.

if exist "_releases\xStationMenuRefiner.v%VERSION%-win-x64" (
    rd /s /q "_releases\xStationMenuRefiner.v%VERSION%-win-x64"
)

dotnet publish src\xStationMenuRefiner.App\xStationMenuRefiner.App.csproj ^
    -c Release -r win-x64 --self-contained true ^
    -o "_releases\xStationMenuRefiner.v%VERSION%-win-x64"
if %ERRORLEVEL% neq 0 goto :error

copy /Y LICENSE "_releases\xStationMenuRefiner.v%VERSION%-win-x64\" >nul 2>&1

pushd "_releases\xStationMenuRefiner.v%VERSION%-win-x64"
tar -a -c -f ..\xStationMenuRefiner.v%VERSION%-win-x64.zip *
popd
if %ERRORLEVEL% neq 0 echo Warning: failed to create win-x64 zip

echo.
echo Built: _releases\xStationMenuRefiner.v%VERSION%-win-x64.zip
goto :end

:error
echo Build failed with code %ERRORLEVEL%
pause
exit /b %ERRORLEVEL%

:end
