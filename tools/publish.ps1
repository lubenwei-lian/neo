<#
.SYNOPSIS
  生成 neo 发布目录 dist\publish（完整、可直接交给 iscc 的形态）。

.DESCRIPTION
  ★ 0.2.26 起的打包纪律（P0-1 / P2-23）：
    正确顺序只有一条：tools\publish.ps1 → iscc installer\neo.iss。
    禁止直接拿半成品 dist\publish 打包。本脚本末尾有硬断言（缺一项就 throw）：
      1) 产物存在：dist\publish\neo.exe、说明.txt、data\、app\neo.exe、app\Neo.Core.dll；
      2) 运行时资产：app\runtime\xray.exe(>30MB)、sing-box.exe(>60MB)、geoip.dat、
         geosite.dat、wintun.dll、libcronet.dll、rule-sets\geoip-cn.srs、geosite-cn.srs；
      3) 版本一致：Neo.App/Neo.Core 的 csproj Version/FileVersion == installer\version.iss
         == dist\publish\neo.exe 的 FileVersion == 说明.txt 版本行；
      4) app\ 下不再有 *.pdb 与 .gitkeep；
      5) 生成 dist\publish\SHA256SUMS.txt（neo.exe / app\neo.exe / app\runtime\xray.exe /
         app\runtime\sing-box.exe；release.ps1 追加的安装包行会被保留）；
      6) 生成 build-info.json（dist\build-info.json 与 dist\publish\build-info.json），供应用
         判断「同版本号换资产后的新构建」；-ValidateOnly 只校验、不重写。
    语言目录（WindowsAppSDK 本地化所需）全部保留，不再裁剪。

.PARAMETER KillRunning
  可选。默认不杀进程；加此开关才强杀 neo / VBCSCompiler / xray / sing-box。
  仅在确认构建输出被占用、且已征得用户同意时使用。

.PARAMETER ValidateOnly
  可选。跳过构建，只对现有 dist\publish 复跑完整性/版本断言并重写 SHA256SUMS.txt。
  用于 iscc 前快速复核（例如打包脚本被手工调用过之后）。

.EXAMPLE
  pwsh -NoProfile -File tools\publish.ps1
.EXAMPLE
  pwsh -NoProfile -File tools\publish.ps1 -ValidateOnly
#>
param(
    [switch]$KillRunning,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# 构建开始时刻：build-info.json 的 build / buildUtc 取这里（不用产物文件时间）。
$buildStart = Get-Date

# ============================================================================
# 版本号一致性（单一来源：installer\version.iss）
# ============================================================================
function Get-ProjectVersion([string]$relPath, [string]$tag) {
    $p = Join-Path $root $relPath
    if (-not (Test-Path -LiteralPath $p)) { throw "[version] 找不到 $relPath" }
    $xml = [xml]([System.IO.File]::ReadAllText($p))
    $values = @($xml.Project.PropertyGroup | ForEach-Object { $_.$tag } | Where-Object { $_ })
    if ($values.Count -eq 0) { throw "[version] $relPath 缺少 <$tag>" }
    return [string]$values[0]
}

function Get-IssVersion {
    $p = Join-Path $root 'installer\version.iss'
    if (-not (Test-Path -LiteralPath $p)) { throw '[version] 找不到 installer\version.iss（版本唯一来源）' }
    $text = [System.IO.File]::ReadAllText($p)
    if ($text -notmatch '#define\s+MyAppVersion\s+"([0-9]+\.[0-9]+\.[0-9]+)"') {
        throw '[version] installer\version.iss 里没找到 #define MyAppVersion "x.y.z"'
    }
    return $Matches[1]
}

function Get-ReadmeVersion {
    $p = Join-Path $root 'installer\说明.txt'
    if (-not (Test-Path -LiteralPath $p)) { throw '[version] 找不到 installer\说明.txt' }
    $text = [System.IO.File]::ReadAllText($p)
    if ($text -notmatch '版本\s+([0-9]+\.[0-9]+\.[0-9]+)') {
        throw '[version] 说明.txt 里没找到「版本 x.y.z」行'
    }
    return $Matches[1]
}

function Assert-VersionEqual([string]$label, [string]$actual, [string]$expected) {
    if ($actual -ne $expected) {
        throw "[version] 不一致：$label = '$actual'，期望 '$expected'（唯一来源 installer\version.iss）。请先同步所有版本源再发布。"
    }
}
# ============================================================================
# build-info.json：应用靠它判断「同版本号换资产后的新构建」（同版本号时比 build 时间）
# 字段：app / build / buildUtc / git / gitDirty / kernels{xray,singbox}。
# 任何一项取不到都 throw —— 宁可不发布，也不写一份假数据。
# ============================================================================
function Get-GitOutput([string[]]$arguments) {
    $out = & git @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw ("[build-info] git {0} 失败（exit {1}）：{2}" -f ($arguments -join ' '), $LASTEXITCODE, (($out | Out-String).Trim()))
    }
    return (($out | Out-String).Trim())
}

function Get-KernelVersion([string]$exe, [string]$pattern, [string]$name) {
    if (-not (Test-Path -LiteralPath $exe)) { throw "[build-info] $name 内核不存在：$exe" }
    $out = (& $exe version 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "[build-info] $name version 退出码 $LASTEXITCODE" }
    $m = [regex]::Match($out, $pattern)
    if (-not $m.Success) { throw ("[build-info] 解析 {0} 版本失败：{1}" -f $name, $out.Trim()) }
    return $m.Groups[1].Value
}

function Write-BuildInfoJson([string]$ver) {
    $xrayVer = Get-KernelVersion (Join-Path $root 'runtime\xray.exe') '(?im)^Xray\s+([0-9][^\s]*)' 'xray'
    $singboxVer = Get-KernelVersion (Join-Path $root 'runtime\sing-box.exe') '(?im)sing-box version\s+(\S+)' 'sing-box'
    $gitShort = Get-GitOutput @('rev-parse', '--short', 'HEAD')
    $gitDirty = (Get-GitOutput @('status', '--porcelain')).Length -gt 0
    $info = [ordered]@{
        app      = $ver
        build    = $buildStart.ToString('yyyy-MM-ddTHH:mm:ss.fffzzz')
        buildUtc = $buildStart.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
        git      = $gitShort
        gitDirty = $gitDirty
        kernels  = [ordered]@{ xray = $xrayVer; singbox = $singboxVer }
    }
    $json = ($info | ConvertTo-Json -Depth 6)
    if (-not $json.EndsWith($nl)) { $json += $nl }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $targets = @((Join-Path $root 'dist\build-info.json'), (Join-Path $out 'build-info.json'))
    foreach ($t in $targets) { [System.IO.File]::WriteAllText($t, $json, $utf8) }
    return $targets
}

function Assert-BuildInfoJson([string]$ver) {
    $p = Join-Path $out 'build-info.json'
    if (-not (Test-Path -LiteralPath $p)) { throw "[build-info] 缺少 $p（先完整运行本脚本，不要加 -ValidateOnly）" }
    $raw = [System.IO.File]::ReadAllText($p)
    $d = $raw | ConvertFrom-Json
    if ([string]$d.app -ne $ver) { throw "[build-info] app='$($d.app)'，期望 '$ver'" }
    # build/buildUtc 从原文取：ConvertFrom-Json 会自动把 ISO 字符串转成 DateTime，打印/比较会被本地化改写。
    $bm = [regex]::Match($raw, '"build"\s*:\s*"([^"]+)"')
    if (-not $bm.Success) { throw '[build-info] 缺少 build 字段' }
    $buildRaw = $bm.Groups[1].Value
    if (-not [regex]::Match($raw, '"buildUtc"\s*:\s*"([^"]+)"').Success) { throw '[build-info] 缺少 buildUtc 字段' }
    if (-not $d.git) { throw '[build-info] 缺少 git 字段' }
    if (-not $d.kernels.xray -or -not $d.kernels.singbox) { throw '[build-info] 缺少 kernels.xray / kernels.singbox' }
    $raw2 = [System.IO.File]::ReadAllText((Join-Path $root 'dist\build-info.json'))
    $bm2 = [regex]::Match($raw2, '"build"\s*:\s*"([^"]+)"')
    if (-not $bm2.Success -or $bm2.Groups[1].Value -ne $buildRaw) { throw '[build-info] dist\build-info.json 与 dist\publish\build-info.json 的 build 不一致' }
    Write-Host ("[neo-publish]   build-info ok: app={0} build={1} git={2} xray={3} sing-box={4}" -f $d.app, $buildRaw, $d.git, $d.kernels.xray, $d.kernels.singbox)
}

$nl = [string][char]10
$out = Join-Path $root 'dist\publish'
$appDir = Join-Path $out 'app'

Write-Host '[neo-publish] step 0: version consistency (single source: installer\version.iss)'
$ver = Get-IssVersion
$appVer = Get-ProjectVersion 'src\Neo.App\Neo.App.csproj' 'Version'
$coreVer = Get-ProjectVersion 'src\Neo.Core\Neo.Core.csproj' 'Version'
$appFileVer = Get-ProjectVersion 'src\Neo.App\Neo.App.csproj' 'FileVersion'
$coreFileVer = Get-ProjectVersion 'src\Neo.Core\Neo.Core.csproj' 'FileVersion'
$readmeVer = Get-ReadmeVersion
$expectedFileVer = $ver + '.0'
Assert-VersionEqual 'Neo.App.csproj <Version>' $appVer $ver
Assert-VersionEqual 'Neo.Core.csproj <Version>' $coreVer $ver
Assert-VersionEqual 'Neo.App.csproj <FileVersion>' $appFileVer $expectedFileVer
Assert-VersionEqual 'Neo.Core.csproj <FileVersion>' $coreFileVer $expectedFileVer
Assert-VersionEqual '说明.txt 版本行' $readmeVer $ver
$launcher = Join-Path $root 'tools\launcher\neo.exe'
if (-not (Test-Path -LiteralPath $launcher)) {
    throw '启动器缺失：tools\launcher\neo.exe（先运行 tools\launcher\build.ps1）'
}
Assert-VersionEqual 'tools\launcher\neo.exe FileVersion' ((Get-Item -LiteralPath $launcher).VersionInfo.FileVersion) $expectedFileVer
Write-Host "[neo-publish]   version = $ver (all sources agree)"

if ($ValidateOnly) {
    Write-Host '[neo-publish] -ValidateOnly：跳过构建，只复核现有 dist\publish'
} else {
    Write-Host '[neo-publish] step 0b: sanitize proxy env (NuGet rejects socks5h)'
    $env:HTTP_PROXY = $null; $env:HTTPS_PROXY = $null; $env:ALL_PROXY = $null
    $env:http_proxy = $null; $env:https_proxy = $null; $env:all_proxy = $null

    if ($KillRunning) {
        Write-Host '[neo-publish] step 1: -KillRunning 已指定，强杀 neo / VBCSCompiler / xray / sing-box'
        foreach ($n in 'neo', 'VBCSCompiler', 'xray', 'sing-box') {
            Get-Process -Name $n -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        }
    } else {
        Write-Host '[neo-publish] step 1: 默认不杀进程（不碰用户正在用的 neo / 内核）。'
        Write-Host '[neo-publish]           若构建被文件占用，先征得用户同意，再加 -KillRunning。'
    }

    $dotnet = $env:NEO_DOTNET
    if (-not $dotnet) {
        $dc = Get-Command dotnet.exe -ErrorAction SilentlyContinue
        if ($dc) { $dotnet = $dc.Source }
    }
    if (-not $dotnet -and (Test-Path 'C:\Program Files\dotnet\dotnet.exe')) {
        $dotnet = 'C:\Program Files\dotnet\dotnet.exe'
    }
    if (-not $dotnet) { throw 'dotnet 未找到：把 dotnet 放进 PATH，或用 NEO_DOTNET 指定' }
    Write-Host "[neo-publish]   dotnet: $dotnet"

    $pyExe = $env:NEO_PYTHON
    if (-not $pyExe) {
        $pc = Get-Command python.exe -ErrorAction SilentlyContinue
        if ($pc) { $pyExe = $pc.Source }
    }
    if (-not $pyExe) {
        $pc = Get-Command py.exe -ErrorAction SilentlyContinue
        if ($pc) { $pyExe = $pc.Source }
    }
    if (-not $pyExe) { throw 'python 未找到：把 python 放进 PATH，或用 NEO_PYTHON 指定' }
    Write-Host "[neo-publish]   python: $pyExe"

    if (Test-Path $out) { Remove-Item $out -Recurse -Force }

    Write-Host '[neo-publish] step 2: dotnet publish (Release, self-contained, win-x64) -> app\'
    & $dotnet publish 'src\Neo.App\Neo.App.csproj' -c Release -p:Platform=x64 -r win-x64 --self-contained true -o $appDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
    if (-not (Test-Path -LiteralPath (Join-Path $appDir 'neo.exe'))) {
        throw "dotnet publish 后 app\neo.exe 不存在：$appDir"
    }

    Write-Host '[neo-publish] step 2b: check XAML comments (XML forbids two dashes inside comments)'
    # XAML 编译器报 WMC9997。这个错误会让整个构建失败，且报错行号指向注释块起始
    # 而非真正那一行，排查很费时。
    $xc = Join-Path $root 'tools\check-xaml-comments.py'
    if (Test-Path $xc) {
        & $pyExe $xc
        if ($LASTEXITCODE -ne 0) {
            throw '[neo-publish] XAML 注释校验未通过。修复：python tools\check-xaml-comments.py --fix'
        }
        Write-Host '[neo-publish]   xaml comment check passed'
    } else {
        Write-Warning '[neo-publish] tools\check-xaml-comments.py missing - 跳过注释校验'
    }

    Write-Host '[neo-publish] step 3: verify rule-sets (the CN split lists must NOT regress)'
    # TUN 下大量国内域名会被判为"非国内"而走代理。上游精简版 geosite-cn.srs 只有
    # 9,302 条，而 xray 吃的 geosite.dat 有 111,415 条，差 12 倍，正是用户抱怨
    # 「TUN 对国内直连非常不友好」的根因。这里在打包前硬性拦住回退。
    # 重新生成（.dat 升级后执行）：python tools\gen-rulesets.py
    $gen = Join-Path $root 'tools\gen-rulesets.py'
    if (Test-Path $gen) {
        & $pyExe $gen --check
        if ($LASTEXITCODE -ne 0) {
            throw '[neo-publish] rule-set 校验未通过：CN 清单疑似被裁剪回退。先跑 python tools\gen-rulesets.py 重新生成，再打包。'
        }
        Write-Host '[neo-publish]   rule-set check passed'
    } else {
        Write-Warning '[neo-publish] tools\gen-rulesets.py missing - 跳过 rule-set 校验'
    }

    Write-Host '[neo-publish] step 4: copy runtime (xray / sing-box / geo data / rule-sets / wintun / libcronet)'
    $rtSrc = Join-Path $root 'runtime'
    $rtOut = Join-Path $appDir 'runtime'
    if (-not (Test-Path -LiteralPath $rtSrc)) { throw "runtime 源目录不存在：$rtSrc" }
    New-Item -ItemType Directory -Force $rtOut | Out-Null
    Get-ChildItem $rtSrc -Force | ForEach-Object { Copy-Item $_.FullName -Destination $rtOut -Recurse -Force }

    Write-Host '[neo-publish] step 5: copy VC++ runtime app-local (needed by WindowsAppSDK on clean machines)'
    $crt = $null
    if ($env:NEO_VCRT -and (Test-Path (Join-Path $env:NEO_VCRT 'vcruntime140.dll'))) {
        $crt = Get-Item -LiteralPath $env:NEO_VCRT
    } else {
        $crtCandidates = Get-ChildItem 'C:\Program Files (x86)\Microsoft Visual Studio\*\BuildTools\VC\Redist\MSVC\*\x64\Microsoft.VC*.CRT' -ErrorAction SilentlyContinue
        $crt = $crtCandidates | Where-Object { Test-Path (Join-Path $_.FullName 'vcruntime140.dll') } | Select-Object -First 1
    }
    if (-not $crt) {
        throw '[neo-publish] VC++ CRT redist 未找到（干净机器上会起不来）。装 VS BuildTools，或用 NEO_VCRT 指向 Microsoft.VC*.CRT 目录。'
    }
    Get-ChildItem (Join-Path $crt.FullName '*.dll') | Copy-Item -Destination $appDir -Force
    Write-Host ("[neo-publish]   CRT source: " + $crt.FullName)

    Write-Host '[neo-publish] step 6: prune unused AI/ML payload (language dirs are KEPT: WindowsAppSDK localization)'
    $aiFiles = Get-ChildItem $appDir -File | Where-Object { $_.Name -match '^(onnxruntime.*|DirectML)\.dll$' -or $_.Name -like 'Microsoft.ML.*' -or $_.Name -like 'Microsoft.Windows.AI.*' -or $_.Name -eq 'createdump.exe' }
    $aiCount = ($aiFiles | Measure-Object).Count
    $aiSize = ($aiFiles | Measure-Object Length -Sum).Sum / 1MB
    $aiFiles | Remove-Item -Force -ErrorAction SilentlyContinue
    Write-Host ("[neo-publish]   pruned {0} AI/ML files ({1:N0} MB); language dirs untouched" -f $aiCount, $aiSize)

    Write-Host '[neo-publish] step 7: strip symbols, place launcher / readme / data dir'
    $pdbs = @(Get-ChildItem $appDir -File -Recurse -Filter '*.pdb' -ErrorAction SilentlyContinue)
    $pdbs | Remove-Item -Force -ErrorAction SilentlyContinue
    $gitkeeps = @(Get-ChildItem $appDir -File -Recurse -Force -Filter '.gitkeep' -ErrorAction SilentlyContinue)
    $gitkeeps | Remove-Item -Force -ErrorAction SilentlyContinue
    Copy-Item -LiteralPath $launcher -Destination (Join-Path $out 'neo.exe') -Force
    Copy-Item -LiteralPath (Join-Path $root 'installer\说明.txt') -Destination (Join-Path $out '说明.txt') -Force
    New-Item -ItemType Directory -Force (Join-Path $out 'data') | Out-Null
    Write-Host ("[neo-publish]   stripped {0} pdb, removed {1} .gitkeep" -f $pdbs.Count, $gitkeeps.Count)
}

# ============================================================================
# ============================================================================
Write-Host '[neo-publish] step 8: hard assertions on dist\publish'
if (-not (Test-Path -LiteralPath $out)) { throw "发布目录不存在：$out（先完整运行本脚本，不要加 -ValidateOnly）" }

$required = @(
    @{ Rel = 'neo.exe'; Desc = 'root 启动器' },
    @{ Rel = '说明.txt'; Desc = '使用说明' },
    @{ Rel = 'app\neo.exe'; Desc = '程序本体' },
    @{ Rel = 'app\Neo.Core.dll'; Desc = 'Neo.Core 程序集' },
    @{ Rel = 'app\runtime\xray.exe'; Desc = 'xray 内核'; MinBytes = 30MB },
    @{ Rel = 'app\runtime\sing-box.exe'; Desc = 'sing-box 内核'; MinBytes = 60MB },
    @{ Rel = 'app\runtime\geoip.dat'; Desc = 'geoip 数据' },
    @{ Rel = 'app\runtime\geosite.dat'; Desc = 'geosite 数据' },
    @{ Rel = 'app\runtime\wintun.dll'; Desc = 'wintun 驱动库' },
    @{ Rel = 'app\runtime\libcronet.dll'; Desc = 'libcronet 运行库' },
    @{ Rel = 'app\runtime\rule-sets\geoip-cn.srs'; Desc = 'CN geoip 分流规则' },
    @{ Rel = 'app\runtime\rule-sets\geosite-cn.srs'; Desc = 'CN geosite 分流规则' }
)
$bad = New-Object System.Collections.Generic.List[string]
foreach ($r in $required) {
    $full = Join-Path $out $r.Rel
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        $bad.Add("缺失 $($r.Desc)：$full")
        continue
    }
    if ($r.MinBytes) {
        $len = (Get-Item -LiteralPath $full).Length
        if ($len -lt $r.MinBytes) {
            $bad.Add(("体积过小 {0}：{1:N0} B < {2:N0} B（{3}）" -f $r.Desc, $len, $r.MinBytes, $full))
        }
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $out 'data') -PathType Container)) {
    $bad.Add("缺失 data 目录：$(Join-Path $out 'data')")
}
if ($bad.Count -gt 0) {
    throw ('[neo-publish] 产物完整性断言失败，禁止打包（缺 ' + $bad.Count + ' 项）：' + $nl + '  - ' + ($bad -join ($nl + '  - ')))
}

Assert-VersionEqual 'dist\publish\neo.exe FileVersion' ((Get-Item -LiteralPath (Join-Path $out 'neo.exe')).VersionInfo.FileVersion) $expectedFileVer
Assert-VersionEqual 'dist\publish\app\neo.exe FileVersion' ((Get-Item -LiteralPath (Join-Path $out 'app\neo.exe')).VersionInfo.FileVersion) $expectedFileVer
Assert-VersionEqual 'dist\publish\app\Neo.Core.dll FileVersion' ((Get-Item -LiteralPath (Join-Path $out 'app\Neo.Core.dll')).VersionInfo.FileVersion) $expectedFileVer

$leftPdb = @(Get-ChildItem $out -File -Recurse -Filter '*.pdb' -ErrorAction SilentlyContinue)
if ($leftPdb.Count -gt 0) { throw ('[neo-publish] 发布目录里还有 ' + $leftPdb.Count + ' 个 .pdb 未剥离（第一个：' + $leftPdb[0].FullName + '）') }
$leftGitkeep = @(Get-ChildItem $out -File -Recurse -Force -Filter '.gitkeep' -ErrorAction SilentlyContinue)
if ($leftGitkeep.Count -gt 0) { throw ('[neo-publish] 发布目录里还有 ' + $leftGitkeep.Count + ' 个 .gitkeep（第一个：' + $leftGitkeep[0].FullName + '）') }

Write-Host '[neo-publish] step 9: write SHA256SUMS.txt'
$hashRels = @('neo.exe', 'app\neo.exe', 'app\runtime\xray.exe', 'app\runtime\sing-box.exe')
$hashLines = @()
foreach ($rel in $hashRels) {
    $full = Join-Path $out $rel
    $h = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
    $hashLines += ('{0}  {1}' -f $h, ($rel -replace '\\', '/'))
}
$sumPath = Join-Path $out 'SHA256SUMS.txt'
#   保留 release.ps1 追加的非 payload 行（如 ../installer/neo-setup-x.y.z.exe）：
#   -ValidateOnly 会重写本文件，不保留就会把安装包行冲掉。
$publishedRels = @($hashRels | ForEach-Object { $_ -replace '\\', '/' })
$preserved = @()
if (Test-Path -LiteralPath $sumPath) {
    foreach ($line in @(Get-Content -LiteralPath $sumPath -ErrorAction SilentlyContinue)) {
        $t = $line.Trim()
        if ($t.Length -eq 0) { continue }
        $parts = $t -split '\s+', 2
        if ($parts.Count -lt 2) { throw "[neo-publish] SHA256SUMS.txt 行格式错误：$t" }
        if ($publishedRels -notcontains $parts[1]) { $preserved += $t }
    }
}
[System.IO.File]::WriteAllText($sumPath, ((($hashLines + $preserved) -join $nl) + $nl), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "  -> $sumPath"
Write-Host '[neo-publish] step 10: write build-info.json'
if ($ValidateOnly) {
    Assert-BuildInfoJson $ver
    Write-Host '[neo-publish]   -ValidateOnly：只校验，不重写（避免把校验时刻当成构建时刻）'
} else {
    $biTargets = Write-BuildInfoJson $ver
    foreach ($t in $biTargets) { Write-Host ("  -> " + $t) }
}

$files = @(Get-ChildItem $out -Recurse -File)
$sum = ($files | Measure-Object Length -Sum).Sum / 1MB
$top = (Get-ChildItem $out -Force | Measure-Object).Count
Write-Host ("[neo-publish] DONE: version {0}, {1} files, {2:N0} MB, top-level entries: {3} -> {4}" -f $ver, $files.Count, $sum, $top, $out)
Write-Host '[neo-publish] NEXT: pwsh -NoProfile -File tools\release.ps1（= iscc + 安装包 sha256 + 校验 + gh 命令模板；本脚本不打包）'
