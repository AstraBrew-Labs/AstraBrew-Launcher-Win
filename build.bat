@echo off
rem AstraBrew Launcher（Windows）构建打包入口。
rem 双击即可运行；参数会原样透传给 build.ps1，例如：
rem   build.bat -Clean
rem   build.bat -Beta -Clean
rem   build.bat -SkipBuild
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
    echo.
    echo [X] 构建失败，退出码 %EXITCODE%
)
echo.
pause
exit /b %EXITCODE%
