# 部署 neo 到安装目录（只覆盖「托管程序集 + XAML 产物」）
#
# 背景（2026-10-23 事故）：用 dotnet build 的全量输出 robocopy 覆盖安装目录，把
# neo.runtimeconfig.json 从「自包含（includedFrameworks）」覆盖成「框架依赖（framework）」，
# 应用随即启动失败并提示「You must install or update .NET」。
# 安装目录必须保持 publish 形态：neo.exe / neo.runtimeconfig.json / neo.deps.json /
# coreclr.dll 等运行时文件一律不能动，只替换我们自己编译出来的程序集与 XAML 产物。
param(
    [string]$Bin = 'E:\桌面\开发工作区\neo\src\Neo.App\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64',
    [string]$App = 'E:\Program Files\neo\app'
)

$ErrorActionPreference = 'Stop'
foreach ($f in @('neo.dll', 'Neo.Core.dll', 'neo.pri')) {
    $src = Join-Path $Bin $f
    if (-not (Test-Path $src)) { throw ("缺少 " + $src + " —— 先 dotnet build") }
    Copy-Item $src (Join-Path $App $f) -Force
    Write-Output ("copied " + $f)
}
foreach ($f in @('App.xbf', 'MainWindow.xbf')) {
    $src = Join-Path $Bin $f
    if (Test-Path $src) { Copy-Item $src (Join-Path $App $f) -Force }
}
$pages = Join-Path $Bin 'Pages'
if (Test-Path $pages) {
    Copy-Item (Join-Path $pages '*.xbf') (Join-Path $App 'Pages') -Force
    Write-Output 'copied Pages/*.xbf'
}

# 自检：安装目录必须仍是自包含配置，否则立刻报警（不要等启动失败才发现）
$rc = Join-Path $App 'neo.runtimeconfig.json'
$txt = Get-Content $rc -Raw
if ($txt -notmatch 'includedFrameworks') {
    throw '安装目录的 neo.runtimeconfig.json 不是自包含形态（缺 includedFrameworks）—— 请从 dist\publish\app 恢复'
}
Write-Output 'deploy done (self-contained config intact)'