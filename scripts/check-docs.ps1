#Requires -Version 7.0
<#
.SYNOPSIS
文档陈旧检查：确认 docs/status.md 未长期未刷新、README 未复述里程碑、
活跃计划未长期未刷新、活跃文档未残留指向已归档文件的死链。
.DESCRIPTION
维护纪律（docs/status.md §8）：开工前运行本脚本。任一"错误"未修复即退出码 1；
警告只提示不阻塞。
.EXAMPLE
.\scripts\check-docs.ps1
.\scripts\check-docs.ps1 -PlanStaleDays 14 -StatusMaxLagCommits 50
#>
param(
    [int]$PlanStaleDays = 30,
    [int]$StatusMaxLagCommits = 30
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$errors = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()
# git 无输出时命令结果是 $null，而 `([string]$null).Trim()` 仍会抛"不能对值为 Null 的表达式调用方法"
# ——[string] 在"内联转换再调方法"的写法下挡不住 null，门禁会自己崩掉。统一走 Out-String：
# 它永远产出字符串（无输入时为空串），是这里唯一可靠的取值方式。
$head = (git -C $root rev-parse --short HEAD | Out-String).Trim()

# ---- 1. status.md 的刷新滞后（警告） ----
# 这一条原为"status.md 的最后一次提交必须等于 HEAD"，即要求**每个提交都碰一下本文**。
# 现状纪律已改为「按结论刷新：没有结论变化就不动本文」（docs/status.md §8），两者冲突——
# status.md 纳入版本控制后，任何不相干的提交都会把它判红，门禁长期红灯等于没有门禁。
# 故改为按"落后多少个提交"计量：连续超过阈值未刷新才提示，由人确认是真没有变化还是该刷新。
$statusPath = Join-Path $root 'docs\status.md'
if (-not (Test-Path $statusPath)) {
    $errors.Add("docs/status.md 不存在——现状事实源缺失，必须先补齐")
} else {
    # 先问"这个文件有没有纳入版本控制"：工作副本用 .git/info/exclude 把 docs/ 整目录排除了，
    # 此时 `git log -- docs/status.md` 取到空值，直接 .Trim() 会崩在门禁脚本里——
    # 崩掉的门禁等于没有门禁：后面的规则一条都跑不到，而人只会看到一个空指针报错。
    $statusTracked = (git -C $root ls-files -- docs/status.md | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($statusTracked)) {
        $warnings.Add("docs/ 在本工作副本未纳入版本控制（被 .git/info/exclude 排除），" +
                      "'status.md 的刷新滞后'这条规则无法判定，已跳过；在纳入版本控制的副本里它会照常校验。")
    } else {
        $statusLast = (git -C $root log -1 --format=%h -- docs/status.md | Out-String).Trim()
        if ([string]::IsNullOrWhiteSpace($statusLast)) {
            $warnings.Add("docs/status.md 已在版本控制内但没有任何提交记录——请先把它提交入库")
        } else {
            $lagText = (git -C $root rev-list --count "$statusLast..HEAD" | Out-String).Trim()
            $lag = 0
            if (-not [int]::TryParse($lagText, [ref]$lag)) { $lag = 0 }
            if ($lag -gt $StatusMaxLagCommits) {
                $warnings.Add("docs/status.md 已连续 $lag 个提交未刷新（上次更新提交 $statusLast，阈值 $StatusMaxLagCommits）" +
                              "——请确认是真没有结论变化，还是该按 §8 刷新对应节")
            }
        }
    }
}

# ---- 2. README 不复述里程碑（警告） ----
$readmePath = Join-Path $root 'README.md'
$readme = $null
if (Test-Path $readmePath) {
    $readme = Get-Content -Raw -Encoding UTF8 $readmePath
    if ($readme -match '(?m)^#+ .*M(8[0-9]|9[0-4])(\s|：|:)') {
        $warnings.Add('README.md 出现里程碑标题（M80+）——历史应留在 git/archive，请删掉并指向 docs/status.md')
    }
}

# ---- 2b. README 的版本引用与 version.json 一致（错误） ----
# README 的项目状态行要给读者一个版本号，而版本真源是仓库根 version.json。
# 两者靠"发布时脚本改写 + 这里校验"绑定：手工只改一边会立刻被这里拦下
# （写死的版本号必然漂移——它曾停在 v0.1 跨越了整个 0.2.0 的开发周期）。
$versionPath = Join-Path $root 'version.json'
if ($null -ne $readme -and (Test-Path $versionPath)) {
    $repoVersion = ([string](Get-Content -Raw -Encoding UTF8 $versionPath | ConvertFrom-Json).version).Trim()
    # 格式写死成"（vX.Y.Z）"：机器要能解析，所以别改成别的写法
    $match = [regex]::Match($readme, '项目状态：早期开发阶段（v(?<version>\d+\.\d+\.\d+)）')
    if (-not $match.Success) {
        $errors.Add("README.md 找不到版本引用（期望「项目状态：早期开发阶段（v$repoVersion）」）——版本要能被机器校验，格式别改")
    } elseif ($match.Groups['version'].Value -ne $repoVersion) {
        $errors.Add("README.md 写的是 v$($match.Groups['version'].Value)，而 version.json 是 $repoVersion" +
                    "——发布脚本会同步，手工只改一边即漂移")
    }
}

# ---- 2c. LESSONS.md 的结构与编号（错误） ----
# 这份清单是给干活的 Agent 用的：条目长歪（编号断了、四段缺一）会让引用指错，也让"该怎么做"读不出来。
# 只查结构与编号，不判内容对错——"一条只记一次事故、只记可复用的"靠人守（见 AGENTS.md 第五节）。
$lessonsPath = Join-Path $root 'LESSONS.md'
if (-not (Test-Path $lessonsPath)) {
    $errors.Add('LESSONS.md 不存在——可复用的坑与经验缺了单一入口（AGENTS.md 第五节指向它）')
} else {
    $blocks = [System.Collections.Generic.List[object]]::new()
    $cur = $null
    foreach ($line in (Get-Content -Encoding UTF8 $lessonsPath)) {
        if ($line -like '### *') {
            if ($null -ne $cur) { $blocks.Add($cur) }
            $cur = [pscustomobject]@{ Head = $line; Body = [System.Collections.Generic.List[string]]::new() }
            continue
        }
        if ($null -ne $cur) { $cur.Body.Add($line) }
    }
    if ($null -ne $cur) { $blocks.Add($cur) }

    $lessonIds = [System.Collections.Generic.List[int]]::new()
    $badHead = 0
    foreach ($b in $blocks) {
        $m = [regex]::Match($b.Head, '^### L(?<id>\d+) ')
        if (-not $m.Success) {
            if ($badHead -lt 3) {
                $errors.Add("LESSONS.md 的条目标题须写成 '### L<n> <标题>'，实际是：$($b.Head)")
            }
            $badHead++
            continue
        }
        $id = [int]$m.Groups['id'].Value
        $lessonIds.Add($id)
        $body = $b.Body -join "`n"
        foreach ($need in '触发／症状', '根因', '处置', '防线') {
            if ($body -notmatch "(?m)^-\s*\*\*$need\*\*") {
                $errors.Add("LESSONS.md 的 L$id 缺「$need」段——四段（触发／症状 / 根因 / 处置 / 防线）缺一不可")
            }
        }
    }
    if ($lessonIds.Count -eq 0) {
        $errors.Add('LESSONS.md 里没有任何 "### L<n>" 条目——没有条目等于没有清单')
    } else {
        $dupIds = @($lessonIds | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { "L$($_.Name)" })
        if ($dupIds.Count -gt 0) {
            $errors.Add("LESSONS.md 编号重复：$($dupIds -join '、')——编号是引用锚点，只增不改、不回收")
        }
        $maxId = ($lessonIds | Measure-Object -Maximum).Maximum
        $missingIds = @((1..$maxId) | Where-Object { $_ -notin $lessonIds })
        if ($missingIds.Count -gt 0) {
            $errors.Add("LESSONS.md 编号不连续，缺：$($missingIds -join '、')")
        }
    }
}

# ---- 3. 活跃计划新鲜度（警告） ----
$activePlans = @(Get-ChildItem (Join-Path $root 'docs\plans') -Filter '*.md' -File -ErrorAction SilentlyContinue |
    Where-Object { $_.DirectoryName -eq (Join-Path $root 'docs\plans') })
$cutoff = (Get-Date).AddDays(-$PlanStaleDays)
foreach ($p in $activePlans) {
    $headLines = Get-Content -LiteralPath $p.FullName -TotalCount 40 -Encoding UTF8
    $dateMatch = [regex]::Match(($headLines -join "`n"), '20\d{2}-\d{2}-\d{2}')
    if (-not $dateMatch.Success) {
        $warnings.Add("活跃计划 $($p.Name) 无日期（状态/记录日期/更新）——请补统一状态头")
        continue
    }
    $d = [datetime]::ParseExact($dateMatch.Value, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
    if ($d -lt $cutoff) {
        $warnings.Add("活跃计划 $($p.Name) 状态日期 $($dateMatch.Value) 已超过 $PlanStaleDays 天未刷新——完成则移入 docs/plans/archive/")
    }
}

# ---- 4. 活跃文档死链（警告） ----
$archivedRefs = @('docs/技术债.md', 'docs/替代进度报告.md', 'docs/plans/工作交接盘点.md')
$scanFiles = @(
    (Join-Path $root 'README.md'),
    (Join-Path $root 'AGENTS.md'),
    (Join-Path $root 'docs\status.md')
) + @($activePlans | ForEach-Object { $_.FullName })
foreach ($f in $scanFiles) {
    if (-not (Test-Path $f)) { continue }
    $text = Get-Content -Raw -Encoding UTF8 $f
    foreach ($ref in $archivedRefs) {
        if ($text -match [regex]::Escape($ref)) {
            $warnings.Add("$($f.Replace($root + '\','')) 引用已归档文件 $ref ——请改为 docs/status.md 或 archive/ 路径")
        }
    }
}

# ---- 汇总 ----
Write-Host "== docs 检查（HEAD=$head）==" -ForegroundColor Cyan
foreach ($w in $warnings) { Write-Host "  [WARN] $w" -ForegroundColor Yellow }
foreach ($e in $errors) { Write-Host "  [ERROR] $e" -ForegroundColor Red }
if ($errors.Count -eq 0 -and $warnings.Count -eq 0) {
    Write-Host "docs 新鲜，无警告无错误。" -ForegroundColor Green
} elseif ($errors.Count -eq 0) {
    Write-Host "docs 基本新鲜（$($warnings.Count) 条警告）。" -ForegroundColor Yellow
} else {
    Write-Host "docs 陈旧：$($errors.Count) 个错误，$($warnings.Count) 条警告。" -ForegroundColor Red
    exit 1
}
exit 0
