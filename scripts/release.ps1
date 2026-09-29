#Requires -Version 7.0
<#
.SYNOPSIS
EOS 发布脚本：跑门禁 → 校验版本一致性 → 升 version.json → 生成 CHANGELOG 节 → 打标签 → 产出清单。

.DESCRIPTION
按 docs/decisions/ADR-026-版本管理与发布流程.md 执行：仓库根 version.json 是唯一版本真源，
发布标签为 v<version>，CHANGELOG 由提交历史生成草稿、人工定稿。

版本递增按提交类型（ADR-026 §2.6）：
  feat → MINOR+1；fix/perf → PATCH+1；BREAKING（type 后带 ! 或正文含 BREAKING CHANGE）→
  0.x 期间 MINOR+1；重构/文档/构建/测试不单独涨。

脚本刻意不做的事：不自动 git push、不部署、不重启服务、不改库。推送与否由人决定。

.EXAMPLE
pwsh scripts/release.ps1 -DryRun                 # 只看会怎么涨、会生成什么，不写任何文件
pwsh scripts/release.ps1 -Bump minor             # 升 MINOR 并生成 CHANGELOG 节
pwsh scripts/release.ps1 -Bump minor -Tag        # 再打附注标签 v<version>
#>
param(
    [ValidateSet('auto', 'major', 'minor', 'patch')]
    [string]$Bump = 'auto',
    [switch]$Tag,
    [switch]$DryRun,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$versionFile = Join-Path $root 'version.json'
$changelogFile = Join-Path $root 'CHANGELOG.md'
# 用返回值而不是 exit：exit 会跳过 finally 里的 Pop-Location，留下错乱的目录栈
$script:exitCode = 0

function Write-Step([string]$text) { Write-Host "== $text" -ForegroundColor Cyan }
function Fail([string]$text) { Write-Host "FAIL $text" -ForegroundColor Red; $script:exitCode = 1 }
function Pass([string]$text) { Write-Host "PASS $text" -ForegroundColor Green }
function Skip([string]$text) { Write-Host "SKIP $text" -ForegroundColor Yellow }

function Get-RepoVersion {
    if (-not (Test-Path $versionFile)) { Fail "version.json 不存在：$versionFile" }
    $parsed = Get-Content $versionFile -Raw -Encoding utf8 | ConvertFrom-Json
    $version = [string]$parsed.version
    if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
        Fail "version.json 的 version 不是 MAJOR.MINOR.PATCH 形式：'$version'"
    }
    return $version
}

function Get-LastReleaseTag {
    $tags = @(git -C $root tag --list 'v*' --sort=-v:refname)
    if ($tags.Count -eq 0) { return $null }
    return $tags[0]
}

function Get-CommitsSince([string]$tag) {
    $range = if ([string]::IsNullOrWhiteSpace($tag)) { 'HEAD' } else { "$tag..HEAD" }
    $lines = @(git -C $root log $range --no-merges --format='%s' 2>$null)
    return $lines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}

# 首次发布（没有任何 v* 标签）不做全历史草稿：把全部提交堆进 CHANGELOG 首节既不可读，
# 也不是"这一版新增了什么"。首版节由人写基线说明，脚本只提示。
function Test-FirstRelease { return $null -eq (Get-LastReleaseTag) }

function Get-BumpFromCommits($subjects) {
    $hasFeature = $false
    $hasFix = $false
    foreach ($subject in $subjects) {
        if ($subject -match '^(\w+)(\([^)]*\))?!:' ) { return 'major' }
        if ($subject -match 'BREAKING[ -]CHANGE') { return 'major' }
        if ($subject -match '^feat(\([^)]*\))?:') { $hasFeature = $true }
        if ($subject -match '^(fix|perf)(\([^)]*\))?:') { $hasFix = $true }
    }
    if ($hasFeature) { return 'minor' }
    if ($hasFix) { return 'patch' }
    return 'none'
}

function Step-Version([string]$current) {
    $major, $minor, $patch = ($current -split '[.-]')[0..2] | ForEach-Object { [int]$_ }
    $lastTag = Get-LastReleaseTag
    $subjects = @(Get-CommitsSince $lastTag)
    Write-Step "提交分析（自 $(if ($lastTag) { $lastTag } else { '仓库起点' }) 起，共 $($subjects.Count) 条）"

    $suggested = if ($Bump -eq 'auto') { Get-BumpFromCommits $subjects } else { $Bump }
    if ($suggested -eq 'none') {
        Skip '自上次发布以来没有 feat/fix/perf 提交——按 ADR-026 §2.6 无需升版本'
        return $current
    }
    if ($suggested -eq 'major' -and $major -eq 0) {
        Write-Host '  提交含 BREAKING：0.x 期间按 MINOR 表达（ADR-026 §2.6）' -ForegroundColor Yellow
        $suggested = 'minor'
    }
    switch ($suggested) {
        'major' { $major++; $minor = 0; $patch = 0 }
        'minor' { $minor++; $patch = 0 }
        'patch' { $patch++ }
    }
    $next = "$major.$minor.$patch"

    if ([version]$next -le [version]$current) {
        Fail "新版本 $next 不大于当前 $current——请显式指定 -Bump，或先合入改动"
    }
    Pass "版本：$current → $next（依据：$suggested）"
    return $next
}

function Step-ChangelogSection($subjects, [string]$version, [string]$previousTag) {
    $groups = [ordered]@{ Added = @(); Changed = @(); Fixed = @(); 'Removed' = @(); Security = @() }
    foreach ($subject in $subjects) {
        if ($subject -match '^(\w+)(\([^)]*\))?!?:\s*(.+)$') {
            $type = $Matches[1]
            $text = $Matches[3].Trim()
        } else {
            $type = 'other'
            $text = $subject.Trim()
        }
        switch -Regex ($type) {
            '^(feat)$' { $groups.Added += $text }
            '^(fix)$' { $groups.Fixed += $text }
            '^(perf|refactor)$' { $groups.Changed += $text }
            '^(revert)$' { $groups.Removed += $text }
            default { }
        }
    }
    $head = if ($previousTag) { "自 $previousTag 起" } else { '自仓库起点起' }
    $lines = @("## [$version] - $(Get-Date -Format 'yyyy-MM-dd')", '',
        "> 草稿：由提交信息按类型分组生成（$head，共 $($subjects.Count) 条）。",
        '> **人工定稿要求**：删掉面向内部的条目、把面向用户的改动改写成业务语言；',
        '> 破坏性变更必须在下方新增 **BREAKING** 小节说明影响与迁移方式。', '')
    $labels = @{ Added = '新增'; Changed = '变更'; Fixed = '修复'; Removed = '移除'; Security = '安全' }
    foreach ($key in $groups.Keys) {
        if ($groups[$key].Count -eq 0) { continue }
        $lines += "### $($labels[$key])"
        $lines += ''
        foreach ($item in $groups[$key]) { $lines += "- $item" }
        $lines += ''
    }
    if ($groups.Added.Count + $groups.Changed.Count + $groups.Fixed.Count + $groups.Removed.Count -eq 0) {
        $lines += '### 变更'
        $lines += ''
        foreach ($subject in $subjects) { $lines += "- $subject" }
        $lines += ''
    }
    return ($lines -join "`n")
}

function Step-Artifacts([string]$version) {
    $outDir = Join-Path $root "artifacts/release-$version"
    if ($DryRun) { Skip "产物目录（DryRun 未创建）：$outDir"; return }
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $commit = (git -C $root rev-parse --short HEAD).Trim()
    $manifest = Join-Path $outDir 'MANIFEST.txt'
    @(
        "EOS release $version"
        "commit: $commit"
        "built: $(Get-Date -Format 'yyyy-MM-ddTHH:mm:ssK')"
        "api: EOS.API 构建产物（含内嵌迁移脚本 / ReportFormats / 字体）"
        "web: EOS.Web 静态产物"
        "db: 迁移由 EOS.API 启动时执行；db/bootstrap 基线登记到迁移 268（落后于 Data/Migrations 的 282，新库靠启动补齐）"
        "note: 制品与清单不含连接串、密钥或 appsettings.Development*.json"
    ) | Set-Content -Path $manifest -Encoding utf8
    Get-ChildItem $outDir -File | ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        "$hash  $($_.Name)"
    } | Add-Content -Path (Join-Path $outDir 'SHA256SUMS.txt') -Encoding utf8
    Pass "产物清单：$outDir"
}

try {
    Push-Location $root
    Write-Step '0. 仓库前置'
    $dirty = @(git status --porcelain)
    if ($dirty.Count -gt 0 -and -not $DryRun) { Fail "工作区有未提交改动（$($dirty.Count) 项）——发布必须基于确定的提交" }
    $ahead = (git rev-list --left-right --count 'origin/main...HEAD') -split '\s+'
    if ($ahead.Count -ge 2 -and [int]$ahead[1] -gt 0) {
        if ($DryRun) { Skip "本地领先 origin/main $($ahead[1]) 个提交（发布前必须先推送，否则标签指向远端不存在的提交）" }
        else { Fail "本地领先 origin/main $($ahead[1]) 个提交——先 git push，再发布" }
    }
    Pass '工作区与远端同步前置通过'
    if ($script:exitCode -ne 0) { return }

    Write-Step '1. 门禁（编译 + 静态检查）'
    if ($SkipBuild) { Skip '按参数跳过构建' }
    elseif ($script:exitCode -eq 0) {
        dotnet build EOS.API/EOS.API.csproj --nologo -v:q | Out-Host
        if ($LASTEXITCODE -ne 0) { Fail 'dotnet build EOS.API 失败' }
        if ($script:exitCode -eq 0) {
            Push-Location EOS.Web
            npm run lint | Out-Host
            if ($LASTEXITCODE -ne 0) { Fail 'npm run lint 失败' }
            if ($script:exitCode -eq 0) {
                npm run build | Out-Host
                if ($LASTEXITCODE -ne 0) { Fail 'npm run build 失败' }
            }
            Pop-Location
        }
        if ($script:exitCode -eq 0) { Pass '编译与静态检查通过' }
    }

    $current = Get-RepoVersion
    if ($script:exitCode -ne 0 -or [string]::IsNullOrWhiteSpace($current)) { return }
    Write-Host "  当前版本：$current" -ForegroundColor Gray

    if (Test-FirstRelease) {
        Write-Step '首次发布（仓库无 v* 标签）'
        Write-Host '  没有历史标签 ⇒ 无从计算"自上次发布以来的改动"，本版按基线版处理：' -ForegroundColor Gray
        Write-Host '  ① CHANGELOG 首节由人撰写基线说明（不生成全历史草稿，打标签时才升版本）；' -ForegroundColor Gray
        Write-Host '  ② 产物清单：pwsh scripts/release.ps1 -Tag 会在门禁通过后升版本、写节、打标签。' -ForegroundColor Gray
        if ($DryRun) { Skip 'DryRun：未写任何文件'; return }
        Step-Artifacts $current
        if ($Tag) {
            git tag -a "v$current" -m "EOS v$current" | Out-Host
            Pass "已打附注标签 v$current（**尚未推送**：git push origin v$current）"
        }
        else {
            Skip '未打标签（需要时加 -Tag）'
        }
        return
    }

    $previousTag = Get-LastReleaseTag
    $tagVersion = $previousTag.TrimStart('v')
    if ($tagVersion -ne $current) {
        Write-Host "  提示：最新标签 $previousTag 与 version.json（$current）不一致——" -ForegroundColor Yellow
        Write-Host '        这属开发态（版本已升、标签未打）或漏打标签，请自行核对后继续。' -ForegroundColor Yellow
    }

    $next = Step-Version $current
    if ($script:exitCode -ne 0) { return }
    if ($next -eq $current) { Pass '无版本变化，流程结束'; return }

    $subjects = @(Get-CommitsSince $previousTag)
    $section = Step-ChangelogSection $subjects $next $previousTag
    if ($DryRun) {
        Write-Host $section
        Skip 'DryRun：未写入 version.json / CHANGELOG.md，未打标签'
        return
    }

    "{`n  `"version`": `"$next`"`n}" | Set-Content -Path $versionFile -Encoding utf8
    $existing = Get-Content $changelogFile -Raw -Encoding utf8
    $marker = '<!-- 新增版本时在文件顶部（本行之上）追加一节，勿修改历史节。 -->'
    $updated = $existing.Replace($marker, "$marker`n`n$section")
    Set-Content -Path $changelogFile -Value $updated -Encoding utf8
    Pass "version.json → $next；CHANGELOG 已追加 $next 节（**需人工定稿**）"

    Step-Artifacts $next

    if ($Tag) {
        git add $versionFile $changelogFile
        git commit -m "chore(release): 发布 v$next" | Out-Host
        git tag -a "v$next" -m "EOS v$next" | Out-Host
        Pass "已提交并打附注标签 v$next（**尚未推送**：git push && git push origin v$next）"
    }
    else {
        Skip '未打标签（需要时加 -Tag）'
    }
}
catch {
    Write-Host "FAIL $($_.Exception.Message)" -ForegroundColor Red
    $script:exitCode = 1
}
finally {
    Pop-Location
}

exit $script:exitCode
