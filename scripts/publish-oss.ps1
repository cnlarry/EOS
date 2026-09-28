#Requires -Version 7.0
<#
.SYNOPSIS
开源仓发布流水线：把内部仓的一个提交/标签，按白名单裁剪、门禁扫描后同步为开源仓的一次发布快照。

.DESCRIPTION
内部仓与开源仓之间不共享 git 历史——开源仓每次收到的是一个「发布快照提交」，
因此内部提交记录、开发节奏与已从工作区移除但仍在历史中的文件都不会外泄。

流程：
  1. 用 git worktree 检出指定 ref 到临时目录（只含被跟踪文件，不受当前工作区状态影响）；
  2. 按 publish/publish.psd1 的 Include/Exclude 与迁移基线裁剪出发布清单；
  3. 叠加 publish/overlay（对外文档、开源版解决方案文件、建库脚本）；
  4. 门禁扫描（Forbidden + SensitiveSubjects），命中即中止；
  5. Apply 模式下同步到开源仓工作区（含删除多余文件）、执行构建校验、git add；
     提交与推送一律由人工完成。

.PARAMETER Ref
要发布的提交或标签，默认 HEAD。

.PARAMETER OssRepo
开源仓本地克隆路径。Report 模式可省略。

.PARAMETER Mode
Report（默认）：只产出报告，不写任何目标文件。
Apply：写入开源仓工作区并 git add，仍不 commit / 不 push。

.PARAMETER SkipBuild
跳过 Apply 模式下的构建校验（仅用于快速迭代，正式发布不要跳过）。

.EXAMPLE
.\scripts\publish-oss.ps1
.EXAMPLE
.\scripts\publish-oss.ps1 -Ref release/v0.1 -OssRepo D:\repo\EOS -Mode Apply
#>
[CmdletBinding()]
param(
    [string]$Ref = 'HEAD',
    [string]$OssRepo = '',
    [ValidateSet('Report', 'Apply')][string]$Mode = 'Report',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$configPath = Join-Path $root 'publish/publish.psd1'
$reportDir = Join-Path $root 'publish/report'

function Write-Step { param([string]$Message) Write-Host "== $Message" -ForegroundColor Cyan }
function Write-Pass { param([string]$Message) Write-Host "PASS $Message" -ForegroundColor Green }
function Write-Fail { param([string]$Message) Write-Host "FAIL $Message" -ForegroundColor Red }
function Write-Warn { param([string]$Message) Write-Host "WARN $Message" -ForegroundColor Yellow }

# glob → 正则：** 跨层级，* 限单层
function Convert-GlobToRegex {
    param([Parameter(Mandatory)][string]$Glob)
    $sb = [System.Text.StringBuilder]::new('^')
    $i = 0
    while ($i -lt $Glob.Length) {
        $ch = $Glob[$i]
        if ($ch -eq '*') {
            if ($i + 1 -lt $Glob.Length -and $Glob[$i + 1] -eq '*') {
                # `**/` 允许匹配零层目录，使 '**/AGENTS.md' 也命中根目录的同名文件
                if ($i + 2 -lt $Glob.Length -and $Glob[$i + 2] -eq '/') {
                    [void]$sb.Append('(?:.*/)?'); $i += 3; continue
                }
                [void]$sb.Append('.*'); $i += 2; continue
            }
            [void]$sb.Append('[^/]*'); $i++; continue
        }
        if ($ch -eq '?') { [void]$sb.Append('[^/]'); $i++; continue }
        [void]$sb.Append([Regex]::Escape([string]$ch)); $i++
    }
    [void]$sb.Append('$')
    return [Regex]::new($sb.ToString(), 'IgnoreCase')
}

function Test-AnyMatch {
    param([string]$Path, [Regex[]]$Patterns)
    foreach ($p in $Patterns) { if ($p.IsMatch($Path)) { return $true } }
    return $false
}

# ---------------------------------------------------------------- 1. 配置
Write-Step '加载发布配置'
if (-not (Test-Path -LiteralPath $configPath)) { throw "发布配置缺失：$configPath" }
$config = Import-PowerShellDataFile -LiteralPath $configPath

$includeRegex = @($config.Include | ForEach-Object { Convert-GlobToRegex $_ })
$excludeRegex = @($config.Exclude | ForEach-Object { Convert-GlobToRegex $_ })
$skipScanRegex = @($config.ForbiddenScanSkip | ForEach-Object { [Regex]::new($_, 'IgnoreCase') })
$baseline = [int]$config.MigrationBaseline
$overlayDir = Join-Path $root ($config.OverlayDir -replace '/', [IO.Path]::DirectorySeparatorChar)

$rules = foreach ($rule in $config.Forbidden) {
    [pscustomobject]@{
        Id      = [string]$rule.Id
        Note    = [string]$rule.Note
        Regex   = [Regex]::new([string]$rule.Pattern)
        Allow   = @($rule.Allow | Where-Object { $_ } | ForEach-Object { [Regex]::new($_) })
    }
}
foreach ($subject in @($config.SensitiveSubjects | Where-Object { $_ })) {
    $rules += [pscustomobject]@{
        Id    = 'sensitive-subject'
        Note  = "敏感主体：$subject"
        Regex = [Regex]::new([Regex]::Escape($subject), 'IgnoreCase')
        Allow = @()
    }
}
Write-Pass "规则 $($rules.Count) 条，Include $($includeRegex.Count) 项，Exclude $($excludeRegex.Count) 项，迁移基线 $baseline"

# ---------------------------------------------------------------- 2. 检出快照
Write-Step "检出发布快照：$Ref"
Push-Location $root
try {
    $resolved = (& git rev-parse --verify "$Ref^{commit}" 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $resolved) { throw "无法解析 ref：$Ref" }
    $resolved = $resolved.Trim()
    $shortRef = $resolved.Substring(0, 12)
} finally { Pop-Location }

$workDir = Join-Path ([IO.Path]::GetTempPath()) "eos-publish-$shortRef-$(Get-Random)"
$worktreeAdded = $false
try {
    Push-Location $root
    try {
        & git worktree add --detach --quiet $workDir $resolved
        if ($LASTEXITCODE -ne 0) { throw "git worktree add 失败：$workDir" }
        $worktreeAdded = $true
    } finally { Pop-Location }
    Write-Pass "快照目录 $workDir"

    # ------------------------------------------------------------ 3. 发布清单
    Write-Step '计算发布清单'
    Push-Location $root
    try {
        $tracked = @(& git ls-tree -r --name-only $resolved)
        if ($LASTEXITCODE -ne 0) { throw 'git ls-tree 失败' }
    } finally { Pop-Location }

    $migrationRegex = [Regex]::new('^EOS\.API/Data/Migrations/(\d+)_', 'IgnoreCase')
    $selected = [System.Collections.Generic.List[string]]::new()
    $droppedByExclude = [System.Collections.Generic.List[string]]::new()
    $droppedByBaseline = [System.Collections.Generic.List[string]]::new()

    foreach ($path in $tracked) {
        if (-not (Test-AnyMatch -Path $path -Patterns $includeRegex)) { continue }
        if (Test-AnyMatch -Path $path -Patterns $excludeRegex) { $droppedByExclude.Add($path); continue }
        $m = $migrationRegex.Match($path)
        if ($m.Success -and [int]$m.Groups[1].Value -le $baseline) { $droppedByBaseline.Add($path); continue }
        $selected.Add($path)
    }
    Write-Pass "入选 $($selected.Count) 个文件（排除 $($droppedByExclude.Count)，迁移基线内 $($droppedByBaseline.Count)，仓库总计 $($tracked.Count)）"
    if ($selected.Count -eq 0) { throw '发布清单为空，请检查 Include 配置。' }

    # ------------------------------------------------------------ 4. 覆盖层
    Write-Step '收集覆盖层'
    $overlayFiles = @{}
    if (Test-Path -LiteralPath $overlayDir) {
        foreach ($file in Get-ChildItem -LiteralPath $overlayDir -Recurse -File) {
            $rel = $file.FullName.Substring($overlayDir.Length).TrimStart('\', '/').Replace('\', '/')
            $overlayFiles[$rel] = $file.FullName
        }
    }
    Write-Pass "覆盖层 $($overlayFiles.Count) 个文件"
    if ($overlayFiles.Count -eq 0) { Write-Warn "覆盖层为空：开源仓将缺少 README / LICENSE（目录 $overlayDir）" }

    # ------------------------------------------------------------ 5. 门禁扫描
    Write-Step '门禁扫描'
    $findings = [System.Collections.Generic.List[object]]::new()

    $scanTargets = [System.Collections.Generic.List[object]]::new()
    foreach ($rel in $selected) {
        # 快照中将被覆盖层替换的同名文件不参与门禁扫描：实际发布的是覆盖层版本
        if ($overlayFiles.ContainsKey($rel)) { continue }
        $scanTargets.Add([pscustomobject]@{ Rel = $rel; Full = (Join-Path $workDir ($rel -replace '/', [IO.Path]::DirectorySeparatorChar)); Origin = 'snapshot' })
    }
    foreach ($rel in $overlayFiles.Keys) {
        $scanTargets.Add([pscustomobject]@{ Rel = $rel; Full = $overlayFiles[$rel]; Origin = 'overlay' })
    }

    $scanned = 0
    foreach ($target in $scanTargets) {
        if (Test-AnyMatch -Path $target.Rel -Patterns $skipScanRegex) { continue }
        if (-not (Test-Path -LiteralPath $target.Full)) { continue }
        # 二进制探测：读取首段判断是否含 NUL
        $head = [byte[]]::new(0)
        try { $head = [IO.File]::ReadAllBytes($target.Full) } catch { continue }
        if ($head.Length -eq 0) { continue }
        $probe = [Math]::Min(4096, $head.Length)
        $isBinary = $false
        for ($i = 0; $i -lt $probe; $i++) { if ($head[$i] -eq 0) { $isBinary = $true; break } }
        if ($isBinary) { continue }

        $scanned++
        $text = [Text.Encoding]::UTF8.GetString($head)
        $lines = $text -split "\r?\n"
        for ($n = 0; $n -lt $lines.Length; $n++) {
            $line = $lines[$n]
            if ($line.Length -eq 0) { continue }
            foreach ($rule in $rules) {
                $match = $rule.Regex.Match($line)
                if (-not $match.Success) { continue }
                $allowed = $false
                foreach ($allow in $rule.Allow) { if ($allow.IsMatch($line)) { $allowed = $true; break } }
                if ($allowed) { continue }
                $snippet = $line.Trim()
                if ($snippet.Length -gt 160) { $snippet = $snippet.Substring(0, 160) + '…' }
                $findings.Add([pscustomobject]@{
                        Rule    = $rule.Id
                        Note    = $rule.Note
                        Origin  = $target.Origin
                        File    = $target.Rel
                        Line    = $n + 1
                        Match   = $match.Value
                        Snippet = $snippet
                    })
            }
        }
    }
    Write-Pass "扫描 $scanned 个文本文件，命中 $($findings.Count) 处"

    # ------------------------------------------------------------ 6. 报告
    Write-Step '写出报告'
    New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
    $reportPath = Join-Path $reportDir 'latest.md'
    $byRule = $findings | Group-Object Rule | Sort-Object Count -Descending

    $md = [System.Collections.Generic.List[string]]::new()
    $md.Add('# 开源发布预检报告')
    $md.Add('')
    $md.Add("- 生成时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $md.Add("- 发布 ref：``$Ref`` → ``$resolved``")
    $md.Add("- 模式：$Mode")
    $md.Add("- 入选文件：$($selected.Count) / 仓库跟踪 $($tracked.Count)")
    $md.Add("- 覆盖层文件：$($overlayFiles.Count)")
    $md.Add("- 门禁命中：$($findings.Count) 处，涉及 $(($findings | Select-Object -ExpandProperty File -Unique).Count) 个文件")
    $md.Add('')
    $md.Add('## 命中汇总（按规则）')
    $md.Add('')
    if ($byRule.Count -eq 0) {
        $md.Add('无命中。')
    } else {
        $md.Add('| 规则 | 命中数 | 涉及文件 | 说明 |')
        $md.Add('| --- | --- | --- | --- |')
        foreach ($group in $byRule) {
            $files = ($group.Group | Select-Object -ExpandProperty File -Unique).Count
            $md.Add("| $($group.Name) | $($group.Count) | $files | $($group.Group[0].Note) |")
        }
    }
    $md.Add('')
    $md.Add('## 命中明细（按文件，每文件最多 20 条）')
    $md.Add('')
    foreach ($group in ($findings | Group-Object File | Sort-Object Count -Descending)) {
        $md.Add("### $($group.Name)（$($group.Count) 处）")
        $md.Add('')
        foreach ($item in ($group.Group | Select-Object -First 20)) {
            $md.Add("- L$($item.Line) ``$($item.Rule)`` → ``$($item.Match)``")
            $md.Add("  - ``$($item.Snippet -replace '`', "'")``")
        }
        if ($group.Count -gt 20) { $md.Add("- …其余 $($group.Count - 20) 处省略") }
        $md.Add('')
    }
    $md.Add('## 被排除的文件')
    $md.Add('')
    $md.Add("### 由 Exclude 规则排除（$($droppedByExclude.Count)）")
    $md.Add('')
    foreach ($path in ($droppedByExclude | Sort-Object)) { $md.Add("- $path") }
    $md.Add('')
    $md.Add("### 由迁移基线排除（$($droppedByBaseline.Count)，编号 <= $baseline）")
    $md.Add('')
    foreach ($path in ($droppedByBaseline | Sort-Object)) { $md.Add("- $path") }
    $md.Add('')
    $md.Add('### 未被任何 Include 命中的顶层目录')
    $md.Add('')
    $notIncluded = $tracked | Where-Object { -not (Test-AnyMatch -Path $_ -Patterns $includeRegex) }
    foreach ($group in ($notIncluded | ForEach-Object { ($_ -split '/')[0] } | Group-Object | Sort-Object Count -Descending)) {
        $md.Add("- $($group.Name)：$($group.Count) 个文件")
    }

    [IO.File]::WriteAllLines($reportPath, $md, [Text.UTF8Encoding]::new($false))
    Write-Pass "报告：$reportPath"

    if ($findings.Count -gt 0) {
        Write-Fail "门禁命中 $($findings.Count) 处，发布中止。请先按报告清理，再重跑。"
        foreach ($group in $byRule) { Write-Host "     - $($group.Name)：$($group.Count) 处" -ForegroundColor Red }
        exit 1
    }
    Write-Pass '门禁通过'

    if ($Mode -eq 'Report') {
        Write-Host ''
        Write-Host "Report 模式完成。确认报告无误后，用 -Mode Apply -OssRepo <开源仓路径> 同步。" -ForegroundColor Cyan
        exit 0
    }

    # ------------------------------------------------------------ 7. 同步
    Write-Step '同步到开源仓'
    if (-not $OssRepo) { throw 'Apply 模式必须提供 -OssRepo。' }
    $ossRoot = (Resolve-Path -LiteralPath $OssRepo).Path
    if (-not (Test-Path -LiteralPath (Join-Path $ossRoot '.git'))) { throw "目标不是 git 仓库：$ossRoot" }

    Push-Location $ossRoot
    try {
        $ossRemote = (& git remote get-url origin 2>$null)
        if ($ossRemote -and $ossRemote -match 'EOS\.dev') {
            throw "目标仓库的 origin 指向内部仓（$ossRemote），拒绝写入。"
        }
        $ossStatus = @(& git status --porcelain)
        if ($ossStatus.Count -gt 0) {
            throw "开源仓工作区不干净（$($ossStatus.Count) 项变更），请先处理后重试。"
        }
    } finally { Pop-Location }

    # 目标文件全集（快照裁剪 + 覆盖层，覆盖层同路径优先）
    $desired = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($rel in $selected) { $desired[$rel] = (Join-Path $workDir ($rel -replace '/', [IO.Path]::DirectorySeparatorChar)) }
    foreach ($rel in $overlayFiles.Keys) { $desired[$rel] = $overlayFiles[$rel] }

    # 删除开源仓中已不在目标全集里的被跟踪文件
    Push-Location $ossRoot
    try {
        $ossTracked = @(& git ls-files)
    } finally { Pop-Location }
    $removed = 0
    foreach ($rel in $ossTracked) {
        if ($desired.ContainsKey($rel)) { continue }
        $full = Join-Path $ossRoot ($rel -replace '/', [IO.Path]::DirectorySeparatorChar)
        if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Force; $removed++ }
    }

    $written = 0
    foreach ($rel in $desired.Keys) {
        $src = $desired[$rel]
        if (-not (Test-Path -LiteralPath $src)) { continue }
        $dest = Join-Path $ossRoot ($rel -replace '/', [IO.Path]::DirectorySeparatorChar)
        $destDir = Split-Path -Parent $dest
        if (-not (Test-Path -LiteralPath $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
        Copy-Item -LiteralPath $src -Destination $dest -Force
        $written++
    }
    # 清理空目录
    for ($pass = 0; $pass -lt 5; $pass++) {
        $empties = Get-ChildItem -LiteralPath $ossRoot -Directory -Recurse |
            Where-Object { $_.FullName -notmatch '\\\.git($|\\)' } |
            Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force | Where-Object { $_.Name -ne '.git' }) }
        if (-not $empties) { break }
        $empties | Remove-Item -Force -Recurse
    }
    Write-Pass "写入 $written 个文件，删除 $removed 个"

    # ------------------------------------------------------------ 8. 构建校验
    if ($SkipBuild) {
        Write-Warn '已跳过构建校验（-SkipBuild）'
    } else {
        Write-Step '开源仓构建校验'
        $apiDir = Join-Path $ossRoot 'EOS.API'
        if (Test-Path -LiteralPath $apiDir) {
            Push-Location $apiDir
            try {
                & dotnet build --nologo -v minimal
                if ($LASTEXITCODE -ne 0) { throw 'EOS.API dotnet build 失败' }
            } finally { Pop-Location }
            Write-Pass 'EOS.API dotnet build'
        }
        $webDir = Join-Path $ossRoot 'EOS.Web'
        if (Test-Path -LiteralPath $webDir) {
            Push-Location $webDir
            try {
                if (-not (Test-Path -LiteralPath (Join-Path $webDir 'node_modules'))) {
                    & npm install
                    if ($LASTEXITCODE -ne 0) { throw 'EOS.Web npm install 失败' }
                }
                & npm run lint
                if ($LASTEXITCODE -ne 0) { throw 'EOS.Web npm run lint 失败' }
                & npm run build
                if ($LASTEXITCODE -ne 0) { throw 'EOS.Web npm run build 失败' }
            } finally { Pop-Location }
            Write-Pass 'EOS.Web lint + build'
        }
    }

    # ------------------------------------------------------------ 9. 暂存
    Write-Step '暂存变更（不提交、不推送）'
    Push-Location $ossRoot
    try {
        & git add -A
        if ($LASTEXITCODE -ne 0) { throw 'git add 失败' }
        $staged = @(& git diff --cached --name-only)
    } finally { Pop-Location }

    Write-Host ''
    Write-Pass "已暂存 $($staged.Count) 个文件变更：$ossRoot"
    Write-Host ''
    Write-Host '人工复核后再提交（本脚本不会 commit / push）：' -ForegroundColor Cyan
    Write-Host "  cd $ossRoot" -ForegroundColor Gray
    Write-Host '  git diff --cached --stat' -ForegroundColor Gray
    Write-Host '  git status' -ForegroundColor Gray
    Write-Host "  git commit -m 'Release vX.Y'" -ForegroundColor Gray
    Write-Host "  git push origin $($config.TargetBranch)" -ForegroundColor Gray
} finally {
    if ($worktreeAdded) {
        Push-Location $root
        try {
            & git worktree remove --force $workDir 2>$null | Out-Null
        } catch {
            Write-Warn "临时工作树未能自动清理：$workDir"
        } finally { Pop-Location }
    }
}
