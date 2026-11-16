# 生产包构建：一次产出「免安装 zip + msi + setup.exe」三件套。
#
# 用法（在项目根目录）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\package.ps1 -Configuration Debug
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\package.ps1 -Configuration Release
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\package.ps1 -Configuration Beta -Platform arm64
#
# 产物落在 dist\<配置名>\build\ 下（例如 dist\Debug\build\），每次打包前该目录会被清空：
#   AstraBrewLauncher-<版本>-win-<架构>.zip          免安装包（解压即用，更新版本用的就是它）
#   AstraBrewLauncher-<版本>-win-<架构>.msi          MSI 安装包（per-user，免管理员）
#   AstraBrewLauncher-<版本>-win-<架构>-setup.exe    MSI 外面那层引导程序，双击装
#   SHA256SUMS.txt / build-info.json                 校验与元数据（给更新源用）
#
# 前置：本机的 dotnet 8 SDK；WiX 走仓库里的本地工具（.config\dotnet-tools.json），
# 首次运行会自动 dotnet tool restore；打包机上还要能联网装一次 Burn 扩展。

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Alpha', 'Beta', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('x86', 'x64', 'arm64')]
    [string]$Platform = 'x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# Windows PowerShell 5.1 默认按控制台代码页（本机是 936）转码子进程输出，
# 转到 UTF-8 后 dotnet / wix 的提示才不会变成乱码，正则匹配也才对得上。
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$project = 'AstraBrew Launcher.csproj'
$rid = "win-$Platform"
$workDir = Join-Path $root "obj\package\$Configuration"
$publishDir = Join-Path $workDir 'publish'
$stageDir = Join-Path $workDir 'stage'
$outDir = Join-Path $root "dist\$Configuration\build"

function Step([string]$text) {
    Write-Host ""
    Write-Host "==> $text" -ForegroundColor Cyan
}

# ---------------------------------------------------------------- 0. 前置检查

# 应用在跑的时候输出目录是锁着的，PRI 生成会以 PRI210 失败，报错信息还看不出是这个原因
$running = Get-Process -Name 'AstraBrew Launcher' -ErrorAction SilentlyContinue
if ($running) {
    $pids = ($running | ForEach-Object { $_.Id }) -join ', '
    throw "AstraBrew Launcher 正在运行（PID $pids）。先关掉它再打包，否则输出目录被占用会报 PRI210。"
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { throw '找不到 dotnet，请先安装 .NET 8 SDK。' }

Step "还原本地打包工具（WiX）"
& dotnet tool restore | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore 失败。' }

# Burn 扩展（setup.exe 那层外壳）不在 WiX 核心里，按当前 WiX 版本补齐
# 不要写成 `... | Select-Object -First 1`：管道被提前掐断会让 $LASTEXITCODE 失真，
# 正常版本号也会被当成失败。
$wixVersionRaw = & dotnet wix --version 2>&1
if ($LASTEXITCODE -ne 0) { throw "WiX 不可用：$wixVersionRaw" }
$wixVersion = @($wixVersionRaw)[0]
$wixSemver = ($wixVersion -split '\+')[0].Trim()

$extensions = & dotnet wix extension list -g 2>&1
if ($extensions -notmatch 'BootstrapperApplications') {
    Step "安装 Burn 扩展 WixToolset.BootstrapperApplications.wixext/$wixSemver"
    & dotnet wix extension add -g "WixToolset.BootstrapperApplications.wixext/$wixSemver"
    if ($LASTEXITCODE -ne 0) { throw 'Burn 扩展安装失败（打包机需要能访问 nuget.org）。' }
}

# ---------------------------------------------------------------- 1. 清空目标位置

Step "清空 dist\$Configuration\build"
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

# ---------------------------------------------------------------- 2. 发布

Step "发布 $Configuration · $rid"
# 分发用的包一律自包含：不要求用户先装 .NET 运行时或 Windows App Runtime。
# 裁剪（PublishTrimmed）在这条链路上关掉：WinUI 的资源加载走反射，裁完会在启动时找不到资源。
& dotnet publish $project -c $Configuration -p:Platform=$Platform -r $rid `
    --self-contained true -p:WindowsAppSDKSelfContained=true -p:PublishTrimmed=false `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败。' }

# ---------------------------------------------------------------- 3. 版本号与命名

$exeName = 'AstraBrew Launcher.exe'
if (-not (Test-Path (Join-Path $publishDir $exeName))) { throw "发布目录里没有 $exeName。" }

# ProductVersion 就是 csproj 的 VersionPrefix + 渠道后缀，末尾的 +<git sha> 不要
$productVersion = (Get-Item (Join-Path $publishDir 'AstraBrew Launcher.dll')).VersionInfo.ProductVersion
$version = ($productVersion -split '\+')[0].Trim()
if (-not $version) { throw '读不到版本号。' }

# MSI 只接受纯数字 a.b.c；Burn 要四段
$msiVersion = ($version -split '-')[0]
$segments = $msiVersion.Split('.')
while ($segments.Count -lt 3) { $segments += '0' }
$msiVersion = ($segments[0..2] -join '.')
$bundleVersion = "$msiVersion.0"

$channelLabel = @{ Debug = '开发版'; Alpha = '预览版'; Beta = '测试版' }[$Configuration]
if ($channelLabel) {
    $displayName = "AstraBrew Launcher ($channelLabel)"
} else {
    $displayName = 'AstraBrew Launcher'
}

$baseName = "AstraBrewLauncher-$version-$rid"
Write-Host "版本 $version（MSI $msiVersion / Bundle $bundleVersion）  名称 $baseName" -ForegroundColor DarkGray

# ---------------------------------------------------------------- 4. 组装待打包目录

Step '组装待打包文件'
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

# 用 robocopy 而不是 Copy-Item：发布目录里有几十个同名文件散在不同语言子目录，
# 通配符复制会拿目录去盖同名文件；robocopy 只合并目录，且能顺手排除 pdb。
# 只有开发版带 pdb（给测试同学看堆栈），其余渠道的符号文件不进包。
$copyArgs = @($publishDir, $stageDir, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
if ($Configuration -ne 'Debug') { $copyArgs += @('/XF', '*.pdb') }
& robocopy @copyArgs | Out-Null
# robocopy 的 0-7 都是成功（1 = 复制了文件，2/4/6 是多余项/不匹配），8 以上才是失败
if ($LASTEXITCODE -ge 8) { throw "复制发布目录失败（robocopy 退出码 $LASTEXITCODE）。" }

# Apache-2.0 要求随分发附带许可证原文
Copy-Item (Join-Path $root 'LICENSE.txt') $stageDir -Force

$fileCount = (Get-ChildItem $stageDir -Recurse -File).Count

# ---------------------------------------------------------------- 5. 免安装 zip

Step '打包 zip（免安装）'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = Join-Path $outDir "$baseName.zip"
# includeBaseDirectory = false：解压出来直接是 exe，更新时整目录覆盖即可
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stageDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

# 压缩根目录写错的话，更新包解压出来会多套一层文件夹，覆盖时对不上——这里当场验一下
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    if (-not ($archive.Entries | Where-Object { $_.FullName -eq $exeName })) {
        throw "zip 根目录里没有 $exeName，压缩层级不对。"
    }
} finally {
    $archive.Dispose()
}

# ---------------------------------------------------------------- 6. MSI

Step '构建 msi'
$msiPath = Join-Path $outDir "$baseName.msi"
$wixArgs = @(
    'wix', 'build', (Join-Path $root 'installer\Package.wxs'),
    '-arch', $Platform,
    '-pdbtype', 'none',
    '-d', "PublishDir=$stageDir",
    '-d', "ProductVersion=$msiVersion",
    '-d', "DisplayName=$displayName",
    '-d', "IconPath=$(Join-Path $root 'Assets\app.ico')",
    '-o', $msiPath
)
& dotnet @wixArgs
if ($LASTEXITCODE -ne 0) { throw 'MSI 构建失败。' }

# ---------------------------------------------------------------- 7. setup.exe

Step '构建 setup.exe（Burn 引导程序）'
$setupPath = Join-Path $outDir "$baseName-setup.exe"
$bundleArgs = @(
    'wix', 'build', (Join-Path $root 'installer\Bundle.wxs'),
    '-ext', 'WixToolset.BootstrapperApplications.wixext',
    '-arch', $Platform,
    '-pdbtype', 'none',
    '-d', "MsiPath=$msiPath",
    '-d', "BundleVersion=$bundleVersion",
    '-d', "DisplayName=$displayName",
    '-d', "IconPath=$(Join-Path $root 'Assets\app.ico')",
    '-o', $setupPath
)
& dotnet @bundleArgs
if ($LASTEXITCODE -ne 0) { throw 'setup.exe 构建失败。' }

# ---------------------------------------------------------------- 8. 校验与元数据

Step '写校验与元数据'
$packages = Get-ChildItem $outDir -File | Where-Object { $_.Extension -in '.zip', '.msi', '.exe' } | Sort-Object Name

$sums = foreach ($file in $packages) {
    $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLower()
    "{0}  {1}" -f $hash, $file.Name
}
Set-Content -Path (Join-Path $outDir 'SHA256SUMS.txt') -Value $sums -Encoding ASCII

$commit = & git rev-parse --short HEAD 2>$null
$info = [ordered]@{
    product   = 'AstraBrew Launcher'
    version   = $version
    channel   = $Configuration
    rid       = $rid
    commit    = "$commit".Trim()
    builtAt   = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    fileCount = $fileCount
    packages  = @(
        foreach ($file in $packages) {
            [ordered]@{
                name   = $file.Name
                bytes  = $file.Length
                sha256 = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLower()
            }
        }
    )
}
# 不带 BOM 的 UTF-8：更新源那边直接 JSON.parse，BOM 会让部分解析器当场报错
$json = $info | ConvertTo-Json -Depth 5
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $outDir 'build-info.json'), $json, $utf8NoBom)

# ---------------------------------------------------------------- 9. 收尾

Step '完成'
foreach ($file in (Get-ChildItem $outDir -File | Sort-Object Name)) {
    $mb = '{0,7:N1} MB' -f ($file.Length / 1MB)
    Write-Host ("  {0}  {1}" -f $mb, $file.Name)
}
Write-Host ""
Write-Host "输出目录：$outDir" -ForegroundColor Green
