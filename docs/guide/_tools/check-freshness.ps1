#Requires -Version 7.0
<#
.SYNOPSIS
开发手册新鲜度检测：源码已更新而对应文档未跟进时报告。

.DESCRIPTION
以 docs/guide/_map.md 的「文档 ↔ 源码」映射为输入，逐篇比较文档与其对应源码的最后提交时间。
源码在文档最后更新之后又有提交，即视为文档可能落后，需人工判断本次变更是否影响文档描述的行为。
脚本只负责提醒「源码动了、文档没动」，不判断变更类型，也不改写任何文件。

四类结果：
  待写    映射中登记但文档尚未创建
  未提交  文档已创建但尚无提交记录（新写的篇目）
  落后    对应源码的提交晚于文档的提交
  新鲜    文档提交不早于其全部源码

.Strict 下「落后」与「未登记」均导致退出码 1，可用于提交前检查。

报「落后」时会同时提示知识库重灌：本手册的「规范与坑」「设计动因」两类内容已进知识库，
源码动了而文档没动 ⇒ 知识库里的内容可能落后于仓库，助手会拿着旧文档讲错规范。
默认只提示（并给出命令）；-SyncKb 直接触发同步脚本（幂等，内容未变的文档会被跳过）。

.EXAMPLE
pwsh docs/guide/_tools/check-freshness.ps1
pwsh docs/guide/_tools/check-freshness.ps1 -Strict
pwsh docs/guide/_tools/check-freshness.ps1 -Strict -SyncKb
#>
param(
    [switch]$Strict,
    [switch]$SyncKb
)

$ErrorActionPreference = 'Stop'

$guideDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$root = (Resolve-Path (Join-Path $guideDir '..' '..')).Path
$mapPath = Join-Path $guideDir '_map.md'
$mapName = '_map.md'

if (-not (Test-Path $mapPath)) {
    Write-Host "缺少映射表 $mapPath ——检测无法进行。" -ForegroundColor Red
    exit 1
}

function Get-LastCommitTime {
    param([string]$RelativePath)
    # git 对无提交记录的路径输出为空，此时命令结果为 $null；
    # 在 $ErrorActionPreference = 'Stop' 下直接对它调方法会抛 InvalidOperation 中断脚本，先判空。
    $out = git -C $root log -1 --format=%ct -- $RelativePath 2>$null
    if ($null -eq $out) { return $null }
    $text = ([string]$out).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    $value = [int64]0
    if (-not [int64]::TryParse($text, [ref]$value)) { return $null }
    return $value
}

function Format-CommitTime {
    param([int64]$UnixTime)
    return [DateTimeOffset]::FromUnixTimeSeconds($UnixTime).LocalDateTime.ToString('yyyy-MM-dd')
}

# ---- 解析映射表 ----
$rows = [System.Collections.Generic.List[object]]::new()
foreach ($line in (Get-Content -LiteralPath $mapPath -Encoding UTF8)) {
    if ($line -notmatch '^\s*\|') { continue }
    $cells = @($line.Trim().Trim('|') -split '\|' | ForEach-Object { $_.Trim() })
    if ($cells.Count -lt 3) { continue }
    if ($cells[0] -eq '文档' -or $cells[0] -match '^-+$') { continue }
    $sources = @()
    if ($cells[2] -ne '-' -and $cells[2] -ne '') {
        $sources = @($cells[2] -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    }
    $rows.Add([pscustomobject]@{ Doc = $cells[0]; Status = $cells[1]; Sources = $sources })
}

if ($rows.Count -eq 0) {
    Write-Host "映射表 $mapName 未解析到任何篇目——检查表格格式。" -ForegroundColor Red
    exit 1
}

# ---- 逐篇比较 ----
$pending = [System.Collections.Generic.List[object]]::new()
$uncommitted = [System.Collections.Generic.List[object]]::new()
$stale = [System.Collections.Generic.List[object]]::new()
$fresh = [System.Collections.Generic.List[object]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()

foreach ($row in $rows) {
    $docRel = "docs/guide/$($row.Doc)"
    if (-not (Test-Path (Join-Path $guideDir $row.Doc))) {
        $pending.Add($row)
        continue
    }

    $docTime = Get-LastCommitTime $docRel
    if ($null -eq $docTime) {
        $uncommitted.Add($row)
        continue
    }

    $newestSourceTime = $null
    $newestSourcePath = ''
    foreach ($source in $row.Sources) {
        if (-not (Test-Path (Join-Path $root $source))) {
            $warnings.Add("$($row.Doc)：映射路径不存在，已跳过 —— $source")
            continue
        }
        $sourceTime = Get-LastCommitTime $source
        if ($null -eq $sourceTime) { continue }
        if ($null -eq $newestSourceTime -or $sourceTime -gt $newestSourceTime) {
            $newestSourceTime = $sourceTime
            $newestSourcePath = $source
        }
    }

    if ($null -ne $newestSourceTime -and $newestSourceTime -gt $docTime) {
        $stale.Add([pscustomobject]@{
            Doc            = $row.Doc
            DocTime        = $docTime
            SourceTime     = $newestSourceTime
            SourcePath     = $newestSourcePath
        })
    } else {
        $fresh.Add($row)
    }
}

# ---- 未登记文档 ----
# 索引与映射表自身不是篇目，不参与登记检查。
$notTopics = @($mapName, 'README.md')
$registered = @($rows | ForEach-Object { $_.Doc })
$unregistered = @(
    Get-ChildItem -LiteralPath $guideDir -Filter '*.md' -File |
        Where-Object { $notTopics -notcontains $_.Name -and $registered -notcontains $_.Name } |
        ForEach-Object { $_.Name }
)

# ---- 报告 ----
$head = ([string](git -C $root rev-parse --short HEAD)).Trim()
Write-Host "== 开发手册新鲜度（HEAD=$head）==" -ForegroundColor Cyan
Write-Host ("  待写 {0} 篇 ｜ 未提交 {1} 篇 ｜ 落后 {2} 篇 ｜ 新鲜 {3} 篇" -f `
    $pending.Count, $uncommitted.Count, $stale.Count, $fresh.Count)

foreach ($item in $stale) {
    Write-Host ""
    Write-Host "  [落后] $($item.Doc)" -ForegroundColor Yellow
    Write-Host ("         文档最后更新 {0}；源码 {1} 于 {2} 有提交" -f `
        (Format-CommitTime $item.DocTime), $item.SourcePath, (Format-CommitTime $item.SourceTime)) -ForegroundColor DarkGray
    Write-Host "         确认本次变更是否影响该篇描述的行为，涉及则更新文档" -ForegroundColor DarkGray
}

foreach ($name in $unregistered) {
    $warnings.Add("$name 未在 $mapName 登记，陈旧检测覆盖不到它")
}

if ($pending.Count -gt 0) {
    Write-Host ""
    Write-Host "  待写：$((@($pending | ForEach-Object { $_.Doc }) -join '、'))" -ForegroundColor DarkGray
}
if ($uncommitted.Count -gt 0) {
    Write-Host "  未提交：$((@($uncommitted | ForEach-Object { $_.Doc }) -join '、'))" -ForegroundColor DarkGray
}

foreach ($warning in $warnings) {
    Write-Host "  [提醒] $warning" -ForegroundColor Yellow
}

# ---- 知识库重灌：文档落后 ⇒ 知识库内容可能落后（不得静默） ----
if ($stale.Count -gt 0) {
    $syncScript = Join-Path $root 'scripts/sync-guide-to-kb.ps1'
    Write-Host ""
    Write-Host "  [知识库重灌] 本手册的「规范与坑」「设计动因」已进知识库；源码领先于文档意味着" -ForegroundColor Yellow
    Write-Host "               知识库里的内容可能落后于仓库——先更新文档，再重灌：" -ForegroundColor Yellow
    if (Test-Path $syncScript) {
        Write-Host "               pwsh scripts/sync-guide-to-kb.ps1        # 幂等：内容未变的文档会跳过" -ForegroundColor DarkGray
    } else {
        Write-Host "               未找到 scripts/sync-guide-to-kb.ps1（同步链路缺失）" -ForegroundColor Red
    }
    if ($SyncKb) {
        if (-not (Test-Path $syncScript)) {
            Write-Host "               无法触发：同步脚本不存在。" -ForegroundColor Red
        } else {
            Write-Host "               -SyncKb：触发知识库重灌…" -ForegroundColor Cyan
            & pwsh -NoProfile -File $syncScript
            Write-Host "               重灌退出码 $LASTEXITCODE（非 0 见上行逐文档结果）" -ForegroundColor DarkGray
        }
    }
}

Write-Host ""
if ($stale.Count -eq 0 -and $unregistered.Count -eq 0) {
    Write-Host "文档与源码同步。" -ForegroundColor Green
    exit 0
}

if ($Strict) {
    Write-Host "存在落后或未登记篇目（$($stale.Count) 落后 / $($unregistered.Count) 未登记）。" -ForegroundColor Red
    exit 1
}

Write-Host "存在落后或未登记篇目（$($stale.Count) 落后 / $($unregistered.Count) 未登记）。" -ForegroundColor Yellow
exit 0
