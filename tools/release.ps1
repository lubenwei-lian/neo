<#
.SYNOPSIS
  neo 电脑版一键发布：publish.ps1 -> iscc -> 安装包 .sha256 -> 复跑断言 -> 打印 gh 命令模板（不上传）。

.DESCRIPTION
  固定顺序（跳步会打出半成品，别跳）：
    1) tools\publish.ps1               构建 dist\publish + dist\build-info.json（含 git / 内核版本）
    2) iscc installer\neo.iss          生成 dist\installer\neo-setup-<ver>.exe
    3) 安装包同名 .sha256，并把该行追加到 dist\publish\SHA256SUMS.txt
    4) tools\publish.ps1 -ValidateOnly 复跑完整性/版本断言（会保留第 3 步追加的安装包行）
    5) 打印 gh release create 模板（只打印，绝不执行上传）

.PARAMETER SkipBuild
  跳过第 1 步，复用现有 dist\publish（例如刚跑过 publish.ps1）。
.PARAMETER Tag
  release tag，默认 desktop-<版本号>；仅用于打印 gh 命令模板。
.PARAMETER DryRun
  干跑：只打印将执行的命令与当前产物状态；不构建、不跑 iscc、不写任何文件。
.PARAMETER Iscc
  指定 ISCC.exe；默认依次取 -Iscc、环境变量 NEO_ISCC、注册表里的 Inno Setup 安装位置、常见路径。

.EXAMPLE
  pwsh -NoProfile -File tools\release.ps1
.EXAMPLE
  pwsh -NoProfile -File tools\release.ps1 -SkipBuild -Tag desktop-0.2.5
.EXAMPLE
  pwsh -NoProfile -File tools\release.ps1 -DryRun
#>
param(
    [switch]$SkipBuild,
    [string]$Tag = '',
    [switch]$DryRun,
    [string]$Iscc = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$nl = [string][char]10
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Step([string]$msg) { Write-Host "[neo-release] $msg" }
function Fail([string]$msg) { throw "[neo-release] $msg" }

function Get-ReleaseVersion {
    $p = Join-Path $root 'installer\version.iss'
    if (-not (Test-Path -LiteralPath $p)) { Fail "找不到 $p（版本唯一来源）" }
    $m = [regex]::Match([System.IO.File]::ReadAllText($p), '#define\s+MyAppVersion\s+"([0-9]+\.[0-9]+\.[0-9]+)"')
    if (-not $m.Success) { Fail 'installer\version.iss 里没找到 #define MyAppVersion "x.y.z"' }
    return $m.Groups[1].Value
}

function Find-Iscc {
    $cands = New-Object System.Collections.Generic.List[string]
    if ($Iscc) { $cands.Add($Iscc) }
    if ($env:NEO_ISCC) { $cands.Add($env:NEO_ISCC) }
    $bases = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
    )
    foreach ($base in $bases) {
        foreach ($k in @(Get-ChildItem $base -ErrorAction SilentlyContinue)) {
            $p = Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue
            if ($p -and $p.DisplayName -like '*Inno Setup*' -and $p.InstallLocation) {
                $cands.Add((Join-Path $p.InstallLocation 'ISCC.exe'))
            }
        }
    }
    $cands.Add((Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'))
    $cands.Add('C:\Program Files (x86)\Inno Setup 6\ISCC.exe')
    $cands.Add('C:\Program Files\Inno Setup 6\ISCC.exe')
    foreach ($c in $cands) {
        if ($c -and (Test-Path -LiteralPath $c)) { return (Get-Item -LiteralPath $c).FullName }
    }
    return $null
}

$ver = Get-ReleaseVersion
if (-not $Tag) { $Tag = "v$ver" }
$publishScript = Join-Path $PSScriptRoot 'publish.ps1'
$issPath = Join-Path $root 'installer\neo.iss'
$outDir = Join-Path $root 'dist\publish'
$installerDir = Join-Path $root 'dist\installer'
$setupName = "neo-setup-$ver.exe"
$setupPath = Join-Path $installerDir $setupName
$shaPath = $setupPath + '.sha256'
$sumPath = Join-Path $outDir 'SHA256SUMS.txt'
$buildInfoPath = Join-Path $root 'dist\build-info.json'

Step ("version=" + $ver + "   tag=" + $Tag + "   dryRun=" + $DryRun)

# ---- 1) publish ----
if ($SkipBuild) {
    Step 'step 1: -SkipBuild，复用现有 dist\publish（要求 build-info.json 是新鲜的）'
} elseif ($DryRun) {
    Step 'step 1: [DryRun] 将运行 pwsh -NoProfile -File tools\publish.ps1'
} else {
    Step 'step 1: tools\publish.ps1（构建 dist\publish + dist\build-info.json）'
    & $publishScript
}
foreach ($p in @((Join-Path $outDir 'neo.exe'), $buildInfoPath)) {
    if (-not (Test-Path -LiteralPath $p)) {
        if ($DryRun) { Step ("[DryRun] 预期产物当前尚不存在：" + $p) }
        else { Fail ("缺少 " + $p + "（先完整运行 tools\publish.ps1，不要加 -SkipBuild）") }
    }
}

# ---- 2) iscc ----
$isccExe = Find-Iscc
if (-not $isccExe) {
    if ($DryRun) { Step 'step 2: [DryRun] 未找到 ISCC.exe（真实运行会中止；可用 -Iscc 指定）' }
    else { Fail '找不到 ISCC.exe：用 -Iscc 指定，或设 NEO_ISCC 环境变量，或安装 Inno Setup 6' }
}
if ($DryRun) {
    Step ("step 2: [DryRun] 将运行 " + $isccExe + " " + $issPath)
} else {
    Step ("step 2: iscc " + $issPath)
    & $isccExe $issPath
    if ($LASTEXITCODE -ne 0) { Fail ("iscc 失败，退出码 " + $LASTEXITCODE) }
    if (-not (Test-Path -LiteralPath $setupPath)) { Fail ("安装包未生成：" + $setupPath) }
    $setupSize = (Get-Item -LiteralPath $setupPath).Length
    if ($setupSize -lt 40MB) { Fail ("安装包体积异常：" + $setupSize + " B（应 > 40MB）") }
}

# ---- 3) 安装包 sha256 + 追加 SHA256SUMS ----
if ($DryRun) {
    Step ("step 3: [DryRun] 将生成 " + $shaPath + "，并把该行追加进 " + $sumPath)
} else {
    $setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash
    [System.IO.File]::WriteAllText($shaPath, ($setupHash + '  ' + $setupName + $nl), $utf8)
    Step ("step 3: 已写 " + $shaPath)
    $relPath = '../installer/' + $setupName
    $kept = @()
    if (Test-Path -LiteralPath $sumPath) {
        foreach ($line in @(Get-Content -LiteralPath $sumPath)) {
            $t = $line.Trim()
            if ($t.Length -eq 0) { continue }
            $parts = $t -split '\s+', 2
            if ($parts.Count -lt 2) { Fail ("SHA256SUMS.txt 行格式错误：" + $t) }
            if ($parts[1] -ne $relPath) { $kept += $t }
        }
    }
    $kept += ($setupHash + '  ' + $relPath)
    [System.IO.File]::WriteAllText($sumPath, (($kept -join $nl) + $nl), $utf8)
    Step ("step 3: 已把安装包行追加到 " + $sumPath)
}

# ---- 4) 复跑 step9 断言 ----
if ($DryRun) {
    Step 'step 4: [DryRun] 将运行 pwsh -NoProfile -File tools\publish.ps1 -ValidateOnly'
} else {
    Step 'step 4: tools\publish.ps1 -ValidateOnly（复跑完整性/版本断言，保留安装包行）'
    & $publishScript -ValidateOnly
    $sumText = [System.IO.File]::ReadAllText($sumPath)
    if ($sumText -notmatch [regex]::Escape($setupName)) { Fail 'SHA256SUMS.txt 里丢失了安装包行（校验步骤把它冲掉了）' }
    Step 'step 4: 断言通过'
}

# ---- 5) 汇总 + gh 模板 ----
Step ''
Step '===== 产物（实际路径 / 大小 / sha256） ====='
foreach ($p in @($setupPath, $shaPath, $buildInfoPath)) {
    if (Test-Path -LiteralPath $p) {
        $fi = Get-Item -LiteralPath $p
        Step ("  " + $fi.FullName)
        Step ("    size=" + $fi.Length + "  sha256=" + (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash)
    } elseif ($DryRun) {
        Step ("  [DryRun] " + $p + " 尚不存在")
    } else {
        Fail ("缺少产物：" + $p)
    }
}

Step ''
Step '===== 发布时必须上传的 3 个资产 ====='
Step ("  1) " + $setupName + "  （安装包本体）")
Step ("  2) " + $setupName + ".sha256  （安装包 SHA-256）")
Step '  3) build-info.json  （应用用它判断「同版本号换资产后的新构建」）'
Step '  可选：dist\publish\SHA256SUMS.txt（含安装包行，供人工/CI 校验）'

Step ''
Step '===== gh release create 命令模板（仅打印，不执行） ====='
$ghCmd = ('gh release create {0} --repo lubenwei-lian/neo --title "neo Windows {1}" --notes-file <发布说明.md> "dist\installer\{2}" "dist\installer\{2}.sha256" "dist\build-info.json"' -f $Tag, $ver, $setupName)
Step $ghCmd
Step ''
Step '确认无误后自行执行上面的 gh 命令；本脚本不会上传任何东西。'

exit 0
