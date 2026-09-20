<#
.SYNOPSIS
    AstraBrew Launcher（Windows）构建打包脚本。

.DESCRIPTION
    无需 Git Bash / WSL，原生 PowerShell 即可运行。

    构建渠道由 Cargo.toml 里的 `[package.metadata.astrabrew] beta` 决定，
    也可用 -Beta / -Release 显式覆盖：
      beta = true  → 测试版（界面显示「测试版」标记，tag 用 `beta-v{版本}`）
      beta = false → 正式版（无标记，tag 用 `v{版本}`）

    渠道决定两件事：
      1. 二进制里注入的 `ASTRA_BUILD_CHANNEL`（决定界面左上角是否显示「测试版」标记）；
      2. 产物文件名中的版本号后缀。

    产物（dist/）：
      AstraBrew Launcher_<版本>_x64-setup.exe      NSIS 安装包
      AstraBrew Launcher_<版本>_x64_portable.zip   免安装压缩包

.EXAMPLE
    .\build.ps1                     按 Cargo.toml 渠道构建打包
    .\build.ps1 -Beta -Clean        清空 dist 后构建测试版
    .\build.ps1 -SkipBuild          跳过 NSIS，仅生成 zip 免安装版

.PARAMETER SkipBuild
    跳过 cargo packager 步骤（假设 release 二进制已存在），仅生成 zip 免安装版。
.PARAMETER Clean
    打包前清空 dist 目录。
.PARAMETER Beta
    强制构建测试版。
.PARAMETER Release
    强制构建正式版。
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
# 路径与常量（脚本位于项目根目录）
# ============================================================================
$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$DistDir = Join-Path $ProjectRoot 'dist'
$ReleaseDir = Join-Path $ProjectRoot 'target\release'
$CargoToml = Join-Path $ProjectRoot 'Cargo.toml'

$ProductName = 'AstraBrew Launcher'
$BinaryName = 'astrabrew-launcher-win.exe'

Set-Location $ProjectRoot

function Write-Step([string] $msg) { Write-Host "`n[*] $msg" -ForegroundColor Cyan }
function Write-Ok([string] $msg) { Write-Host "[OK] $msg" -ForegroundColor Green }
function Write-Warn([string] $msg) { Write-Host "[!]  $msg" -ForegroundColor Yellow }
function Die([string] $msg) { Write-Host "[X]  $msg" -ForegroundColor Red; exit 1 }

if (-not (Test-Path $CargoToml)) { Die "找不到 Cargo.toml: $CargoToml" }
$cargoContent = Get-Content $CargoToml -Raw

# ============================================================================
# 1. 读取版本号
# ============================================================================
Write-Step '读取版本号'
if (-not $Version) {
    $pkgMatch = [regex]::Match($cargoContent, '(?ms)^\[package\][^\[]*?^version\s*=\s*"([^"]+)"')
    if (-not $pkgMatch.Success) { Die '无法从 Cargo.toml 解析 [package].version' }
    $Version = $pkgMatch.Groups[1].Value
}
$Version = $Version.Trim()
Write-Ok "版本号: $Version"

# ============================================================================
# 2. 判定构建渠道
# ============================================================================
# 显式参数优先；否则读 `[package.metadata.astrabrew] beta`。
$isBeta = $Beta.IsPresent
if (-not $Beta -and -not $Release) {
    $betaRaw = [regex]::Match(
        $cargoContent,
        '(?ms)^\[package\.metadata\.astrabrew\][^\[]*?^\s*beta\s*=\s*([^\r\n#]+)'
    ).Groups[1].Value.Trim().ToLowerInvariant()
    $isBeta = $betaRaw -eq 'true'
}
if ($Release.IsPresent) { $isBeta = $false }

if ($isBeta) {
    $env:ASTRA_BUILD_CHANNEL = 'beta'
    Write-Ok '构建渠道: Beta（界面显示「测试版」标记）'
} else {
    $env:ASTRA_BUILD_CHANNEL = 'release'
    Write-Ok '构建渠道: Release（正式版，无标记）'
}

# ============================================================================
# 3. 检查工具链
# ============================================================================
Write-Step '检查工具链'

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    Die '未找到 cargo，请先安装 Rust 工具链'
}
Write-Ok "cargo: $((Get-Command cargo).Source)"

if (-not $SkipBuild) {
    if (-not (Get-Command cargo-packager -ErrorAction SilentlyContinue)) {
        Write-Warn '未安装 cargo-packager，开始安装（cargo install cargo-packager --locked）...'
        & cargo install cargo-packager --locked
        if ($LASTEXITCODE -ne 0) { Die 'cargo-packager 安装失败' }
    }
    Write-Ok "cargo-packager: $((Get-Command cargo-packager).Source)"

    # NSIS 是 cargo-packager 生成 .exe 安装包的前提。
    if (-not (Get-Command makensis -ErrorAction SilentlyContinue)) {
        Write-Warn '未找到 makensis（NSIS）。请先安装：choco install nsis -y'
    }
}

# ============================================================================
# 4. 清理 dist 目录
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
# 5. 生成 NSIS 安装包（exe）
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
# 6. 生成 zip 免安装版
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
    $sizeMB = '{0:N2}' -f ((Get-Item $PortableExePath).Length / 1MB)
    Write-Ok "已暂存: $PortableExeName ($sizeMB MB)"

    $suffix = if ($isBeta) { '-beta' } else { '' }
    $ZipName = "$ProductName`_$Version$suffix`_x64_portable.zip"
    $ZipPath = Join-Path $DistDir $ZipName
    if (Test-Path $ZipPath) { Remove-Item -Force $ZipPath }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $compression = [System.IO.Compression.CompressionLevel]::Optimal
    [System.IO.Compression.ZipFile]::CreateFromDirectory($TempStage, $ZipPath, $compression, $false)
    $zipMB = '{0:N2}' -f ((Get-Item $ZipPath).Length / 1MB)
    Write-Ok "zip 免安装版: $ZipName ($zipMB MB)"
} finally {
    if (Test-Path $TempStage) { Remove-Item -Recurse -Force $TempStage -ErrorAction SilentlyContinue }
}

# ============================================================================
# 7. 列出 dist 产物
# ============================================================================
Write-Step 'dist 目录产物清单'
if (Test-Path $DistDir) {
    Get-ChildItem -Path $DistDir -File | Sort-Object Name | ForEach-Object {
        $sizeMB = '{0:N2}' -f ($_.Length / 1MB)
        Write-Host ("  {0,-52} {1,10} MB" -f $_.Name, $sizeMB) -ForegroundColor White
    }
}

Write-Host "`n========================================" -ForegroundColor Green
Write-Host " 构建打包完成 - 版本 $Version" -ForegroundColor Green
Write-Host "========================================`n" -ForegroundColor Green
