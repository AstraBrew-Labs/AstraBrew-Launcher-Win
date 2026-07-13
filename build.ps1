<#
.SYNOPSIS
    AstraBrew Launcher Windows 构建打包脚本（项目根目录版）
.DESCRIPTION
    与 scripts/build.ps1 功能完全等价，仅路径计算不同（脚本位于项目根目录）。
    无需 Git Bash / WSL，原生 PowerShell 即可运行。双击 build.bat 也可启动。
.EXAMPLE
    .\build.ps1                 完整构建打包（正式版）
    .\build.ps1 -Beta           构建测试版（版本号加 -beta，UI 显示 BETA 角标）
    .\build.ps1 -Beta -Clean    清空 dist 后构建测试版
    .\build.ps1 -SkipBuild      跳过 NSIS，仅生成 zip 免安装版
.PARAMETER Beta
    构建测试版：版本号追加 -beta 后缀 + UI 渲染 BETA 角标。
.PARAMETER Release
    构建正式版（默认）：版本号原样 + 无 BETA 角标。
.PARAMETER SkipBuild
    跳过 cargo packager 步骤（假设 dist 已有 exe），仅生成 zip 免安装版。
.PARAMETER Clean
    打包前清空 dist 目录。
.PARAMETER Version
    覆盖版本号（默认从 Cargo.toml 读取）。
#>
[CmdletBinding()]
param(
    [switch] $SkipBuild,
    [switch] $Clean,
    [switch] $Beta,
    [switch] $Release,
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# ============================================================================
# 路径与常量（脚本位于项目根目录，ProjectRoot = 脚本所在目录）
# ============================================================================
$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$DistDir = Join-Path $ProjectRoot 'dist'
$ReleaseDir = Join-Path $ProjectRoot 'target\release'
$CargoToml = Join-Path $ProjectRoot 'Cargo.toml'

$ProductName = 'AstraBrew Launcher'
$BinaryName = 'astrabrew-launcher-win.exe'

Set-Location $ProjectRoot

function Write-Step([string]$msg) { Write-Host "`n[*] $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "[OK] $msg" -ForegroundColor Green }
function Write-Warn([string]$msg) { Write-Host "[!]  $msg" -ForegroundColor Yellow }
function Die([string]$msg) { Write-Host "[X]  $msg" -ForegroundColor Red; exit 1 }

# ============================================================================
# 1. 读取版本号
# ============================================================================
Write-Step '读取版本号'
if (-not $Version) {
    if (-not (Test-Path $CargoToml)) { Die "找不到 Cargo.toml: $CargoToml" }
    $cargoContent = Get-Content $CargoToml -Raw
    $pkgMatch = [regex]::Match($cargoContent, '(?ms)^\[package\][^\[]*?version\s*=\s*"([^"]+)"')
    if (-not $pkgMatch.Success) { Die '无法从 Cargo.toml 解析 version' }
    $Version = $pkgMatch.Groups[1].Value
}
Write-Ok "版本号: $Version"

# 根据 -Beta / -Release 参数计算最终版本号并设置编译时环境变量
# （build.rs 读取 ASTRABREW_BUILD_TYPE 控制 BETA 角标渲染）
if ($Beta) {
    $Version = "$Version-beta"
    $env:ASTRABREW_BUILD_TYPE = 'beta'
    Write-Ok "构建模式: Beta（版本号追加 -beta，UI 渲染 BETA 角标）"
} else {
    $env:ASTRABREW_BUILD_TYPE = 'release'
    Write-Ok "构建模式: Release（正式版，无 BETA 角标）"
}

# ============================================================================
# 2. 检查工具链
# ============================================================================
Write-Step '检查工具链'

$cargoPath = (Get-Command cargo -ErrorAction SilentlyContinue)
if (-not $cargoPath) { Die '未找到 cargo，请先安装 Rust 工具链' }
Write-Ok "cargo: $($cargoPath.Source)"

if (-not $SkipBuild) {
    $packagerPath = (Get-Command cargo-packager -ErrorAction SilentlyContinue)
    if (-not $packagerPath) {
        Write-Warn '未安装 cargo-packager，开始安装（cargo install cargo-packager --locked）...'
        & cargo install cargo-packager --locked
        if ($LASTEXITCODE -ne 0) { Die 'cargo-packager 安装失败' }
        $packagerPath = (Get-Command cargo-packager -ErrorAction SilentlyContinue)
        if (-not $packagerPath) { Die 'cargo-packager 安装后仍无法找到，请检查 cargo bin 是否在 PATH' }
    }
    Write-Ok "cargo-packager: $($packagerPath.Source)"
}

# ============================================================================
# 3. 清理 dist 目录
# ============================================================================
if ($Clean -and (Test-Path $DistDir)) {
    Write-Step "清空 dist 目录: $DistDir"
    Remove-Item -Recurse -Force $DistDir
    Write-Ok 'dist 目录已清空'
}
if (-not (Test-Path $DistDir)) {
    New-Item -ItemType Directory -Path $DistDir -Force | Out-Null
}

# ============================================================================
# 4. 生成 NSIS 安装包（exe）
# ============================================================================
if (-not $SkipBuild) {
    Write-Step '调用 cargo packager 生成 NSIS 安装包（含 cargo build --release）'
    & cargo packager --release
    if ($LASTEXITCODE -ne 0) { Die 'cargo packager 执行失败' }
    Write-Ok 'NSIS 安装包生成完毕'
} else {
    Write-Warn '已跳过 NSIS 构建（-SkipBuild）'
}

# ============================================================================
# 5. 生成 zip 免安装版
# ============================================================================
Write-Step '生成 zip 免安装版'

$ReleaseExe = Join-Path $ReleaseDir $BinaryName
if (-not (Test-Path $ReleaseExe)) {
    Die "未找到 release 二进制: $ReleaseExe`n请先运行 cargo build --release 或去掉 -SkipBuild 参数"
}

$TempStage = Join-Path $env:TEMP "astrabrew-launcher-stage-$([System.Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $TempStage -Force | Out-Null

try {
    $PortableExeName = "$ProductName.exe"
    $PortableExePath = Join-Path $TempStage $PortableExeName
    Copy-Item -Path $ReleaseExe -Destination $PortableExePath -Force
    Write-Ok "已暂存: $PortableExeName ($('{0:N2}' -f ((Get-Item $PortableExePath).Length / 1MB)) MB)"

    $ZipName = "$ProductName`_$Version`_x64_portable.zip"
    $ZipPath = Join-Path $DistDir $ZipName
    if (Test-Path $ZipPath) { Remove-Item -Force $ZipPath }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $CompressionLevel = [System.IO.Compression.CompressionLevel]::Optimal
    [System.IO.Compression.ZipFile]::CreateFromDirectory($TempStage, $ZipPath, $CompressionLevel, $false)
    Write-Ok "zip 免安装版: $ZipName ($('{0:N2}' -f ((Get-Item $ZipPath).Length / 1MB)) MB)"
} finally {
    if (Test-Path $TempStage) { Remove-Item -Recurse -Force $TempStage -ErrorAction SilentlyContinue }
}

# ============================================================================
# 6. 列出 dist 产物
# ============================================================================
Write-Step 'dist 目录产物清单'
if (Test-Path $DistDir) {
    Get-ChildItem -Path $DistDir -File | Sort-Object Name | ForEach-Object {
        $sizeMB = '{0:N2}' -f ($_.Length / 1MB)
        Write-Host ("  {0,-50}  {1,10} MB" -f $_.Name, $sizeMB) -ForegroundColor White
    }
}

Write-Host "`n========================================" -ForegroundColor Green
Write-Host " 构建打包完成 - 版本 $Version" -ForegroundColor Green
Write-Host "========================================`n" -ForegroundColor Green
