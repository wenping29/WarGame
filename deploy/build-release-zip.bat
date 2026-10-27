@echo off
setlocal EnableExtensions EnableDelayedExpansion

REM ============================================================================
REM  HexFront release package build (front-end + back-end separated, zip format)
REM  -- Windows version
REM
REM  Usage:
REM    deploy\build-release-zip.bat
REM    set API_BASE_URL=https://api.your-domain.com/api && deploy\build-release-zip.bat
REM
REM  Output:
REM    deploy\out\hexfront-release-<timestamp>.zip
REM      hexfront-release\web\                     front-end static bundle (API URL injected)
REM      hexfront-release\server\HexFront.Api\     back-end publish + app.db (in-process hex-grid gen)
REM      hexfront-release\deploy\                  nginx conf + env + DEPLOY.md
REM  NOTE: the ~2G map / scenario data (server\data\) is NOT packed - it is
REM        provisioned on the deploy host separately. The package stays small.
REM
REM  Environment variables:
REM    API_BASE_URL       Full prefix the front-end calls the back-end with.
REM                       MUST end with /api.
REM                       Default: http://43.142.147.244:7777/api  (placeholder, replace before deploy)
REM    VITE_TIANDITU_TK   Tianditu token registered for the FRONT-END domain.
REM                       Leave empty -> the bundle keeps no key and falls back to the one
REM                       served by the back-end (data/tianditu.json), which is changeable
REM                       at runtime by editing that file and restarting the back-end.
REM
REM  Equivalent to: bash deploy/build-release-zip.sh
REM  The real packaging logic lives in deploy\tools\pack_release.py and is shared by
REM  both entry points. Use the .sh on Linux/macOS, this file on Windows.
REM
REM  IMPORTANT (Windows):
REM    * This file is deliberately plain ASCII and uses CRLF line endings.
REM      A non-ASCII batch file gets mis-parsed by cmd.exe, and putting
REM      "chcp 65001" inside it makes cmd.exe resume at wrong byte offsets
REM      (header lines then get executed as commands). Keep it ASCII; keep CRLF.
REM ============================================================================

set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "REPO=%%~fI"
set "OUT_DIR=%REPO%\deploy\out"

if not defined API_BASE_URL set "API_BASE_URL=http://43.142.147.244:7777/api"

REM ---- timestamp (via PowerShell, avoids the locale-dependent %DATE%) ----------
set "TS="
for /f "usebackq delims=" %%I in (`powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"`) do set "TS=%%I"
if not defined TS (
    echo [ERROR] Cannot generate a timestamp.
    exit /b 1
)

set "PUBLISH_DIR=%OUT_DIR%\publish-linux-x64-%TS%"
set "ART_DIR=%OUT_DIR%\artifacts-%TS%"
set "ZIP=%OUT_DIR%\hexfront-release-%TS%.zip"

if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"

REM ---- 1. front-end build ---------------------------------------------------
REM  VITE_* are injected as environment variables and take priority over the
REM  .env.production file. VITE_TIANDITU_TK is NOT set when empty, so a value
REM  configured in .env.production still applies (setting it to an empty string
REM  would override it and leave the front-end with no key and no fallback).
echo [1/4] Build front-end   VITE_API_BASE_URL=%API_BASE_URL%
set "VITE_API_BASE_URL=%API_BASE_URL%"
pushd "%REPO%\webfront"
if defined VITE_TIANDITU_TK (
    echo     Tianditu token: injected (front-end key, whitelisted for the front-end domain)
) else (
    echo     Tianditu token: not injected - falls back to data/tianditu.json served by the back-end
)
call npm run build
if errorlevel 1 goto :fail_frontend
popd

REM  Verify the target API URL really landed in the bundle (warn if it did not).
findstr /C:"%API_BASE_URL%" "%REPO%\webfront\dist\assets\*.js" >nul 2>&1
if errorlevel 1 echo     [WARN] %API_BASE_URL% not found in the bundle - the front-end may still point at the old address

REM ---- 2. back-end publish --------------------------------------------------
REM  --artifacts-path points at a brand-new directory: on Windows the MSBuild
REM  Copy task stack-overflows (MSB4166) when overwriting existing files, and a
REM  fresh directory avoids that "overwrite" trigger at the root.
echo [2/4] Publish back-end (linux-x64, in-process hex-grid gen)
pushd "%REPO%\server"
dotnet publish HexFront.Api\HexFront.Api.csproj -c Release -r linux-x64 --no-self-contained -o "%PUBLISH_DIR%" --artifacts-path "%ART_DIR%" --disable-build-servers
if errorlevel 1 goto :fail_publish_api
popd

REM ---- 3. merge the SQLite WAL ---------------------------------------------
REM  The -wal file may still hold unmerged transactions. Packing without merging
REM  is not data loss (the server replays the WAL on open), but -wal/-shm are
REM  excluded by the packaging rules, so the server would not see the most
REM  recent rows on its first start. Checkpoint them into the main DB first.
echo [3/4] Merge database WAL
pushd "%REPO%\server\HexFront.Api"
where sqlite3 >nul 2>&1
if errorlevel 1 (
    echo     sqlite3 not found, skipped - the server replays the WAL on first start
) else (
    sqlite3 app.db "PRAGMA wal_checkpoint(TRUNCATE);"
    if errorlevel 1 echo     WAL checkpoint failed, ignored
    set "DBCHECK="
    for /f "delims=" %%I in ('sqlite3 app.db "PRAGMA quick_check;"') do set "DBCHECK=%%I"
    if defined DBCHECK echo     integrity: !DBCHECK!
)
popd

REM ---- 4. pack the zip ------------------------------------------------------
echo [4/4] Pack zip (front-end + back-end + deploy only, no map data)
set "PY="
where python >nul 2>&1 && set "PY=python"
if defined PY goto :have_python
where py >nul 2>&1 && set "PY=py -3"
if defined PY goto :have_python
echo [ERROR] python not found - cannot pack.
exit /b 1
:have_python
pushd "%REPO%"
%PY% "%REPO%\deploy\tools\pack_release.py" --out "%ZIP%" --root hexfront-release --web "%REPO%\webfront\dist" --publish "%PUBLISH_DIR%" --db "%REPO%\server\HexFront.Api\app.db" --deploy "%REPO%\deploy\release"
if errorlevel 1 goto :fail_pack
popd

set "ZIPBYTES=0"
for %%I in ("%ZIP%") do set "ZIPBYTES=%%~zI"
set /a ZIPMB=%ZIPBYTES%/1024/1024
echo.
echo [DONE] Release package: %ZIP%
echo     Size: %ZIPMB% MB
echo     Unzip root: hexfront-release\
endlocal & exit /b 0

REM ==================== failure branches ====================
:fail_frontend
popd
echo [ERROR] Front-end build failed.
endlocal & exit /b 1

:fail_publish_api
popd
echo [ERROR] Back-end publish failed.
endlocal & exit /b 1

:fail_pack
popd
echo [ERROR] Packaging failed.
endlocal & exit /b 1
