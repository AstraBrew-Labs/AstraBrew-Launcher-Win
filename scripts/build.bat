@echo off
REM ============================================================================
REM  AstraBrew Launcher - Windows build & package entry point
REM  Usage:
REM    scripts\build.bat            full build (NSIS exe + portable zip)
REM    scripts\build.bat -Clean     clean dist then full build
REM    scripts\build.bat -SkipBuild skip NSIS, only generate portable zip
REM
REM  Double-click in Explorer to run a full build.
REM ============================================================================
setlocal

REM Locate PowerShell: prefer pwsh (PowerShell 7+), fall back to Windows PowerShell
where pwsh >nul 2>nul
if %errorlevel%==0 (
    set "PS=pwsh"
) else (
    set "PS=powershell"
)

REM Resolve script directory (works even when launched from explorer)
set "SCRIPT_DIR=%~dp0"
set "PS1=%SCRIPT_DIR%build.ps1"

if not exist "%PS1%" (
    echo [X]  build.ps1 not found: %PS1%
    exit /b 1
)

REM Forward all CLI args to PowerShell
set "ARGS=%*"

REM -ExecutionPolicy Bypass: allow running the script without changing system policy
REM -NoExit: keep window open after completion so user can read output
%PS% -NoProfile -ExecutionPolicy Bypass -NoExit -File "%PS1%" %ARGS%

endlocal
