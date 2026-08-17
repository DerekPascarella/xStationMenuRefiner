@echo off
rem Full cross-platform release build: win-x86, win-x64, linux-x64, osx-x64, osx-arm64.
rem Produces self-contained single-file publishes in _releases\.
rem Requires: .NET 8 SDK, and WSL for the macOS .app bundle step.
rem
rem Written by Derek Pascarella (ateam)

setlocal EnableDelayedExpansion

set /p VERSION=<version.txt
rem Strip a leading 'v' so a version.txt of either "1.0.0" or "v1.0.0" works.
if /i "%VERSION:~0,1%"=="v" set "VERSION=%VERSION:~1%"

echo ================================================
echo xStation Menu Refiner v%VERSION% - Full release build
echo ================================================
echo.

echo Formatting code...
dotnet format xStationMenuRefiner.sln
if %ERRORLEVEL% neq 0 goto :error
echo.

echo Cleaning previous release output...
if exist "_releases" rd /s /q "_releases" 2>nul
if not exist "_releases" mkdir "_releases"

rem ----- win-x64 -----
call :build_windows win-x64
if %ERRORLEVEL% neq 0 goto :error

rem ----- win-x86 -----
call :build_windows win-x86
if %ERRORLEVEL% neq 0 goto :error

rem Stale Windows-only bits can leak into the other RID packages otherwise.
echo.
echo Cleaning intermediate build output...
if exist "src\xStationMenuRefiner.Core\bin" rd /s /q "src\xStationMenuRefiner.Core\bin"
if exist "src\xStationMenuRefiner.Core\obj" rd /s /q "src\xStationMenuRefiner.Core\obj"
if exist "src\xStationMenuRefiner.App\bin"  rd /s /q "src\xStationMenuRefiner.App\bin"
if exist "src\xStationMenuRefiner.App\obj"  rd /s /q "src\xStationMenuRefiner.App\obj"

rem ----- linux-x64 -----
call :build_linux
if %ERRORLEVEL% neq 0 goto :error

rem ----- osx-x64 -----
rem Inlined rather than a :build_macos subroutine, because cmd.exe loses its read pointer
rem in the .bat across consecutive `call`s that invoke `wsl bash`, which produces a
rem phantom "system cannot find the batch label" on the second one.
set ARCH=x64
set RID=osx-x64
set TMP_OUT=_releases\temp-%RID%

echo.
echo ================================================
echo Building for %RID%
echo ================================================

dotnet publish src\xStationMenuRefiner.App\xStationMenuRefiner.App.csproj ^
    -c Release -r %RID% --self-contained true ^
    -o "%TMP_OUT%"
if %ERRORLEVEL% neq 0 goto :error

copy /Y LICENSE "%TMP_OUT%\" >nul 2>&1

echo Creating macOS .app bundle...
wsl bash create-macos-bundle.sh "_releases/temp-%RID%" "%VERSION%" "_releases" "%ARCH%" < NUL
if %ERRORLEVEL% neq 0 goto :error

rd /s /q "%TMP_OUT%" 2>nul

echo Built %RID%: _releases\xStationMenuRefiner.v%VERSION%-osx-%ARCH%-AppBundle.tar.gz

rem ----- osx-arm64 -----
set ARCH=arm64
set RID=osx-arm64
set TMP_OUT=_releases\temp-%RID%

echo.
echo ================================================
echo Building for %RID%
echo ================================================

dotnet publish src\xStationMenuRefiner.App\xStationMenuRefiner.App.csproj ^
    -c Release -r %RID% --self-contained true ^
    -o "%TMP_OUT%"
if %ERRORLEVEL% neq 0 goto :error

copy /Y LICENSE "%TMP_OUT%\" >nul 2>&1

echo Creating macOS .app bundle...
wsl bash create-macos-bundle.sh "_releases/temp-%RID%" "%VERSION%" "_releases" "%ARCH%" < NUL
if %ERRORLEVEL% neq 0 goto :error

rd /s /q "%TMP_OUT%" 2>nul

echo Built %RID%: _releases\xStationMenuRefiner.v%VERSION%-osx-%ARCH%-AppBundle.tar.gz

echo.
echo ================================================
echo All builds completed successfully
echo ================================================
echo.
echo Release files in _releases:
dir /B _releases\*.zip _releases\*.tar.gz 2>nul
echo.
goto :end

rem ------------------------------------------------------------------
:build_windows
set RID=%~1
set OUT=_releases\xStationMenuRefiner.v%VERSION%-%RID%

echo.
echo ================================================
echo Building for %RID%
echo ================================================

dotnet publish src\xStationMenuRefiner.App\xStationMenuRefiner.App.csproj ^
    -c Release -r %RID% --self-contained true ^
    -o "%OUT%"
if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%

copy /Y LICENSE "%OUT%\" >nul 2>&1

pushd "%OUT%" && tar -a -c -f ..\xStationMenuRefiner.v%VERSION%-%RID%.zip * && popd
if %ERRORLEVEL% neq 0 echo Warning: failed to zip %RID%

echo Built %RID%: _releases\xStationMenuRefiner.v%VERSION%-%RID%.zip
exit /b 0

rem ------------------------------------------------------------------
:build_linux
set OUT=_releases\xStationMenuRefiner.v%VERSION%-linux-x64

echo.
echo ================================================
echo Building for linux-x64
echo ================================================

dotnet publish src\xStationMenuRefiner.App\xStationMenuRefiner.App.csproj ^
    -c Release -r linux-x64 --self-contained true ^
    -o "%OUT%"
if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%

copy /Y LICENSE "%OUT%\" >nul 2>&1

rem Tar from inside WSL so the binary can be chmod +x first. Cmd.exe's native tar does
rem not carry Unix exec bits, and dotnet publish on Windows cannot set them either, so
rem the binary would land in the archive as 0644.
wsl bash -c "chmod +x '_releases/xStationMenuRefiner.v%VERSION%-linux-x64/xStationMenuRefiner' && cd _releases && tar -czf xStationMenuRefiner.v%VERSION%-linux-x64.tar.gz xStationMenuRefiner.v%VERSION%-linux-x64" < NUL
if %ERRORLEVEL% neq 0 echo Warning: failed to tar.gz linux-x64

echo Built linux-x64: _releases\xStationMenuRefiner.v%VERSION%-linux-x64.tar.gz
exit /b 0

rem ------------------------------------------------------------------
:error
echo.
echo ================================================
echo Build failed. See errors above.
echo ================================================
pause
exit /b 1

:end
echo Build process finished.
pause
