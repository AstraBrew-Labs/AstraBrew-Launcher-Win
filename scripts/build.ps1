<#
.SYNOPSIS
    AstraBrew Launcher Windows 构建打包脚本
.DESCRIPTION
    完整构建流程：
      1. 检查并安装 cargo-packager CLI（如未安装）
      2. 调用 cargo packager --release 生成 NSIS 安装包（exe）
      3. 将 release 二进制重命名后打包为 zip 免安装版
      4. 统一输出到项目根目录的 dist/ 目录
    字体与窗口图标已通过 include_bytes! 内嵌，产物完全自包含。
.PARAMETER SkipBuild
    跳过 cargo packager 步骤（假设 dist 已有 exe），仅生成 zip 免安装版。
    用于调试 zip 打包流程。
.PARAMETER Clean
    打包前清空 dist 目录。
.PARAMETER Version
    覆盖版本号（默认从 Cargo.toml 读取）。
.PARAMETER Beta
    构建测试版：版本号追加 -beta 后缀 + UI 渲染 BETA 角标。
.PARAMETER Release
    构建正式版（默认）：版本号原样 + 无 BETA 角标。
.EXAMPLE
    .\scripts\build.ps1
    完整构建打包（正式版）
.EXAMPLE
    .\scripts\build.ps1 -Beta
    构建测试版（版本号加 -beta，UI 显示 BETA 角标）
.EXAMPLE
    .\scripts\build.ps1 -Clean
    清空 dist 后完整构建打包
.EXAMPLE
    .\scripts\build.ps1 -SkipBuild
    跳过 NSIS 构建，仅生成 zip 免安装版（要求 target/release 已有 exe）
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
$ProgressPreference = 'SilentlyContinue'  # 加速 Invoke-WebRequest

# ============================================================================
# 路径与常量
# ============================================================================
$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Split-Path -Parent $ScriptRoot
$DistDir = Join-Path $ProjectRoot 'dist'
$ReleaseDir = Join-Path $ProjectRoot 'target\release'
$CargoToml = Join-Path $ProjectRoot 'Cargo.toml'

$ProductName = 'AstraBrew Launcher'
$BinaryName = 'astrabrew-launcher-win.exe'  # cargo 默认产物名（package name）

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
    # 同时匹配 [package] 段下的 version 与 [package.metadata.packager] 下的 version
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

# cargo
$cargoPath = (Get-Command cargo -ErrorAction SilentlyContinue)
if (-not $cargoPath) { Die '未找到 cargo，请先安装 Rust 工具链' }
Write-Ok "cargo: $($cargoPath.Source)"

# cargo-packager（仅在不跳过构建时需要）
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

# 临时目录：把 exe 重命名为产品名后打包
$TempStage = Join-Path $env:TEMP "astrabrew-launcher-stage-$([System.Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $TempStage -Force | Out-Null

try {
    $PortableExeName = "$ProductName.exe"
    $PortableExePath = Join-Path $TempStage $PortableExeName
    Copy-Item -Path $ReleaseExe -Destination $PortableExePath -Force
    Write-Ok "已暂存: $PortableExeName ($('{0:N2}' -f ((Get-Item $PortableExePath).Length / 1MB)) MB)"

    # 输出 zip 路径
    $ZipName = "$ProductName`_$Version`_x64_portable.zip"
    $ZipPath = Join-Path $DistDir $ZipName

    # 若已存在则先删除（Compress-Archive 不支持覆盖）
    if (Test-Path $ZipPath) { Remove-Item -Force $ZipPath }

    # 使用 .NET ZipFile 保证压缩级别可控 + 跨 PowerShell 版本一致
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
