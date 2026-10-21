<#
  编译 tools\launcher\neo.exe（root 启动器，负责拉起 app\neo.exe）。
  gcc 查找顺序：-Gcc 参数 → NEO_GCC 环境变量 → PATH 里的 gcc.exe → 已知便携路径。
  编译后从 neo_launcher.rc 读 FILEVERSION，断言产物 FileVersion 与 rc 一致（P1-2）。
#>
param(
    [string]$Gcc
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

$candidates = @(
    $Gcc,
    $env:NEO_GCC,
    (Get-Command gcc.exe -ErrorAction SilentlyContinue).Source,
    'E:\桌面\工具软件\c++工具\w64devkit\bin\gcc.exe',
    'E:\桌面\c++工具\w64devkit\bin\gcc.exe'
) | Where-Object { $_ -and (Test-Path $_) }
$gcc = $candidates | Select-Object -First 1
if (-not $gcc) { throw 'gcc not found：安装 w64devkit，或用 -Gcc <path> / NEO_GCC 指定' }
$windres = Join-Path (Split-Path $gcc) 'windres.exe'
if (-not (Test-Path $windres)) { throw "windres not found next to gcc: $windres" }

Push-Location $here
try {
    & $windres '--output-format=coff' '--input-format=rc' '-i' 'neo_launcher.rc' '-o' 'neo_launcher.res'
    if ($LASTEXITCODE -ne 0) { throw "windres failed: $LASTEXITCODE" }
    & $gcc '-O2' '-s' '-municode' '-mwindows' '-finput-charset=UTF-8' ("-B" + (Split-Path $gcc) + '\') 'neo_launcher.c' 'neo_launcher.res' '-o' 'neo.exe'
    if ($LASTEXITCODE -ne 0) { throw "gcc failed: $LASTEXITCODE" }

    # 从 rc 反读期望版本并比对产物：避免 rc 改了却忘了重编译，或产物与 rc 脱节
    $rc = Get-Content -Raw -LiteralPath (Join-Path $here 'neo_launcher.rc')
    if ($rc -notmatch 'FILEVERSION\s+(\d+),(\d+),(\d+),(\d+)') { throw 'neo_launcher.rc 里没找到 FILEVERSION' }
    $expected = '{0}.{1}.{2}.{3}' -f $Matches[1], $Matches[2], $Matches[3], $Matches[4]

    $vi = (Get-Item (Join-Path $here 'neo.exe')).VersionInfo
    if ($vi.FileVersion -ne $expected) {
        throw "launcher FileVersion=$($vi.FileVersion) 与 rc FILEVERSION=$expected 不一致"
    }
    $size = (Get-Item (Join-Path $here 'neo.exe')).Length
    Write-Host ("[launcher] built neo.exe ({0:N0} bytes), FileVersion = {1}" -f $size, $vi.FileVersion)
}
finally {
    Pop-Location
}
