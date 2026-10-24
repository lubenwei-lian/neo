<#
.SYNOPSIS
    neo 安装包验收脚本：干净目录静默安装 → 校验内容/默认值/最大化 → 升级保留性 → 卸载。
.DESCRIPTION
    对应「装机验证」：安装包不能只在开发机上跑 publish 验证。本脚本做四件事：
      1) /VERYSILENT 装到 _work 下的临时目录（不碰用户正在用的安装）
      2) 校验关键文件（xray/sing-box/分流数据存在；mihomo 不随包安装）
      3) 启动安装后的程序，检查窗口是否最大化（IsZoomed）
      4) 升级保留性：geoip.dat / 用户自装 mihomo.exe 做标记，重装后必须保留；app\neo.exe 必须被更新
      5) 卸载并清理临时目录
    注意：AppId 固定，装到临时目录会覆盖卸载注册项；脚本结束时用 -LiveDir 重装一次恢复，
          传 -LiveDir '' 可跳过（不推荐）。
.PARAMETER SetupPath
    安装包路径；默认 dist\installer\neo-setup-<version>.exe（version 取自 installer\version.iss）。
.PARAMETER LiveDir
    正在使用的安装目录（默认 E:\Program Files\neo）。
.PARAMETER WorkDir
    临时目录父目录（默认 planning\_work\installer-verify）。
.EXAMPLE
    pwsh -NoProfile -File tools\verify-installer.ps1
#>
[CmdletBinding()]
param(
    [string]$SetupPath = "",
    [string]$LiveDir = "E:\Program Files\neo",
    [string]$WorkDir = ""
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $WorkDir) { $WorkDir = Join-Path $root 'planning\_work\installer-verify' }
$target = Join-Path $WorkDir 'neo'
$script:fail = 0

function Step([string]$msg) { Write-Host "[verify-installer] $msg" }
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "  PASS  $name" }
    else { Write-Host "  FAIL  $name $detail"; $script:fail++ }
}
function Wait-Setup([string]$setup, [string[]]$setupArgs) {
    # 2026-10-24 修复：Start-Process 不会为含空格的参数自动加引号。
    #   "/DIR=E:\Program Files\neo" 会被拆成 "/DIR=E:\Program" + "Files\neo" 两个参数，
    #   安装器于是装到了 E:\Program（实测过）。这里统一给含空格的参数补外层引号。
    $quoted = @($setupArgs | ForEach-Object {
        if ($_ -match '\s' -and -not $_.StartsWith('"')) { '"' + $_ + '"' } else { $_ }
    })
    $p = Start-Process -FilePath $setup -ArgumentList $quoted -PassThru
    if (-not $p.WaitForExit(420000)) { try { $p.Kill() } catch { } ; throw "安装/卸载超时：$setup" }
    return $p.ExitCode
}

if (-not $SetupPath) {
    $ver = (Select-String -Path (Join-Path $root 'installer\version.iss') -Pattern '#define MyAppVersion "(.*)"').Matches[0].Groups[1].Value
    $SetupPath = Join-Path $root "dist\installer\neo-setup-$ver.exe"
}
if (-not (Test-Path $SetupPath)) { throw "找不到安装包：$SetupPath（先跑 tools\release.ps1）" }
Step "setup = $SetupPath"
Step "target = $target"

Get-Process neo -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

# ---- 1) 全新安装 ----
$code = Wait-Setup $SetupPath @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=$target", "/LOG=$(Join-Path $WorkDir 'install-1.log')")
Check '全新安装退出码 0' ($code -eq 0) "exit=$code"

# 安装包 [Run] 会自动拉起程序：先结束，避免干扰文件检查
Start-Sleep -Seconds 2
Get-Process neo -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

$must = @('neo.exe', 'app\neo.exe', 'app\runtime\xray.exe', 'app\runtime\sing-box.exe',
          'app\runtime\neo-cli.exe',
          'app\runtime\geoip.dat', 'app\runtime\geosite.dat', 'app\runtime\Country.mmdb',
          'app\runtime\rule-sets\geosite-cn.srs', 'build-info.json')
foreach ($rel in $must) { Check "存在 $rel" (Test-Path (Join-Path $target $rel)) }
Check '不随包安装 mihomo.exe' (-not (Test-Path (Join-Path $target 'app\runtime\mihomo.exe')))

# ---- 2) 启动后窗口最大化 ----
Start-Process (Join-Path $target 'neo.exe')
Start-Sleep -Seconds 16
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class NEO_VERIFY_WIN {
  [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
}
'@
$proc = Get-Process neo -ErrorAction SilentlyContinue | Sort-Object StartTime -Descending | Select-Object -First 1
Check '安装后可启动' ($null -ne $proc)
if ($proc) { Check '启动默认最大化（IsZoomed）' ([NEO_VERIFY_WIN]::IsZoomed($proc.MainWindowHandle)) }
Get-Process neo -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

# ---- 3) 升级保留性 ----
$geoip = Join-Path $target 'app\runtime\geoip.dat'
Add-Content -Path $geoip -Value 'NEO-UPGRADE-MARKER' -Encoding ASCII
'fake-mihomo' | Set-Content -Path (Join-Path $target 'app\runtime\mihomo.exe') -Encoding ASCII
Set-Content -Path (Join-Path $target 'app\neo.exe') -Value 'stale-build' -Encoding ASCII

$code2 = Wait-Setup $SetupPath @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=$target", "/LOG=$(Join-Path $WorkDir 'install-2.log')")
Check '覆盖安装退出码 0' ($code2 -eq 0) "exit=$code2"
Start-Sleep -Seconds 2
Get-Process neo -ErrorAction SilentlyContinue | Stop-Process -Force

$pubApp = Join-Path $root 'dist\publish\app\neo.exe'
$appUpdated = (Get-FileHash (Join-Path $target 'app\neo.exe') -Algorithm SHA256).Hash -eq (Get-FileHash $pubApp -Algorithm SHA256).Hash
Check '应用本体被更新（app\neo.exe 与发布产物一致）' $appUpdated
Check '分流数据保留（geoip.dat 标记仍在）' ((Get-Content $geoip -Tail 1 -Encoding ASCII) -match 'NEO-UPGRADE-MARKER')
Check '用户自装 mihomo.exe 保留' (Test-Path (Join-Path $target 'app\runtime\mihomo.exe'))

# ---- 4) 卸载 + 清理 ----
$unins = Join-Path $target 'unins000.exe'
if (Test-Path $unins) {
    $code3 = Wait-Setup $unins @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    Check '卸载退出码 0' ($code3 -eq 0) "exit=$code3"
} else { Check '存在卸载器 unins000.exe' $false }
# 卸载器不会删除「用户资产」（app 下自装内核、data 用户数据），这里手动清；
# 偶尔有句柄未释放，清两轮并去掉只读属性，避免误报。
Start-Sleep -Seconds 2
Get-Process neo -ErrorAction SilentlyContinue | Stop-Process -Force
Get-ChildItem $target -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Attributes = 'Normal' } catch { } }
Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path $target) { Start-Sleep -Seconds 3; Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue }
Check '临时目录已清理' (-not (Test-Path $target))

# ---- 5) 恢复「正在用的安装」的卸载注册项 ----
if ($LiveDir -and (Test-Path (Join-Path $LiveDir 'neo.exe'))) {
    Step "恢复正在使用的安装：$LiveDir"
    $code4 = Wait-Setup $SetupPath @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=$LiveDir")
    Check '恢复安装退出码 0' ($code4 -eq 0) "exit=$code4"
    Start-Sleep -Seconds 2
    Get-Process neo -ErrorAction SilentlyContinue | Stop-Process -Force
}

if ($script:fail -gt 0) { Write-Host "[verify-installer] FAILED：$($script:fail) 项未通过"; exit 1 }
Write-Host '[verify-installer] OK：安装包验收全部通过'
exit 0
