#Requires -Version 7.0
<#
.SYNOPSIS
EOS 发布脚本：跑门禁 → 校验版本一致性 → 升 version.json → 生成 CHANGELOG 节 → 产出清单；
定稿提交之后用 -TagOnly 打标签。**分两步是刻意的**：标签必须落在「CHANGELOG 定稿」那一笔上，
而本节生成的是**草稿**（发给用户的说明还没改写、内部条目还没删），草稿提交不该被标成发布点。

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

脚本刻意不做的事：不自动 git push、不部署、不重启服务、不改库。推送与否由人决定
（推送与建 GitHub Release 由 `.agents/skills/eos-release/scripts/` 下的发布助手接管）。

流程共 9 步（每步失败即停，退出码 1）：0 仓库前置 → 1 迁移门禁 → 2 编译与静态检查 →
3 手册新鲜度（默认仅提示）→ 4 版本判定 → 5 CHANGELOG 节 →
6 写 version.json / CHANGELOG / README 的版本引用 → 7 产物与清单。
打标签是**独立的第二步**（-TagOnly），不在本流程里。

写版本号的文件由本脚本统一维护：`version.json`（真源）、`CHANGELOG.md`（新增一节）、
`README.md`（项目状态行的版本引用）。README 那个号也曾被人手写、然后就停在 `v0.1` 跨越了整个
0.2.0 开发周期——所以它改由脚本改写，并由 `scripts/check-docs.ps1` 校验与真源一致。

.EXAMPLE
pwsh scripts/release.ps1 -DryRun                 # 只看会怎么涨、会生成什么，不写任何文件
# 第 1 步（服务在跑时产物落到仓库外，避开被锁的 bin）：
pwsh scripts/release.ps1 -Bump auto -BuildOutput "$env:TEMP\eos-release-build"
# 第 2 步（人工定稿 CHANGELOG 并提交之后）：
pwsh scripts/release.ps1 -TagOnly -Version 0.2.0
pwsh scripts/release.ps1 -DryRun -SkipBuild -SkipMigrationGate   # 只验提交分析与草稿文本
#>
param(
    [ValidateSet('auto', 'major', 'minor', 'patch')]
    [string]$Bump = 'auto',
    [switch]$Tag,
    # 只打标签：给"CHANGELOG 定稿并提交之后"这一步用。不升版本、不改文件，
    # 但会把该断言的都断言掉（工作区干净 / version.json 与 -Version 一致 /
    # 该版本节已定稿而非草稿 / 标签未被占用），并重算产物清单让它记下最新提交。
    [switch]$TagOnly,
    # 配合 -TagOnly：断言 version.json 就是它，防止把别的版本号打上去。
    [string]$Version,
    [switch]$DryRun,
    [switch]$SkipBuild,
    [switch]$SkipMigrationGate,
    # 把 API 构建产物落到仓库外的目录。运行中的 EOS.API 会锁住 bin/Debug 下的
    # EOS.API.exe，默认构建因此报 MSB3027/MSB3021（拷贝失败，不是编译失败）；
    # 指定本参数后构建照常做、只是不往被锁的目录写，门禁的判别力得以保留。
    [string]$BuildOutput,
    # 手册新鲜度默认**非阻断**（它是"该改没改文档"的提示，不是发布正确性的一部分：
    # 文档在别的提交里落后，不该拦住一次已经就绪的发布）。加本开关恢复阻断语义。
    [switch]$StrictGuide
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$versionFile = Join-Path $root 'version.json'
$changelogFile = Join-Path $root 'CHANGELOG.md'
$readmeFile = Join-Path $root 'README.md'
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

# README 项目状态行里的版本引用。README 要给读者一个版本号，而版本真源是 version.json；
# 两者由"发布时在这里改写 + check-docs.ps1 校验一致"绑定，手写就必然漂移。
# 格式固定为「项目状态：早期开发阶段（vX.Y.Z）」——正则要能解析，改格式会让发布失败。
#
# 首尾拆成 head/tail 两组：`-replace` 替换的是**整个匹配**，替换串必须把首尾原样拼回去
# （曾经只写 `v${version}`，于是"项目状态：早期开发阶段（"和"）"被整段吃掉），
# 而替换串里的 `${version}` 是 .NET 的**捕获组**（旧号），不是 PowerShell 的新号——两个坑叠在一起。
$readmeVersionPattern = '(?<head>项目状态：早期开发阶段（v)(?<version>\d+\.\d+\.\d+)(?<tail>）)'

function Step-ReadmeVersion([string]$version) {
    if (-not (Test-Path $readmeFile)) {
        Fail "README.md 不存在——版本引用无处可写"
        return
    }
    $readme = Get-Content -Raw -Encoding utf8 $readmeFile
    if ($readme -notmatch $readmeVersionPattern) {
        Fail "README.md 里找不到版本引用（期望「项目状态：早期开发阶段（v$version）」）——格式被改过就同步不了，先改回再发布"
        return
    }
    # 单引号给出 .NET 的具名组引用，$version 走 PowerShell 插值给出新号。
    # head 组里已经含那个 "v"，这里不能再补一个（补了会得到"（vv9.10.0）"）。
    $updated = $readme -replace $readmeVersionPattern, ('${head}' + $version + '${tail}')
    # 落盘前自检：把改写后的整段拿出来与期望串逐字比对。这类"整段替换"写错会静默吃掉正文
    # 或多拼一个字符，而替换动作本身不会报错——只比对版本号数字的话，多一个 v 是查不出来的。
    $check = [regex]::Match($updated, $readmeVersionPattern)
    $expected = "项目状态：早期开发阶段（v$version）"
    if (-not $check.Success -or $check.Value -ne $expected) {
        Fail "README.md 改写后不是「$expected」（实际匹配到「$($check.Value)」）——**未写盘**，请检查版本引用格式"
        return
    }
    if ($DryRun) { Skip "README.md 版本引用（DryRun 未改写）：→ v$version"; return }
    # -NoNewline：Get-Content -Raw 已含文件末尾换行，再补一个会让 README 每次发布多一行空行
    Set-Content -Path $readmeFile -Value $updated -Encoding utf8 -NoNewline
    Pass "README.md 版本引用 → v$version"
}

function Step-Artifacts([string]$version, $repoMigrations) {
    $outDir = Join-Path $root "artifacts/release-$version"
    if ($DryRun) { Skip "产物目录（DryRun 未创建）：$outDir"; return }
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $commit = (git -C $root rev-parse --short HEAD).Trim()
    # 迁移数量与最大编号从磁盘现算：写死会在下一个迁移落地时静默过期（本文件早先就写死过 282）
    $headNumber = ($repoMigrations | Where-Object { $null -ne $_.Number } |
        Sort-Object Number | Select-Object -Last 1).Number
    # 基线覆盖到哪个编号同样从文件现算：这里曾写死 268，而 324~329 落地时基线被重导到 329，
    # 写死的数字会让清单静默说谎（与上一行注释记的是同一个坑）。
    $baselineFile = Join-Path $root 'db\bootstrap\40_journal_baseline.sql'
    $baselineMax = $null
    if (Test-Path -LiteralPath $baselineFile) {
        $baselineMax = ([regex]::Matches((Get-Content -Raw -Encoding utf8 $baselineFile), 'Migrations\.(\d+)_') |
            ForEach-Object { [int]$_.Groups[1].Value } | Sort-Object | Select-Object -Last 1)
    }
    $dbLine = if ($null -eq $baselineMax) {
        "db: 迁移由 EOS.API 启动时执行；仓库内嵌迁移 $($repoMigrations.Count) 个（最新编号 $headNumber）；db/bootstrap 基线文件读不到，未标注覆盖范围"
    }
    elseif ($baselineMax -ge $headNumber) {
        "db: 迁移由 EOS.API 启动时执行；仓库内嵌迁移 $($repoMigrations.Count) 个（最新编号 $headNumber）；db/bootstrap 基线已覆盖到 $baselineMax，新库灌完基线即终态、零迁移"
    }
    else {
        "db: 迁移由 EOS.API 启动时执行；仓库内嵌迁移 $($repoMigrations.Count) 个（最新编号 $headNumber）；db/bootstrap 基线登记到迁移 $baselineMax，其后的脚本靠启动时 DbUp 补齐"
    }
    $manifest = Join-Path $outDir 'MANIFEST.txt'
    @(
        "EOS release $version"
        "commit: $commit"
        "built: $(Get-Date -Format 'yyyy-MM-ddTHH:mm:ssK')"
        "api: EOS.API 构建产物（含内嵌迁移脚本 / ReportFormats / 字体）"
        "web: EOS.Web 静态产物"
        $dbLine
        # 单引号：这一行里的 ${VAR} 是给人看的配置写法，写成双引号会被 PowerShell 当变量展开成空串
        'note: 制品与清单不含连接串与密钥（配置只写 ${VAR} 环境变量引用，真值只存在于环境变量）'
    ) | Set-Content -Path $manifest -Encoding utf8
    # 本步骤会在定稿提交之后再跑一次（-TagOnly 要把 commit 刷成定稿那笔），
    # 所以校验和清单必须**覆盖**而不是追加，且不把自己算进去。
    Get-ChildItem $outDir -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        "$hash  $($_.Name)"
    } | Set-Content -Path (Join-Path $outDir 'SHA256SUMS.txt') -Encoding utf8
    Pass "产物清单：$outDir"
}

try {
    Push-Location $root

    # ==================== -TagOnly：定稿提交之后的第二步 ====================
    # 只做"打标签"这一件事：不升版本、不改文件、不跑编译（第 1 步已跑过）。
    # 但把该断言的都断言掉——发布是不可逆动作，宁可停下。
    if ($TagOnly) {
        Write-Step '仅打标签（CHANGELOG 定稿提交之后）'
        $dirty = @(git status --porcelain)
        if ($dirty.Count -gt 0) { Fail "工作区有未提交改动（$($dirty.Count) 项）——定稿先提交，再打标签" }

        $repoVersion = ([string](Get-Content $versionFile -Raw -Encoding utf8 | ConvertFrom-Json).version).Trim()
        if ($Version -and $Version -ne $repoVersion) {
            Fail "version.json 是 $repoVersion，与 -Version $Version 不一致——不要给别的版本号打标签"
        }

        # README 的版本引用也必须与真源一致：发布时由 Step-ReadmeVersion 改写，
        # 这里再断言一次，防止人工定稿那一提交里漏掉它（漏了的话 README 会一直停在上一版）
        if ($script:exitCode -eq 0) {
            $readmeMatch = [regex]::Match((Get-Content -Raw -Encoding utf8 $readmeFile), $readmeVersionPattern)
            if (-not $readmeMatch.Success) {
                Fail "README.md 里找不到版本引用（期望「项目状态：早期开发阶段（v$repoVersion）」）"
            } elseif ($readmeMatch.Groups['version'].Value -ne $repoVersion) {
                Fail "README.md 写的是 v$($readmeMatch.Groups['version'].Value)，与 version.json 的 $repoVersion 不一致——别手改，跑 release.ps1 同步"
            }
        }
        # 注意变量名不叫 $tag：PowerShell 变量名大小写不敏感，$tag 会撞上本脚本的 -Tag 开关参数
        # （[switch]$Tag），往里赋字符串会直接抛"无法把 String 转成 SwitchParameter"。
        $tagName = "v$repoVersion"

        # 该版本节必须已**定稿**：草稿还留着脚本写的那两行提示，说明人还没做最后一步
        $lines = @(Get-Content $changelogFile -Encoding utf8)
        $start = -1
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match "^##\s*\[$([regex]::Escape($repoVersion))\]") { $start = $i; break }
        }
        if ($start -lt 0) { Fail "CHANGELOG.md 里找不到 [$repoVersion] 节" }
        else {
            $end = $lines.Count - 1
            for ($i = $start + 1; $i -lt $lines.Count; $i++) {
                if ($lines[$i] -match '^##\s*\[' -or $lines[$i] -match '^\[') { $end = $i - 1; break }
            }
            $section = ($lines[$start..$end] -join "`n")
            # 判据锚在草稿行本身（行首 + 前缀）：裸词匹配会误伤正文——定稿时写明
            # "按人工定稿要求改写"之类的句子是很自然的，那种节不该被判成草稿。
            if ($section -match '(?m)^> 草稿：由提交信息' -or $section -match '(?m)^> \*\*人工定稿要求\*\*') {
                Fail "CHANGELOG 的 [$repoVersion] 节还是脚本草稿（仍含「> 草稿：由提交信息」或「> **人工定稿要求**」行）——先定稿并提交，再打标签"
            }
        }

        if ($script:exitCode -eq 0) {
            $occupied = ((& git tag --list $tagName) -join '').Trim()
            if ($occupied) { Fail "本地标签 $tagName 已存在——版本号不复用、标签不重打（回滚口径见 SKILL.md 第七节）" }
        }

        # 打标签就是发布动作，ADR-026 §2.3.1 要求此刻库与代码仍然一致
        if ($script:exitCode -eq 0) {
            # 只在还没失败时读迁移清单：已经拦下的时候再抛一条"路径不存在"只会盖掉真正的原因
            $repoMigrations = @(Get-RepoMigrations)
            if ($SkipMigrationGate) { Skip '按参数跳过迁移门禁' }
            else { [void](Step-MigrationGate $repoMigrations) }
        }

        if ($script:exitCode -eq 0 -and -not $DryRun) {
            # 产物清单记的是"构建时的 HEAD"，而定稿提交在它之后：这里重算一次，
            # 让 MANIFEST 的 commit 等于**将要打标签的提交**。
            Step-Artifacts $repoVersion $repoMigrations
            git tag -a $tagName -m "EOS $tagName" | Out-Host
            Pass "已打附注标签 $tagName → $((& git rev-parse --short HEAD).Trim())（尚未推送：git push origin main && git push origin $tagName）"
        }
        elseif ($script:exitCode -eq 0) {
            Skip "DryRun：未打标签（$tagName）"
        }
        return
    }

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
        if ([string]::IsNullOrWhiteSpace($BuildOutput)) {
            dotnet build EOS.API/EOS.API.csproj --nologo -v:q | Out-Host
        }
        else {
            Write-Host "  产物输出目录（仓库外）：$BuildOutput" -ForegroundColor Gray
            dotnet build EOS.API/EOS.API.csproj --nologo -v:q -o $BuildOutput | Out-Host
        }
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

    Write-Step '3. 开发手册新鲜度'
    $freshness = (pwsh docs/guide/_tools/check-freshness.ps1 -Strict 2>&1 | Out-String).Trim()
    $behind = @($freshness -split "`r?`n" | Where-Object { $_ -match '落后\s*(\d+)' } |
        ForEach-Object { [int]([regex]::Match($_, '落后\s*(\d+)').Groups[1].Value) } | Select-Object -First 1)
    if ($null -eq $behind) { $behind = 0 }
    if ($behind -gt 0) {
        if ($StrictGuide) { Fail "有 $behind 篇手册落后于其映射源码——同步后再发布（或去掉 -StrictGuide）" }
        else {
            Skip "有 $behind 篇手册落后于其映射源码——按变更范围同步后**并入发布提交**（见 .agents/skills/eos-release）"
        }
    }
    else { Pass '手册与其映射源码同步（落后 0 篇）' }

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
    # 标记文字必须与 CHANGELOG.md 里那一行**逐字一致**：Replace 找不到就静默不插入（新节会丢），
    # 改这里就必须同步改文件。措辞与代码行为一致：新节插在**本行之下**，故最新在上、旧的在下。
    $marker = '<!-- 新增版本时在本行之下追加一节（最新在上、旧的在下），勿修改历史节。 -->'
    $updated = $existing.Replace($marker, "$marker`n`n$section")
    Set-Content -Path $changelogFile -Value $updated -Encoding utf8
    Pass "version.json → $next；CHANGELOG 已追加 $next 节（**需人工定稿**）"

    Step-ReadmeVersion $next

    # README 没同步上就不出产物：产物是为这一版发出去的，各处版本号不一致时出它没有意义
    if ($script:exitCode -eq 0) { Step-Artifacts $next $repoMigrations }

    if ($Tag) {
        # 不能在这里打标签：此刻 CHANGELOG 只是**草稿**（发给用户的说明还没改写、内部条目还在），
        # 自动提交再打标签会让标签指向「草稿」那一笔，而 ADR-026 与发布技能都要求标签落在
        # 「CHANGELOG 定稿」那一笔上。故本分支的 -Tag 只提示、不动作。
        # （首次发布分支不同：那时节由人先写好并提交，不存在草稿，所以那边 -Tag 仍然照打。）
        Fail "上面已写入草稿与产物，但**没有**打标签：本分支的 -Tag 会打在草稿提交上。请两步走——① 定稿 CHANGELOG 后 git commit；② pwsh scripts/release.ps1 -TagOnly -Version $next"
    }
    else {
        Skip "未打标签。定稿并提交后跑：pwsh scripts/release.ps1 -TagOnly -Version $next"
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
