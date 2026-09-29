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

迁移与版本的关系：**迁移编号不参与版本号**（前者是 DbUp 的执行顺序键、记在 ERP_SCHEMA_JOURNAL，
后者是给人看的产品版本）。但 ADR-026 §1.2 要求发布说明标注"是否含迁移、迁移编号区间"，
故本脚本承担两件与迁移有关的事：
  ① 门禁：仓库内嵌迁移脚本与库内台账必须一一对应——台账多出脚本 ⇒ 有人改过/删过已执行脚本
     （新库会永远建不起来）；磁盘多出脚本 ⇒ 有迁移还没落库（先重启 EOS.API 再发布）；
  ② 产物：CHANGELOG 草稿与 MANIFEST 自动带上"自上个标签起新增的迁移编号区间与数量"。
连库只读（一条 SELECT），连不上即失败；-DryRun 下跳过该项。

脚本刻意不做的事：不自动 git push、不部署、不重启服务、不改库。推送与否由人决定。

.EXAMPLE
pwsh scripts/release.ps1 -DryRun                 # 只看会怎么涨、会生成什么，不写任何文件
pwsh scripts/release.ps1 -Bump minor             # 升 MINOR 并生成 CHANGELOG 节
pwsh scripts/release.ps1 -Bump minor -Tag        # 再打附注标签 v<version>
pwsh scripts/release.ps1 -DryRun -SkipBuild -SkipMigrationGate   # 只验提交分析与草稿文本
#>
param(
    [ValidateSet('auto', 'major', 'minor', 'patch')]
    [string]$Bump = 'auto',
    [switch]$Tag,
    [switch]$DryRun,
    [switch]$SkipBuild,
    [switch]$SkipMigrationGate
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

# ---------------------------------------------------------------------------
# 迁移：仓库内嵌脚本 vs 库内台账
# ---------------------------------------------------------------------------
# DbUp 的判重身份是**资源名**（= 命名空间 + 文件名），台账里存的就是这个形态。
# 台账只记文件名而资源名对不上属理论情况（手工改过台账），报出来让人核对即可。
function Get-RepoMigrations {
    $migrationDir = Join-Path $root 'EOS.API/Data/Migrations'
    return @(Get-ChildItem -Path $migrationDir -Filter '*.sql' -File | Sort-Object Name | ForEach-Object {
            $number = if ($_.Name -match '^(\d+)_') { [int]$Matches[1] } else { $null }
            [pscustomobject]@{
                FileName = $_.Name
                Number   = $number
                Resource = "EOS.API.Data.Migrations.$($_.Name)"
                Added    = $_.LastWriteTime
            }
        })
}

# 台账解析用 CONCAT 合成单列：多列查询在托管回退路径下按制表符分隔，单列最稳。
# 读的是库自身信息（DbUp 的表），与业务数据无关，不需要库名守护。
function Get-JournalScripts {
    $connectionString = [Environment]::GetEnvironmentVariable('MSSQL_ERP_CONN', 'User')
    if (-not $connectionString) { $connectionString = $env:MSSQL_ERP_CONN }
    if (-not $connectionString) {
        $connectionString = 'Server=localhost;Database=EOS.ERP;Integrated Security=True;Encrypt=False;TrustServerCertificate=True'
    }
    elseif ($connectionString -notmatch '(?i)(^|;)\s*encrypt\s*=') {
        $connectionString = $connectionString.TrimEnd(';') + ';Encrypt=False;TrustServerCertificate=True'
    }
    $connection = New-Object System.Data.SqlClient.SqlConnection $connectionString
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = "SET NOCOUNT ON; SELECT CONCAT(ScriptName, '|', CONVERT(varchar(23), Applied, 120)) FROM dbo.ERP_SCHEMA_JOURNAL"
        $command.CommandTimeout = 30
        $reader = $command.ExecuteReader()
        $rows = New-Object System.Collections.Generic.List[string]
        try {
            while ($reader.Read()) {
                if (-not $reader.IsDBNull(0)) { $rows.Add([string]$reader.GetValue(0)) }
            }
        }
        finally { $reader.Dispose(); $command.Dispose() }
        return $rows
    }
    finally { $connection.Dispose() }
}

# 迁移号区间文本：连续取 "283–284"，只有一个取 "284"，没有取 $null。
function Format-MigrationRange($numbers) {
    $list = @($numbers | Where-Object { $null -ne $_ } | Sort-Object -Unique)
    if ($list.Count -eq 0) { return $null }
    if ($list.Count -eq 1) { return [string]$list[0] }
    return "$($list[0])–$($list[-1])"
}

# 上个标签之后新增的迁移（没有标签时= 全部，与 CHANGELOG 首版不生成全历史草稿的口径一致：
# 首版由人写基线说明，这里只回 [0.1.0] 之前已存在的脚本，不当作"本版新增"）。
function Get-NewMigrationsSinceTag([string]$tag, $repoMigrations) {
    if ([string]::IsNullOrWhiteSpace($tag)) { return @() }
    $changed = @(git -C $root diff --name-only --diff-filter=A "$tag..HEAD" -- 'EOS.API/Data/Migrations/*.sql' 2>$null)
    $names = @($changed | ForEach-Object { Split-Path $_ -Leaf })
    return @($repoMigrations | Where-Object { $names -contains $_.FileName })
}

# 迁移门禁：两向集合比对。台账多出 → 已执行脚本被改过/删过（新库永远建不起来）；
# 磁盘多出 → 有迁移没落库（先重启 EOS.API 再发布）。
function Step-MigrationGate($repoMigrations) {
    $repoNames = @($repoMigrations | ForEach-Object { $_.Resource })
    $journal = @()
    try { $journal = @(Get-JournalScripts) }
    catch {
        Fail "连不上库，无法核对迁移台账（$($_.Exception.Message))——发布必须确认库与代码一致；确要跳过加 -SkipMigrationGate"
        return @{ Reachable = $false; Journal = @() }
    }
    $journalNames = @($journal | ForEach-Object { ($_ -split '\|')[0] })
    $missingOnDisk = @($journalNames | Where-Object { $repoNames -notcontains $_ })
    $notApplied = @($repoNames | Where-Object { $journalNames -notcontains $_ })

    if ($missingOnDisk.Count -gt 0) {
        Fail "台账里有 $($missingOnDisk.Count) 个脚本在仓库中已不存在——已执行的迁移脚本被改名或删除，全新环境将无法建库："
        $missingOnDisk | Select-Object -First 5 | ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
    }
    if ($notApplied.Count -gt 0) {
        Fail "仓库有 $($notApplied.Count) 个迁移尚未落库——重启 EOS.API 让 DbUp 执行后再发布："
        $notApplied | ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
    }
    if ($missingOnDisk.Count -eq 0 -and $notApplied.Count -eq 0) {
        Pass "迁移台账与仓库内嵌脚本一一对应（$($repoNames.Count) 个）"
    }
    return @{ Reachable = $true; Journal = $journal }
}

# ---------------------------------------------------------------------------
# 提交分析与版本递增
# ---------------------------------------------------------------------------
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

function Step-ChangelogSection($subjects, [string]$version, [string]$previousTag, $migrations = @()) {
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
    # 迁移信息进草稿头部：ADR-026 §1.2 要求发布说明标注"是否含迁移、迁移编号区间"，
    # 由脚本先落一行事实，人工定稿时改写措辞即可，不必自己去数。
    if ($migrations.Count -gt 0) {
        $dbLine = "> 数据库：本版含迁移 **$(Format-MigrationRange ($migrations | ForEach-Object { $_.Number }))**（共 $($migrations.Count) 个），部署时由 EOS.API 启动自动执行。"
    }
    else {
        $dbLine = '> 数据库：本版**不含**迁移。'
    }
    $lines = @("## [$version] - $(Get-Date -Format 'yyyy-MM-dd')", '',
        "> 草稿：由提交信息按类型分组生成（$head，共 $($subjects.Count) 条）。",
        $dbLine,
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

function Step-Artifacts([string]$version, $repoMigrations) {
    $outDir = Join-Path $root "artifacts/release-$version"
    if ($DryRun) { Skip "产物目录（DryRun 未创建）：$outDir"; return }
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $commit = (git -C $root rev-parse --short HEAD).Trim()
    # 迁移数量与最大编号从磁盘现算：写死会在下一个迁移落地时静默过期（本文件早先就写死过 282）
    $headNumber = ($repoMigrations | Where-Object { $null -ne $_.Number } |
        Sort-Object Number | Select-Object -Last 1).Number
    $manifest = Join-Path $outDir 'MANIFEST.txt'
    @(
        "EOS release $version"
        "commit: $commit"
        "built: $(Get-Date -Format 'yyyy-MM-ddTHH:mm:ssK')"
        "api: EOS.API 构建产物（含内嵌迁移脚本 / ReportFormats / 字体）"
        "web: EOS.Web 静态产物"
        "db: 迁移由 EOS.API 启动时执行；仓库内嵌迁移 $($repoMigrations.Count) 个（最新编号 $headNumber）；db/bootstrap 基线登记到迁移 268，其后的脚本靠启动时 DbUp 补齐"
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

    $repoMigrations = @(Get-RepoMigrations)

    Write-Step '1. 迁移门禁（仓库内嵌脚本 vs 库内台账）'
    if ($SkipMigrationGate) { Skip '按参数跳过迁移门禁' }
    else { [void](Step-MigrationGate $repoMigrations) }
    if ($script:exitCode -ne 0) { return }

    Write-Step '2. 门禁（编译 + 静态检查）'
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
        Step-Artifacts $current $repoMigrations
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

    $mig = @(Get-NewMigrationsSinceTag $previousTag $repoMigrations)
    if ($mig.Count -gt 0) {
        Pass "本版含迁移 $(Format-MigrationRange ($mig | ForEach-Object { $_.Number }))（共 $($mig.Count) 个）"
    }
    else { Write-Host '  本版不含迁移' -ForegroundColor Gray }

    $subjects = @(Get-CommitsSince $previousTag)
    $section = Step-ChangelogSection $subjects $next $previousTag $mig
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

    Step-Artifacts $next $repoMigrations

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
    # Pop-Location 必须执行，它自己不能失败（否则连退出码都拿不到）；
    # exit 必须写在这里、不能写在 finally 之后——try 里的 return 会**跳过 finally 之后的语句**，
    # 于是"门禁失败设了 exitCode=1"也会以退出码 0 结束（发布静默成功）。实测：
    #   try { if (cond) { return } } finally { ... } ; exit 1   # 直接 return 时 exit 1 永不执行
    Pop-Location
    exit $script:exitCode
}
