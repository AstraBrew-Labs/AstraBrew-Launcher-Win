@echo off
REM ============================================================================
REM  AstraBrew Launcher - Windows 构建打包入口（项目根目录版）
REM
REM  用法（在项目根目录执行）:
REM    build.bat                 完整构建打包（正式版）
REM    build.bat -Beta           构建测试版（版本号加 -beta，UI 显示 BETA 角标）
REM    build.bat -Clean          清空 dist 后构建正式版
REM    build.bat -Beta -Clean    清空 dist 后构建测试版
REM    build.bat -SkipBuild      跳过 NSIS，仅生成 zip 免安装版
REM
REM  可直接在资源管理器中双击运行，打包完成后窗口不会自动关闭。
REM ============================================================================
setlocal

REM 定位本批处理所在目录（即项目根目录）
set "ROOT=%~dp0"
set "PS1=%ROOT%build.ps1"

if not exist "%PS1%" (
    echo [X]  build.ps1 not found: %PS1%
    pause
    exit /b 1
)

REM 优先用 PowerShell 7 (pwsh)，找不到则用 Windows PowerShell
where pwsh >nul 2>nul
if %errorlevel%==0 (
    set "PS=pwsh"
) else (
    set "PS=powershell"
)

REM 转发所有命令行参数，保持窗口打开以便查看输出
%PS% -NoProfile -ExecutionPolicy Bypass -NoExit -File "%PS1%" %*

endlocal
