# 文案资源自检：中英 key 一致性、占位符一致性、代码/XAML 里引用但资源里没有的 key。
#
# 用法（在项目根目录）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-strings.ps1
#
# 退出码：0 = 全部通过；1 = 有问题（输出里逐条列出）。

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Read-Resw([string]$path) {
    if (-not (Test-Path $path)) { throw "缺少资源文件：$path" }
    $doc = [xml](Get-Content $path -Raw -Encoding UTF8)
    $map = @{}
    foreach ($node in $doc.root.data) {
        $map[$node.name] = [string]$node.value
    }
    return $map
}

$doc = [xml](Get-Content 'Strings\zh-CN\Resources.resw' -Raw -Encoding UTF8)
$zh = Read-Resw 'Strings\zh-CN\Resources.resw'
$en = Read-Resw 'Strings\en-US\Resources.resw'

$problems = 0
function Fail([string]$message) {
    Write-Host "  ✗ $message" -ForegroundColor Red
    $script:problems++
}

Write-Host "资源条目：zh-CN $($zh.Count) 条 / en-US $($en.Count) 条"

Write-Host "`n[1] 两份资源的 key 是否一致"
foreach ($key in $zh.Keys) { if (-not $en.ContainsKey($key)) { Fail "en-US 缺：$key" } }
foreach ($key in $en.Keys) { if (-not $zh.ContainsKey($key)) { Fail "zh-CN 缺：$key" } }

Write-Host "`n[2] {0}/{1} 占位符是否两边一致"
foreach ($key in $zh.Keys) {
    if (-not $en.ContainsKey($key)) { continue }
    # 只比「用了哪几个占位符」，不比顺序：中英文语序本来就不同，
    # 「从 {0} 拉取 {1}」译成「pulling {1} from {0}」是正确的，不是错误。
    $a = ([regex]::Matches($zh[$key], '\{\d+\}') | ForEach-Object { $_.Value } | Sort-Object) -join ','
    $b = ([regex]::Matches($en[$key], '\{\d+\}') | ForEach-Object { $_.Value } | Sort-Object) -join ','
    if ($a -ne $b) { Fail "$key 占位符不一致（zh: [$a] / en: [$b]）" }
}

Write-Host "`n[2b] key 结构：重复条目 / 前缀冲突"
$names = @($doc.root.data | ForEach-Object { $_.name })
$dupes = $names | Group-Object | Where-Object { $_.Count -gt 1 }
foreach ($d in $dupes) { Fail "同名 key 出现 $($d.Count) 次：$($d.Name)" }

# MRT 把点当层级：A 与 A.x 不能共存，否则 PRI278「既是资源又是范围」
$set = [System.Collections.Generic.HashSet[string]]::new([string[]]$names)
foreach ($name in $names) {
    $prefix = $name
    $cut = $prefix.LastIndexOf('.')
    while ($cut -gt 0) {
        $prefix = $prefix.Substring(0, $cut)
        if ($set.Contains($prefix)) {
            Fail "前缀冲突：$prefix 与 $name 不能共存（给前者加 .Label 之类的后缀）"
        }
        $cut = $prefix.LastIndexOf('.')
    }
}

Write-Host "`n[3] 代码与 XAML 引用的 key 是否都存在"
$referenced = @{}
Get-ChildItem -Recurse -Include *.cs, *.xaml |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
    ForEach-Object {
        $text = Get-Content $_.FullName -Raw -Encoding UTF8
        foreach ($m in [regex]::Matches($text, 'Loc\.(?:Text|Content|Placeholder|ToolTip|Property|Name)="([^"]+)"')) {
            $value = $m.Groups[1].Value
            # 通用入口写成「属性名:资源键」
            $referenced[$value.Split(':')[-1]] = $_.Name
        }
        foreach ($m in [regex]::Matches($text, 'L\.(?:Get|GetOrEmpty|F)\("([^"]+)"')) {
            $referenced[$m.Groups[1].Value] = $_.Name
        }
    }

foreach ($key in $referenced.Keys | Sort-Object) {
    if (-not $zh.ContainsKey($key)) { Fail "资源里没有：$key（引用于 $($referenced[$key])）" }
}

Write-Host "`n[4] 长度告警（中文 > 22 字且不是按钮类）"
foreach ($key in $zh.Keys) {
    if ($key -match '\.Content$|\.Text$' -and $zh[$key].Length -gt 14) {
        Write-Host "  ! $key 长度 $($zh[$key].Length)：$($zh[$key])" -ForegroundColor Yellow
    }
    elseif ($zh[$key].Length -gt 40) {
        Write-Host "  ! $key 长度 $($zh[$key].Length)：$($zh[$key].Substring(0, 20))…" -ForegroundColor Yellow
    }
}

Write-Host "`n[5] 英文资源里不该出现中文（语言名除外）"
# 语言选项本身要用母语写（简体中文 / English），这是唯一合理的例外。
# 其它任何一条英文里带汉字，都说明翻译没跟上——界面上会直接露馅。
$allowChinese = @('Settings.Language.Option.1')
foreach ($key in $en.Keys | Sort-Object) {
    if ($allowChinese -contains $key) { continue }
    if ($en[$key] -match '[\u4e00-\u9fff]') {
        Fail "英文资源含中文：$key = $($en[$key])"
    }
}

if ($problems -eq 0) {
    Write-Host "`n全部通过。" -ForegroundColor Green
    exit 0
}

Write-Host "`n共 $problems 处问题。" -ForegroundColor Red
exit 1
