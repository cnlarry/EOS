#Requires -Version 7.0
<#
.SYNOPSIS
文档陈旧检查：确认 docs/status.md 与 git HEAD 对齐、README 未复述里程碑、
活跃计划未长期未刷新、活跃文档未残留指向已归档文件的死链。
.DESCRIPTION
维护纪律（docs/status.md §8）：开工前运行本脚本。任一"错误"未修复即退出码 1；
警告只提示不阻塞。
.EXAMPLE
.\scripts\check-docs.ps1
.\scripts\check-docs.ps1 -PlanStaleDays 14
#>
param(
    [int]$PlanStaleDays = 30
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$errors = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()
# git 无输出时命令结果是 null，直接 .Trim() 会把门禁脚本自己打崩；先转字符串再裁剪。
$head = ([string](git -C $root rev-parse --short HEAD)).Trim()

# ---- 1. status.md 与 HEAD 对齐（错误） ----
$statusPath = Join-Path $root 'docs\status.md'
if (-not (Test-Path $statusPath)) {
    $errors.Add("docs/status.md 不存在——现状事实源缺失，必须先补齐")
} else {
    # 先问"这个文件有没有纳入版本控制"：本工作副本用 .git/info/exclude 把 docs/ 整目录排除了，
    # 此时 `git log -- docs/status.md` 取到空值，直接 .Trim() 会崩在门禁脚本里——
    # 崩掉的门禁等于没有门禁：后面的规则一条都跑不到，而人只会看到一个空指针报错。
    $statusTracked = ([string](git -C $root ls-files -- docs/status.md)).Trim()
    if ([string]::IsNullOrWhiteSpace($statusTracked)) {
        $warnings.Add("docs/ 在本工作副本未纳入版本控制（被 .git/info/exclude 排除），" +
                      "'status.md 随最新提交更新'这条规则无法判定，已跳过；在纳入版本控制的副本里它会照常校验。")
    } else {
        $statusLast = ([string](git -C $root log -1 --format=%h -- docs/status.md)).Trim()
        if ($statusLast -ne $head) {
            $errors.Add("docs/status.md 未随最新提交更新（上次更新提交 $statusLast，HEAD $head）——任务收尾必须刷新现状文档")
        }
    }
}

# ---- 2. README 不复述里程碑（警告） ----
$readmePath = Join-Path $root 'README.md'
if (Test-Path $readmePath) {
    $readme = Get-Content -Raw -Encoding UTF8 $readmePath
    if ($readme -match '(?m)^#+ .*M(8[0-9]|9[0-4])(\s|：|:)') {
        $warnings.Add('README.md 出现里程碑标题（M80+）——历史应留在 git/archive，请删掉并指向 docs/status.md')
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
