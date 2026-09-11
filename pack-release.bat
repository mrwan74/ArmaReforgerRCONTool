@echo off
setlocal enabledelayedexpansion

REM ============================================================================
REM ARMA REFORGER RCON TOOL (ARRT) - LOCAL RELEASE PACKER
REM ============================================================================

if "%~1"=="" (
    echo.
    echo [ERROR] Missing version argument.
    echo Usage:   pack-release.bat ^<SemVer^>
    echo Example: pack-release.bat 0.9.1-preview.1
    echo.
    exit /b 1
)

set "VERSION=%~1"
set "PACK_ID=ReforgerRcon"
set "PACK_TITLE=ARMA Reforger RCON Tool"
set "MAIN_EXE=ReforgerRcon.exe"
set "PROJECT_PATH=%~dp0ReforgerRcon\ReforgerRcon.csproj"
set "ICON_PATH=%~dp0ReforgerRcon\Assets\app.ico"
set "PUBLISH_DIR=%~dp0publish"
set "RELEASES_DIR=%~dp0releases"

echo ============================================================================
echo  PACKAGING FOR GITHUB WEB RELEASES: %PACK_ID% v%VERSION%
echo  Configuration: Debug ^| Runtime: win-x64 ^| Self-Contained: true
echo ============================================================================

REM Clean only the temporary compilation directory
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"

REM IMPORTANT: We DO NOT delete %RELEASES_DIR% so vpk can use prior versions to build deltas!
if not exist "%RELEASES_DIR%" mkdir "%RELEASES_DIR%"

echo.
echo [1/2] Compiling and publishing .NET 10 project...
dotnet publish "%PROJECT_PATH%" ^
    -c Debug ^
    -r win-x64 ^
    --self-contained true ^
    /p:PublishSingleFile=false ^
    -o "%PUBLISH_DIR%"

if errorlevel 1 (
    echo [ERROR] dotnet publish failed. Aborting release.
    exit /b 1
)

echo.
echo [2/2] Running Velopack packaging (Creating Installer, Full .nupkg, and Delta)...
vpk pack ^
    -u "%PACK_ID%" ^
    -v "%VERSION%" ^
    -p "%PUBLISH_DIR%" ^
    -e "%MAIN_EXE%" ^
    --packTitle "%PACK_TITLE%" ^
    --icon "%ICON_PATH%" ^
    -o "%RELEASES_DIR%"

if errorlevel 1 (
    echo [ERROR] Velopack packaging failed.
    exit /b 1
)

echo.
echo ============================================================================
echo  PACKAGING SUCCEEDED!
echo  Opening the releases folder for manual upload to the GitHub website...
echo ============================================================================

explorer "%RELEASES_DIR%"