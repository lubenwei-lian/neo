<#
.SYNOPSIS
  构建 neo 自编译 sing-box 内核（含 balancer，TUN 多节点并发所需），产出可直接上传 release 的 zip + sha256。

.DESCRIPTION
  2026-10-21 新增（内核更新链路加固的一部分）。背景：
    · runtime\sing-box.exe 不是官方版，是打了 balancer 补丁的自编译版；
    · 之前只有 doc\内核补丁-20261018\说明.md 里的手工步骤，源码目录与 go 工具链路径已失效，无法复现；
    · 应用端「检查更新」只认 neo-singbox-<版本>-windows-amd64.zip，且现在要求同目录有 .sha256。

  本脚本做四件事：
    1) 取 sing-box v1.14.3 源码（本地 -SourceDir 或从 GitHub 下载 zip）；
    2) 用 doc 里的补丁版 balancer.go 覆盖 protocol/group/balancer.go；
    3) 按文档的 build tags / ldflags 编译，产出 sing-box.exe（版本串 = -Version）；
    4) 自检版本 + balancer 能力，打包 dist\kernel\neo-singbox-<版本>-windows-amd64.zip 并写 .sha256。

.PARAMETER Version
  注入到二进制里的版本串，默认 1.14.3-noGate。发布资产文件名会用它。

.PARAMETER SingBoxTag
  上游源码 tag，默认 v1.14.3。补丁是按这个版本的 balancer.go 做的，换版本必须重新核对。

.PARAMETER SourceDir
  可选：已解压的 sing-box 源码目录。不传则自动下载 tag 的源码 zip。

.PARAMETER OutDir
  产物目录，默认 <repo>\dist\kernel。

.EXAMPLE
  pwsh -NoProfile -File tools\build-singbox.ps1
#>
param(
    [string]$Version = '1.14.3-noGate',
    [string]$SingBoxTag = 'v1.14.3',
    [string]$SourceDir = '',
    [string]$OutDir = '',
    [string]$GoExe = 'E:\桌面\工具软件\编译工具\go\bin\go.exe',
    [string]$PatchDir = '',
    [string]$WorkDir = '',
    # 允许 go 自动下载匹配 go.mod 的工具链（GOTOOLCHAIN=auto）。默认关闭（离线可复现优先）。
    [switch]$AllowToolchainDownload
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $root 'dist\kernel' }
if (-not $WorkDir) { $WorkDir = Join-Path $root '_work\singbox-build' }
if (-not $PatchDir) {
    $PatchDir = Join-Path $root 'doc\内核补丁-20261018\files'
}

function Fail([string]$msg) { throw "[build-singbox] $msg" }
function Step([string]$msg) { Write-Host "[build-singbox] $msg" }

# ---- 0. 前置检查 ----
Step "version=$Version  upstream=$SingBoxTag"
if (-not (Test-Path -LiteralPath $GoExe)) { Fail "找不到 go：$GoExe（可用 -GoExe 指定）" }
if (-not (Test-Path -LiteralPath $PatchDir)) { Fail "找不到补丁目录：$PatchDir" }
& $GoExe version | ForEach-Object { Step "go: $_" }

# ---- 1. 源码 ----
if (-not $SourceDir) {
    $zip = Join-Path $WorkDir "sing-box-$SingBoxTag.zip"
    $SourceDir = Join-Path $WorkDir "sing-box-$SingBoxTag"
    if (-not (Test-Path -LiteralPath (Join-Path $SourceDir 'go.mod'))) {
        New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
        if (-not (Test-Path -LiteralPath $zip)) {
            $url = "https://github.com/SagerNet/sing-box/archive/refs/tags/$SingBoxTag.zip"
            Step "下载源码 $url"
            # 直连失败时尝试系统代理（Electron/Windows 的 IE 设置由 Invoke-WebRequest 默认读取？不保证）
            try {
                Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing -TimeoutSec 120
            } catch {
                Fail "源码下载失败：$($_.Exception.Message)。可手动下载 $url 解压后用 -SourceDir 指定"
            }
        }
        Step '解压源码…'
        Expand-Archive -LiteralPath $zip -DestinationPath $WorkDir -Force
        $extracted = Get-ChildItem $WorkDir -Directory | Where-Object { $_.Name -like "sing-box-*" } | Select-Object -First 1
        if (-not $extracted) { Fail '解压后未找到源码目录' }
        $SourceDir = $extracted.FullName
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $SourceDir 'go.mod'))) { Fail "源码目录无效（缺 go.mod）：$SourceDir" }
Step "source: $SourceDir"

function Get-GoTuple([string]$s) {
    $m = [regex]::Match($s, 'go\s*(\d+)\.(\d+)(?:\.(\d+))?')
    if (-not $m.Success) { return $null }
    $patch = if ($m.Groups[3].Success) { [int]$m.Groups[3].Value } else { 0 }
    return @([int]$m.Groups[1].Value, [int]$m.Groups[2].Value, $patch)
}
function Compare-GoTuple($a, $b) {
    for ($i = 0; $i -lt 3; $i++) {
        if ($a[$i] -lt $b[$i]) { return -1 }
        if ($a[$i] -gt $b[$i]) { return 1 }
    }
    return 0
}
$localGoLine = (& $GoExe version 2>&1 | Out-String).Trim()
$localGo = Get-GoTuple $localGoLine
$requiredLine = (Get-Content (Join-Path $SourceDir 'go.mod') | Where-Object { $_ -match '^go\s+' } | Select-Object -First 1)
$requiredGo = Get-GoTuple $requiredLine
if (-not $localGo) { Fail "无法解析 go 版本：$localGoLine" }
if ($requiredGo -and (Compare-GoTuple $localGo $requiredGo) -lt 0) {
    if ($AllowToolchainDownload) {
        Step "本机 go 低于源码要求（$($requiredLine.Trim())），已启用 -AllowToolchainDownload：将让 go 自动下载匹配的工具链"
    } else {
        $msg = "go 版本不够：源码要求 go {0}.{1}.{2}，当前是 {3}。请用 -GoExe 指定 go >= {0}.{1}.{2} 的可执行文件（例如便携版 go1.25.x），或加 -AllowToolchainDownload 让 go 自动下载工具链。" -f $requiredGo[0], $requiredGo[1], $requiredGo[2], $localGoLine
        Fail $msg
    }
} else {
    Step "go 版本满足要求（$localGoLine；源码要求 $($requiredLine.Trim())）"
}

# ---- 2. 应用补丁 ----
#   ① protocol/group/balancer.go   新增 balancer 出站本体（含「删掉 3 倍质量门槛」改动）
#   ② option/balancer.go           新增 option.BalancerOutboundOptions（否则 config 加载报 unknown outbound type）
#   ③ constant/balancer.go         新增 TypeBalancer / 策略 / sticky / 默认值常量
#   ④ include/registry.go          注册 group.RegisterBalancer（脚本内做一行插入）
# 只补 balancer.go 是编不过的 —— 这正是「更新链路断档」的具体表现，已修。
$groupDir = Join-Path $SourceDir 'protocol\group'
if (-not (Test-Path -LiteralPath $groupDir)) { Fail "源码里找不到 protocol/group 目录（tag 目录结构变了？）" }

$patchFiles = Get-ChildItem -LiteralPath $PatchDir -Recurse -File
if ($patchFiles.Count -eq 0) { Fail "补丁目录为空：$PatchDir" }
foreach ($f in $patchFiles) {
    $rel = $f.FullName.Substring($PatchDir.TrimEnd('\').Length).TrimStart('\')
    $dst = Join-Path $SourceDir $rel
    $dstDir = Split-Path -Parent $dst
    New-Item -ItemType Directory -Force -Path $dstDir | Out-Null
    Copy-Item -LiteralPath $f.FullName -Destination $dst -Force
    Step "已应用补丁文件：$rel"
}

# ④ 在 include/registry.go 里注册 balancer 出站（一行插入，带命中数断言）
$registryFile = Join-Path $SourceDir 'include\registry.go'
if (-not (Test-Path -LiteralPath $registryFile)) { Fail '源码里找不到 include/registry.go' }
$registryText = Get-Content -LiteralPath $registryFile -Raw
if ($registryText -notmatch 'group\.RegisterBalancer\(') {
    $hitCount = ([regex]::Matches($registryText, [regex]::Escape('group.RegisterURLTest(registry)'))).Count
    if ($hitCount -ne 1) { Fail "include/registry.go 里 group.RegisterURLTest(registry) 命中 $hitCount 次（期望 1），已中止" }
    $replacement = 'group.RegisterURLTest(registry)' + [Environment]::NewLine + '    group.RegisterBalancer(registry)'
    $registryText = $registryText -replace [regex]::Escape('group.RegisterURLTest(registry)'), $replacement
    Set-Content -LiteralPath $registryFile -Value $registryText -NoNewline
    Step '已在 include/registry.go 注册 group.RegisterBalancer'
} else {
    Step 'include/registry.go 已注册过 RegisterBalancer，跳过'
}
Step '补丁应用完成'

# ---- 3. 编译 ----
$tagsFile = Join-Path $SourceDir 'release\DEFAULT_BUILD_TAGS_OTHERS'
if (-not (Test-Path -LiteralPath $tagsFile)) { Fail '源码里找不到 release/DEFAULT_BUILD_TAGS_OTHERS' }
$tags = (Get-Content $tagsFile -Raw).Trim() -replace ',badlinkname','' -replace ',tfogo_checklinkname0',''
Step "build tags: $tags"

$env:GOPROXY = 'https://goproxy.cn,direct'
$env:GOTOOLCHAIN = if ($AllowToolchainDownload) { 'auto' } else { 'local' }
$env:CGO_ENABLED = '0'
$env:GOFLAGS = '-mod=readonly'
if (-not $env:GOMODCACHE) { $env:GOMODCACHE = 'E:\Cache\go-mod' }
if (-not $env:GOCACHE) { $env:GOCACHE = 'E:\Cache\go-build' }

$exeOut = Join-Path $WorkDir 'sing-box.exe'
Push-Location $SourceDir
try {
    Step 'go build…（首次会拉依赖，可能 3-10 分钟）'
    & $GoExe build -trimpath -p 6 -tags $tags `
        -ldflags "-X github.com/sagernet/sing-box/constant.Version=$Version -s -w" `
        -o $exeOut ./cmd/sing-box
    if ($LASTEXITCODE -ne 0) { Fail "go build 失败，退出码 $LASTEXITCODE" }
} finally { Pop-Location }

# ---- 4. 自检（版本 + balancer 能力） ----
$verOut = & $exeOut version 2>&1 | Out-String
Step ($verOut.Trim())
if ($verOut -notmatch [regex]::Escape($Version)) { Fail "版本自检失败：期望包含 $Version，实得 $($verOut.Trim())" }

$probe = Join-Path $WorkDir 'balancer-probe.json'
$probeJson = @'
{
  "log": {"level": "error"},
  "inbounds": [{"type":"socks","tag":"probe-in","listen":"127.0.0.1","listen_port":19998}],
  "outbounds": [
    {"type":"socks","tag":"pa","server":"127.0.0.1","server_port":1080},
    {"type":"socks","tag":"pb","server":"127.0.0.1","server_port":1081},
    {"type":"balancer","tag":"bal","outbounds":["pa","pb"],"strategy":"roundRobin","interval":"45s","idle_timeout":"30m"}
  ]
}
'@
Set-Content -LiteralPath $probe -Value $probeJson -Encoding utf8
& $exeOut check -c $probe | Out-Null
if ($LASTEXITCODE -ne 0) { Fail 'balancer 能力自检失败（check 退出码非 0）—— 补丁没生效？' }
Step '自检通过：版本正确且支持 balancer 出站'

# ---- 5. 打包 + sha256 ----
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$zipName = "neo-singbox-$Version-windows-amd64.zip"
$zipPath = Join-Path $OutDir $zipName
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
$stage = Join-Path $WorkDir 'stage'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item -LiteralPath $exeOut -Destination (Join-Path $stage 'sing-box.exe') -Force
Compress-Archive -Path (Join-Path $stage 'sing-box.exe') -DestinationPath $zipPath -Force
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$shaPath = $zipPath + '.sha256'
Set-Content -LiteralPath $shaPath -Value ($hash + '  ' + $zipName) -Encoding ascii
$size = (Get-Item -LiteralPath $zipPath).Length

Step "完成：$zipPath（$([math]::Round($size/1MB,1)) MB）"
Step "SHA-256：$hash"
Step "发布：把 $zipName 与 $zipName.sha256 一起上传到 release（neo 的更新检查只认这个命名）"