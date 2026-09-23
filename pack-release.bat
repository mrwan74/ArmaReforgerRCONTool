@echo off
setlocal enabledelayedexpansion

REM ============================================================================
REM ARMA REFORGER RCON TOOL (ARRT) - LOCAL RELEASE PACKER (CROSS-PLATFORM AUTO-UPDATE)
REM ============================================================================

if "%~1"=="" (
    echo.
    echo [ERROR] Missing version argument.
    echo Usage:   pack-release.bat ^<SemVer^>
    echo Example: pack-release.bat 0.9.0-alpha.4
    echo.
    exit /b 1
)

set "RAW_VER=%~1"
if /i "!RAW_VER:~0,1!"=="v" set "RAW_VER=!RAW_VER:~1!"
set "VERSION=!RAW_VER!"

set "PACK_ID=ReforgerRcon"
set "PACK_TITLE=ARMA Reforger RCON Tool"
set "MAIN_EXE_WIN=ARMA REFORGER RCON TOOL.exe"
set "MAIN_EXE_LINUX=ARMA REFORGER RCON TOOL"
set "PROJECT_PATH=%~dp0ReforgerRcon\ReforgerRcon.csproj"
set "PROJECT_DIR=%~dp0ReforgerRcon"
set "ICON_PATH=%PROJECT_DIR%\Assets\app.ico"
set "PUBLISH_WIN=%~dp0publish\win-x64"
set "PUBLISH_LINUX=%~dp0publish\linux-x64"
set "RELEASES_DIR=%~dp0releases"
set "STAGE_SCRIPT=%~dp0stage-portable.ps1"

set "ZIP_WIN=ARRT_v!VERSION!_win-x64.zip"
set "ZIP_LINUX=ARRT_v!VERSION!_linux-x64.zip"

REM ----------------------------------------------------------------------------
REM Locate 7-Zip Executable
REM ----------------------------------------------------------------------------
set "SEVENZIP=C:\Program Files\7-Zip\7z.exe"
if not exist "%SEVENZIP%" (
    where 7z.exe >nul 2>&1 && for /f "delims=" %%I in ('where 7z.exe') do set "SEVENZIP=%%I"
)

echo ============================================================================
echo  PACKAGING FOR GITHUB RELEASES: %PACK_ID% v!VERSION!
echo  Configuration : Debug
echo  Auto-Updates  : Windows [win-x64] and Linux [linux-x64]
echo  Compression   : 7-Zip Deflate Ultra [Level 9, 258 FastBytes, 15 Passes]
echo  Output Feeds  : releases.win.json AND releases.linux.json
echo ============================================================================

REM Clean temporary publish and Velopack directories
if exist "%TEMP%\velopack" rmdir /s /q "%TEMP%\velopack" >nul 2>&1
if exist "%~dp0publish" rmdir /s /q "%~dp0publish"

REM Retain %RELEASES_DIR% so vpk can compute deltas
if not exist "%RELEASES_DIR%" mkdir "%RELEASES_DIR%"

REM ----------------------------------------------------------------------------
REM [1/5] Compile and publish Windows x64 (Debug, Single-File, ReadyToRun)
REM ----------------------------------------------------------------------------
echo.
echo [1/5] Compiling and publishing Windows x64 [Debug, Single-File, ReadyToRun]...
dotnet publish "%PROJECT_PATH%" ^
    -c Debug ^
    -r win-x64 ^
    --self-contained true ^
    /p:PublishSingleFile=true ^
    /p:PublishReadyToRun=true ^
    /p:DebugType=portable ^
    /p:DebugSymbols=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%PUBLISH_WIN%"

if errorlevel 1 (
    echo [ERROR] dotnet publish for Windows failed. Aborting release.
    exit /b 1
)

REM Fallback verification: ensure GeoIP mmdb files exist in the publish directory
if not exist "%PUBLISH_WIN%\GeoIP" mkdir "%PUBLISH_WIN%\GeoIP"
if exist "%PROJECT_DIR%\GeoIP\*.mmdb" copy /y "%PROJECT_DIR%\GeoIP\*.mmdb" "%PUBLISH_WIN%\GeoIP\" >nul 2>&1
if exist "%~dp0GeoIP\*.mmdb" copy /y "%~dp0GeoIP\*.mmdb" "%PUBLISH_WIN%\GeoIP\" >nul 2>&1
if exist "%PROJECT_DIR%\appdata\geoip\*.mmdb" copy /y "%PROJECT_DIR%\appdata\geoip\*.mmdb" "%PUBLISH_WIN%\GeoIP\" >nul 2>&1

REM Strip bloated 3rd-party native PDBs
if exist "%PUBLISH_WIN%\libSkiaSharp.pdb" del /f /q "%PUBLISH_WIN%\libSkiaSharp.pdb" >nul 2>&1
if exist "%PUBLISH_WIN%\libHarfBuzzSharp.pdb" del /f /q "%PUBLISH_WIN%\libHarfBuzzSharp.pdb" >nul 2>&1

REM ----------------------------------------------------------------------------
REM [2/5] Velopack Packaging for Windows
REM ----------------------------------------------------------------------------
echo.
echo [2/5] Running Velopack packaging for Windows...
vpk pack ^
    -u "%PACK_ID%" ^
    -v "%VERSION%" ^
    -p "%PUBLISH_WIN%" ^
    -e "%MAIN_EXE_WIN%" ^
    --packTitle "%PACK_TITLE%" ^
    --icon "%ICON_PATH%" ^
    --exclude "^$" ^
    -o "%RELEASES_DIR%"

if errorlevel 1 (
    echo [ERROR] Velopack packaging for Windows failed.
    exit /b 1
)

REM Clean any Setup.exe
if exist "%RELEASES_DIR%\*Setup.exe" del /f /q "%RELEASES_DIR%\*Setup.exe" >nul 2>&1
if exist "%RELEASES_DIR%\*setup.exe" del /f /q "%RELEASES_DIR%\*setup.exe" >nul 2>&1

REM Rename portable zip to ARRT_v<VERSION>_win-x64.zip
if exist "%RELEASES_DIR%\!ZIP_WIN!" del /f /q "%RELEASES_DIR%\!ZIP_WIN!" >nul 2>&1
if exist "%RELEASES_DIR%\%PACK_ID%-win-Portable.zip" move /y "%RELEASES_DIR%\%PACK_ID%-win-Portable.zip" "%RELEASES_DIR%\!ZIP_WIN!" >nul
if exist "%RELEASES_DIR%\%PACK_ID%-Portable.zip" move /y "%RELEASES_DIR%\%PACK_ID%-Portable.zip" "%RELEASES_DIR%\!ZIP_WIN!" >nul

REM Staging base .nupkg for deltas and cleaning archive duplicates
echo [2.1/5] Staging Windows delta package and cleaning archive duplicates...
if exist "%STAGE_SCRIPT%" powershell -NoProfile -ExecutionPolicy Bypass -File "%STAGE_SCRIPT%" "%RELEASES_DIR%\!ZIP_WIN!" "%RELEASES_DIR%\%PACK_ID%-%VERSION%-full.nupkg" "%PUBLISH_WIN%"

REM Re-compressing Windows ZIP with 7-Zip Deflate Ultra
if exist "%SEVENZIP%" (
    echo.
    echo [2.2/5] Applying 7-Zip Deflate Ultra compression to Windows portable archive...
    set "TEMP_REPACK_WIN=%TEMP%\arrt_repack_win_%RANDOM%"
    if exist "!TEMP_REPACK_WIN!" rmdir /s /q "!TEMP_REPACK_WIN!"
    mkdir "!TEMP_REPACK_WIN!"

    "%SEVENZIP%" x "%RELEASES_DIR%\!ZIP_WIN!" -o"!TEMP_REPACK_WIN!" -y -bsp1
    del /f /q "%RELEASES_DIR%\!ZIP_WIN!"

    pushd "!TEMP_REPACK_WIN!"
    "%SEVENZIP%" a -tzip -mx=9 -mm=Deflate -mfb=258 -mpass=15 -mmt=on -bsp1 "%RELEASES_DIR%\!ZIP_WIN!" *
    if exist ".portable" "%SEVENZIP%" a -tzip -mx=9 -mm=Deflate -bso0 "%RELEASES_DIR%\!ZIP_WIN!" .portable >nul 2>&1
    popd

    rmdir /s /q "!TEMP_REPACK_WIN!"
    echo [SUCCESS] Windows archive re-compressed with 7-Zip Deflate Ultra.
)

REM ----------------------------------------------------------------------------
REM [3/5] Compile and publish Linux x64 (Debug, Single-File, ReadyToRun)
REM ----------------------------------------------------------------------------
echo.
echo [3/5] Compiling and publishing Linux x64 [Debug, Single-File, ReadyToRun]...
dotnet publish "%PROJECT_PATH%" ^
    -c Debug ^
    -r linux-x64 ^
    --self-contained true ^
    /p:PublishSingleFile=true ^
    /p:PublishReadyToRun=true ^
    /p:DebugType=portable ^
    /p:DebugSymbols=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%PUBLISH_LINUX%"

if errorlevel 1 (
    echo [ERROR] dotnet publish for Linux failed. Aborting release.
    exit /b 1
)

REM Fallback verification: ensure GeoIP mmdb files exist in Linux publish directory
if not exist "%PUBLISH_LINUX%\GeoIP" mkdir "%PUBLISH_LINUX%\GeoIP"
if exist "%PROJECT_DIR%\GeoIP\*.mmdb" copy /y "%PROJECT_DIR%\GeoIP\*.mmdb" "%PUBLISH_LINUX%\GeoIP\" >nul 2>&1
if exist "%~dp0GeoIP\*.mmdb" copy /y "%~dp0GeoIP\*.mmdb" "%PUBLISH_LINUX%\GeoIP\" >nul 2>&1
if exist "%PROJECT_DIR%\appdata\geoip\*.mmdb" copy /y "%PROJECT_DIR%\appdata\geoip\*.mmdb" "%PUBLISH_LINUX%\GeoIP\" >nul 2>&1

REM Strip 3rd party debug symbols from Linux output
if exist "%PUBLISH_LINUX%\libSkiaSharp.pdb" del /f /q "%PUBLISH_LINUX%\libSkiaSharp.pdb" >nul 2>&1
if exist "%PUBLISH_LINUX%\libHarfBuzzSharp.pdb" del /f /q "%PUBLISH_LINUX%\libHarfBuzzSharp.pdb" >nul 2>&1

REM ----------------------------------------------------------------------------
REM [4/5] Velopack Packaging for Linux (Generates releases.linux.json, .nupkg, and .AppImage)
REM ----------------------------------------------------------------------------
echo.
echo [4/5] Running Velopack packaging for Linux (Cross-compiling Linux channel)...
vpk [linux] pack ^
    -u "%PACK_ID%" ^
    -v "%VERSION%" ^
    -p "%PUBLISH_LINUX%" ^
    -e "%MAIN_EXE_LINUX%" ^
    --packTitle "%PACK_TITLE%" ^
    --icon "%ICON_PATH%" ^
    --exclude "^$" ^
    -o "%RELEASES_DIR%"

if errorlevel 1 (
    echo [ERROR] Velopack packaging for Linux failed.
    exit /b 1
)

REM ----------------------------------------------------------------------------
REM [5/5] Create Linux x64 Portable ZIP with 7-Zip Deflate Ultra
REM ----------------------------------------------------------------------------
echo.
echo [5/5] Creating Linux x64 Portable ZIP with 7-Zip Deflate Ultra...
if exist "%RELEASES_DIR%\!ZIP_LINUX!" del /f /q "%RELEASES_DIR%\!ZIP_LINUX!" >nul 2>&1

if exist "%SEVENZIP%" (
    pushd "%PUBLISH_LINUX%"
    "%SEVENZIP%" a -tzip -mx=9 -mm=Deflate -mfb=258 -mpass=15 -mmt=on -bsp1 "%RELEASES_DIR%\!ZIP_LINUX!" *
    popd
    echo [SUCCESS] Linux archive created with 7-Zip Deflate Ultra.
) else (
    powershell -NoProfile -Command "Compress-Archive -Path '%PUBLISH_LINUX%\*' -DestinationPath '%RELEASES_DIR%\!ZIP_LINUX!' -Force"
)

echo.
echo ============================================================================
echo  PACKAGING SUCCEEDED!
echo  Releases ready in releases\ folder:
echo    [Windows Feed]
echo    - releases.win.json
echo    - %PACK_ID%-%VERSION%-full.nupkg
echo    - !ZIP_WIN!
echo.
echo    [Linux Feed]
echo    - releases.linux.json
echo    - %PACK_ID%-%VERSION%-linux-full.nupkg
echo    - !ZIP_LINUX!
echo ============================================================================

explorer "%RELEASES_DIR%"
