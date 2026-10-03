#Requires -Version 7.0
<#
.SYNOPSIS
EOS 发布助手：推送代码与标签（带重试）、建 GitHub Release（说明取自 CHANGELOG、附件取自产物清单）、
并在最后做一次判定性核对。

.DESCRIPTION
为什么需要它（而不是让人手敲 git push + gh release create）：

  ① **网络会抖**。本机经代理访问 GitHub，`git push` / `gh` 偶发
     `EOF`、`SSL connection could not be established`、`Could not resolve host`。
     手敲一次失败就得重来，且容易误判成"权限问题"；这里对每个网络动作做有限重试，
     并把"重试 N 次仍失败"与"确定性失败"区分开。
  ② **Release 说明要取对**。必须取 `CHANGELOG.md` 里**该版本**那一节，而不是整份文件；
     且要核对"脚本算出的版本"与"CHANGELOG 里存在这一节"一致——不一致就停，不建半个 Release。
  ③ **附件位置固定**。`artifacts/release-<版本>/MANIFEST.txt` 与 `SHA256SUMS.txt` 由 `release.ps1` 产出
     （该目录已 gitignore，不随仓库分发，因此只在本机存在）。

本脚本**不做**的事：不改 `version.json`、不改 `CHANGELOG.md`、不打标签（那些是
`scripts/release.ps1` 的职责）；不重启服务；不决定版本号。

.EXAMPLE
# 只推送主分支（本地领先远端时用），随后手工打标签
pwsh .agents/skills/eos-release/scripts/publish-release.ps1 -Version 0.2.0 -SkipTag

# 推送主分支 + 标签
pwsh .agents/skills/eos-release/scripts/publish-release.ps1 -Version 0.2.0

# 再建 GitHub Release（说明取自 CHANGELOG 的 [0.2.0] 节）
pwsh .agents/skills/eos-release/scripts/publish-release.ps1 -Version 0.2.0 -CreateRelease

# 只核对，不做任何写操作
pwsh .agents/skills/eos-release/scripts/publish-release.ps1 -Version 0.2.0 -VerifyOnly
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Repo = 'cnlarry/EOS',

    [switch]$SkipPush,
    [switch]$SkipTag,
    [switch]$CreateRelease,
    [switch]$VerifyOnly,

    [int]$Retries = 5,
    [int]$RetryDelaySeconds = 5
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$script:failed = $false

function Write-Step([string]$text) { Write-Host "== $text" -ForegroundColor Cyan }
function Pass([string]$text) { Write-Host "PASS $text" -ForegroundColor Green }
function Skip([string]$text) { Write-Host "SKIP $text" -ForegroundColor Yellow }
function Fail([string]$text) { Write-Host "FAIL $text" -ForegroundColor Red; $script:failed = $true }

# 代理导致的瞬时网络失败：重试有意义。
# `connection closed` 是 SSH 走代理时最常见的一种（实测推标签时遇到 `Connection closed by 198.18.0.21 port 22`）：
# 它读起来像"对端主动断开"、很像确定性失败，但下一次重试往往就通了——不认它会白白把可重试判成失败。
$transientPattern = 'EOF|SSL connection could not be established|could not resolve host|connection reset|' +
    'connection closed|kex_exchange_identification|banner exchange|operation timed out|' +
    'the remote end hung up|TLS|temporarily unavailable|502|503|504'

function Invoke-WithRetry {
    <#
      执行一条外部命令并重试瞬时网络失败。返回 @{ Ok; Output }。
      非瞬时失败（如权限拒绝、标签不存在）立即返回，不做无意义重试。
    #>
    param([string] $Label, [scriptblock] $Action)

    for ($attempt = 1; $attempt -le $Retries; $attempt++) {
        $output = & $Action 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0) { return @{ Ok = $true; Output = $output.Trim() } }
        $text = $output.Trim()
        if ($attempt -lt $Retries -and $text -match $transientPattern) {
            Write-Host "  $Label 第 $attempt 次失败（疑似网络瞬断），$RetryDelaySeconds 秒后重试：$($text -split "`n" | Select-Object -First 1)" -ForegroundColor DarkYellow
            Start-Sleep -Seconds $RetryDelaySeconds
            continue
        }
        return @{ Ok = $false; Output = $text }
    }
    return @{ Ok = $false; Output = '重试次数用尽' }
}

function Test-RemoteTag {
    <#
      查远端是否已有某标签。**必须返回三态**：网络失败时 `Ok=$false`，绝不能把"查不到"
      当成"不存在"——`git ls-remote` 连不上时输出在 stderr，若直接当空结果处理，会给出
      "远端没有该标签"这种误导性结论（实测踩过：SSH 被掐断却报标签不存在）。连不上时重试。
      @returns @{ Ok; Exists; Output }
    #>
    param([string] $Tag)

    for ($attempt = 1; $attempt -le $Retries; $attempt++) {
        $output = (& git ls-remote --tags origin $Tag 2>&1 | Out-String).Trim()
        if ($LASTEXITCODE -eq 0) {
            return @{ Ok = $true; Exists = -not [string]::IsNullOrWhiteSpace($output); Output = $output }
        }
        if ($attempt -lt $Retries) {
            Write-Host "  查询远端标签第 $attempt 次失败（网络），$RetryDelaySeconds 秒后重试" -ForegroundColor DarkYellow
            Start-Sleep -Seconds $RetryDelaySeconds
        }
        else {
            return @{ Ok = $false; Exists = $false; Output = $output }
        }
    }
    return @{ Ok = $false; Exists = $false; Output = '重试次数用尽' }
}

# ---- 0. 定位仓库根（本脚本位于 .agents/skills/eos-release/scripts/，往上 4 层）----
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
if (-not (Test-Path (Join-Path $root 'version.json'))) {
    Fail "未能从脚本位置定位仓库根（期望 $root 下有 version.json）"
    exit 1
}
Set-Location $root

# gh 可能不在本进程的 PATH 上（安装后老进程 PATH 未刷新），优先用绝对路径。
$ghCmd = 'gh'
$ghAbsolute = Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'
if (Test-Path $ghAbsolute) { $ghCmd = $ghAbsolute }
elseif (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Fail "找不到 gh（既不在 PATH，也不在 $ghAbsolute）——建 Release 需要 GitHub CLI"
    if ($CreateRelease) { exit 1 }
}

$tag = "v$Version"
$artifacts = Join-Path $root "artifacts/release-$Version"
$repoVersion = ([string](Get-Content (Join-Path $root 'version.json') -Raw -Encoding utf8 | ConvertFrom-Json).version).Trim()
$head = (& git rev-parse --short HEAD).Trim()
$originMain = (& git rev-parse --short origin/main).Trim()

Write-Step "发布目标：$tag（仓库 $Repo）"
Write-Host "  仓库根      : $root"
Write-Host "  HEAD        : $head"
Write-Host "  origin/main : $originMain"
Write-Host "  version.json: $repoVersion"

# ---- 1. 前置校验（确定性失败，不重试）----
Write-Step '1. 前置校验'
if ($repoVersion -ne $Version) {
    Fail "version.json 是 $repoVersion，但本次发布目标写的是 $Version —— 两者必须一致（版本真源是 version.json）"
    exit 1
}
$localTag = ((& git tag --list $tag) -join '').Trim()
if ([string]::IsNullOrWhiteSpace($localTag)) {
    if ($SkipTag) { Skip "本地标签 $tag 不存在（按参数跳过推送标签）" }
    else { Fail "本地没有标签 $tag —— 先跑 scripts/release.ps1 打完标签再发布（本脚本不打标签）" }
}
else { Pass "本地标签存在：$tag" }

$dirty = @(& git status --porcelain)
if ($dirty.Count -gt 0) { Fail "工作区不干净（$($dirty.Count) 项）——发布必须基于确定的提交：`n$($dirty -join "`n")" }
else { Pass '工作区干净' }

if (-not $VerifyOnly -and -not $SkipPush) {
    # ---- 2. 推送主分支 ----
    Write-Step '2. 推送主分支'
    $push = Invoke-WithRetry -Label 'git push origin main' -Action { & git push origin main }
    if ($push.Ok) { Pass 'main 已推送' } else { Fail "推送 main 失败：$($push.Output)" }
}

# ---- 3. 推送标签 ----
if (-not $script:failed -and -not $VerifyOnly -and -not $SkipTag) {
    Write-Step '3. 推送标签'
    if (-not $localTag) {
        Fail "本地没有标签 $tag，无法推送——先跑 scripts/release.ps1 打完标签"
    }
    else {
        $remoteProbe = Test-RemoteTag -Tag $tag
        if (-not $remoteProbe.Ok) {
            Fail "无法确认远端是否已有标签（网络失败，不是「标签不存在」）：$($remoteProbe.Output)"
        }
        elseif ($remoteProbe.Exists) {
            Skip "远端已有标签 $tag（不覆盖；要重打请先按 SKILL.md 第七节回滚）"
        }
        else {
            $pushTag = Invoke-WithRetry -Label "git push origin $tag" -Action { & git push origin $tag }
            if ($pushTag.Ok) { Pass "标签 $tag 已推送" } else { Fail "推送标签失败：$($pushTag.Output)" }
        }
    }
}

# ---- 4. 建 GitHub Release ----
if (-not $script:failed -and -not $VerifyOnly -and $CreateRelease) {
    Write-Step '4. 建 GitHub Release'

    $changelogPath = Join-Path $root 'CHANGELOG.md'
    $lines = @(Get-Content $changelogPath -Encoding utf8)
    $startIndex = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "^##\s*\[$([regex]::Escape($Version))\]") { $startIndex = $i; break }
    }
    if ($startIndex -lt 0) {
        Fail "CHANGELOG.md 里找不到 [${Version}] 节 —— 定稿后再建 Release（说明必须取自该节）"
    }
    else {
        $endIndex = $lines.Count - 1
        for ($i = $startIndex + 1; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^##\s*\[' -or $lines[$i] -match '^\[') { $endIndex = $i - 1; break }
        }
        $section = ($lines[$startIndex..$endIndex] -join "`n").Trim()
        $notes = "# EOS $tag`n`n$section"
        $notesPath = Join-Path $env:TEMP "eos-$tag-release-notes.md"
        Set-Content -Path $notesPath -Value $notes -Encoding utf8
        Write-Host "  说明取自 CHANGELOG 第 $($startIndex + 1)–$($endIndex + 1) 行 → $notesPath"

        $assets = @()
        foreach ($name in @('MANIFEST.txt', 'SHA256SUMS.txt')) {
            $candidate = Join-Path $artifacts $name
            if (Test-Path $candidate) { $assets += $candidate } else { Skip "产物缺失（不作为附件）：$name" }
        }
        if ($assets.Count -eq 0) { Skip '没有可用附件——先跑 scripts/release.ps1 生成产物清单' }

        $exists = (& $ghCmd release view $tag --repo $Repo 2>$null | Out-String).Trim()
        if ($exists) {
            $edit = Invoke-WithRetry -Label 'gh release edit' -Action {
                & $ghCmd release edit $tag --repo $Repo --title "EOS $tag" --notes-file $notesPath
            }
            if ($edit.Ok) { Pass "Release 已更新：$tag" } else { Fail "更新 Release 失败：$($edit.Output)" }
        }
        else {
            $create = Invoke-WithRetry -Label 'gh release create' -Action {
                if ($assets.Count -gt 0) {
                    & $ghCmd release create $tag --repo $Repo --title "EOS $tag" --notes-file $notesPath @assets
                }
                else {
                    & $ghCmd release create $tag --repo $Repo --title "EOS $tag" --notes-file $notesPath
                }
            }
            if ($create.Ok) { Pass "Release 已创建：$($create.Output -split "`n" | Select-Object -Last 1)" }
            else { Fail "创建 Release 失败：$($create.Output)" }
        }
    }
}

# ---- 5. 判定性核对 ----
Write-Step '5. 核对'
$tagCommit = (& git rev-list -n1 $tag 2>$null | Out-String).Trim()
if ($tagCommit) {
    Pass "标签 $tag → $($tagCommit.Substring(0,7))"
    $contains = (& git branch -r --contains $tagCommit 2>$null | Out-String)
    if ($contains -match 'origin/main') { Pass '该提交在 origin/main 上' } else { Fail '该提交不在 origin/main 上——标签可能指向未推送的提交' }
}
else { Fail "本地找不到标签 $tag 的指向提交" }

$remoteProbe = Test-RemoteTag -Tag $tag
if (-not $remoteProbe.Ok) {
    Fail "无法确认远端标签状态（网络失败，不是「标签不存在」）：$($remoteProbe.Output)"
}
elseif ($remoteProbe.Exists) { Pass '远端已有该标签' }
elseif (-not $SkipTag) { Fail '远端没有该标签' }
else { Skip '按参数未推送标签' }

$counts = ((& git rev-list --left-right --count 'origin/main...HEAD' 2>$null | Out-String).Trim() -split '\s+')
if ($counts.Count -ge 2 -and $counts[0] -eq '0' -and $counts[1] -eq '0') { Pass '本地与 origin/main 已同步' }
else { Fail "本地与 origin/main 未同步（远端新 $($counts[0]) / 本地新 $($counts[1])）" }

if ($ghCmd -and (Get-Command $ghCmd -ErrorAction SilentlyContinue) -and -not $script:failed) {
    $view = (& $ghCmd release view $tag --repo $Repo --json tagName,isDraft,assets 2>$null | Out-String).Trim()
    if ($view) {
        $parsed = $view | ConvertFrom-Json
        $assetNames = @($parsed.assets | ForEach-Object { $_.name })
        Pass "Release $($parsed.tagName)：draft=$($parsed.isDraft)，附件=[$($assetNames -join ', ')]"
    }
    else { Skip 'Release 尚未创建（或 gh 查询失败）' }
}

Write-Host ''
if ($script:failed) { Write-Host '== 有失败项，见上方 FAIL ==' -ForegroundColor Red; exit 1 }
Write-Host '== 发布助手完成（0 FAIL）==' -ForegroundColor Green
exit 0
