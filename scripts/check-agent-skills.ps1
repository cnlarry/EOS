#Requires -Version 7.0
<#
.SYNOPSIS
    Agent 技能门禁：frontmatter 名与目录名一致、引用资产存在、引用资产未陈旧。

.DESCRIPTION
    输入是 .agents/skills/_map.md（技能清单 + 引用资产例外 + 忽略的顶层片段）。校验四件事：

      1. 每个技能目录的 frontmatter `name` 与目录名一致；
      2. 技能正文引用的仓库内路径存在——默认「必须存在」；例外口径（本机 / 已退役 / 外部 / 相对）
         见 _map.md 第二节，**按最长前缀继承**（登记 `docs/plans/` 即覆盖其下所有路径）；
      3. **未识别的顶层片段**：抽取到的路径其顶层目录既不在仓库根、也未登记任何口径 ⇒ FAIL。
         这道关专防「目录被删掉了，技能还在引用」——正是本门禁要解决的问题；
      4. 引用资产未比技能更新（资产更新而技能没动 ⇒ STALE）——**仅提示，不阻断**。

    为什么第 4 项不阻断：它是 git 时间戳代理，资产一改就命中（改本脚本自己也会让技能 README 命中），
    拿来阻断只会逼出"为过门禁而空改文档"的动作。确定性的是前三项，它们也是真正抓到过漂移的那三项。

    为什么需要它：该目录曾出现引用「已被删除的发布脚本」与「已退役目录」的漂移，
    而没有门禁的「不双写」纪律守不住（见 .agents/skills/README.md）。

    只读文件系统与 git 历史，不连库、不发网络请求。

.EXAMPLE
    pwsh scripts/check-agent-skills.ps1
    pwsh scripts/check-agent-skills.ps1 -Strict    # 警告也计入退出码 1（陈旧始终仅提示）
#>
param(
    [switch]$Strict
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$skillsDir = Join-Path $root '.agents\skills'
$skillsDirGitPath = '.agents/skills'
$mapPath = Join-Path $skillsDir '_map.md'

$errors = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()
$stales = [System.Collections.Generic.List[string]]::new()

function Get-GitText {
    <#
      git 无输出时命令结果是 $null，而 `([string]$null).Trim()` 在「内联转换再调方法」的写法下
      仍会抛「不能对值为 Null 的表达式调用方法」——门禁会自己崩掉。统一用 Out-String：
      它永远产出字符串（无输入时为空串）。
    #>
    param([string[]] $Arguments)
    return ((& git -C $root @Arguments 2>$null | Out-String).Trim())
}

function Get-LastCommitSeconds {
    <# 路径最后一次提交的 Unix 秒；未跟踪或无历史返回 0。 #>
    param([string] $Relative)
    $value = Get-GitText @('log', '-1', '--format=%ct', '--', $Relative)
    $parsed = 0L
    if ([long]::TryParse($value, [ref] $parsed)) { return $parsed }
    return 0L
}

function Test-RepoPath {
    param([string] $Relative)
    return (Test-Path -LiteralPath (Join-Path $root $Relative))
}

function Get-MapSection {
    <# 取 _map.md 中以指定二级标题开头的一节。 #>
    param([string] $MapText, [string] $HeadingPrefix)
    return @($MapText -split '(?m)^##\s+') |
        Where-Object { $_ -match ('^' + [regex]::Escape($HeadingPrefix)) } |
        Select-Object -First 1
}

function Read-MapTable {
    <# 从一节里取两列表格：返回 @{ 第一列 = 第二列 }。 #>
    param([string] $Section)
    $result = @{}
    foreach ($line in ($Section -split "`r?`n")) {
        if ($line -notmatch '^\s*\|') { continue }
        $cells = @(($line.Trim().Trim('|') -split '\|') | ForEach-Object { $_.Trim() })
        if ($cells.Count -lt 2) { continue }
        $key = $cells[0].Trim('`').TrimEnd('/')
        $value = $cells[1]
        if ([string]::IsNullOrWhiteSpace($key) -or $key -match '^-+$' -or $key -eq '资产' -or $key -eq '片段') { continue }
        if ([string]::IsNullOrWhiteSpace($value) -or $value -match '^-+$' -or $value -eq '口径' -or $value -eq '说明') { continue }
        $result[$key] = $value
    }
    return $result
}

if (-not (Test-Path -LiteralPath $skillsDir)) {
    Write-Host 'SKIP: .agents/skills 不存在（技能未随本工作副本分发）' -ForegroundColor Yellow
    exit 0
}
if (-not (Test-Path -LiteralPath $mapPath)) {
    Write-Host "FAIL: 缺少映射表 $mapPath ——技能须先登记，否则无从判断引用是否还成立" -ForegroundColor Red
    exit 1
}

# ---- 0. 解析映射表 ----
$mapText = Get-Content -Raw -Encoding UTF8 $mapPath
$exceptionSection = Get-MapSection -MapText $mapText -HeadingPrefix '二、引用资产例外'
if (-not $exceptionSection) {
    Write-Host 'FAIL: _map.md 缺少「二、引用资产例外」一节' -ForegroundColor Red
    exit 1
}
$policy = Read-MapTable -Section $exceptionSection   # 资产（去尾斜杠）-> 口径

$ignoreSection = Get-MapSection -MapText $mapText -HeadingPrefix '三、忽略的顶层片段'
$ignoreTop = [System.Collections.Generic.HashSet[string]]::new()
if ($ignoreSection) {
    foreach ($k in (Read-MapTable -Section $ignoreSection).Keys) { $null = $ignoreTop.Add($k) }
}

function Get-PolicyKind {
    <# 最长前缀匹配：登记 docs/plans 即覆盖 docs/plans/archive 等其下所有路径。 #>
    param([string] $Ref)
    $bestKind = ''
    $bestLength = -1
    foreach ($key in $policy.Keys) {
        if ($Ref -eq $key -or $Ref.StartsWith("$key/")) {
            if ($key.Length -gt $bestLength) { $bestLength = $key.Length; $bestKind = $policy[$key] }
        }
    }
    return $bestKind
}

# ---- 1. 技能清单：目录 + frontmatter name ----
$skillDirs = @(Get-ChildItem -LiteralPath $skillsDir -Directory | Sort-Object Name)
if ($skillDirs.Count -eq 0) {
    Write-Host 'FAIL: .agents/skills 下没有任何技能目录' -ForegroundColor Red
    exit 1
}

$scanTargets = [System.Collections.Generic.List[object]]::new()
foreach ($d in $skillDirs) {
    $skillMd = Join-Path $d.FullName 'SKILL.md'
    if (-not (Test-Path -LiteralPath $skillMd)) {
        $errors.Add("技能目录 $($d.Name) 缺少 SKILL.md")
        continue
    }
    $head = @(Get-Content -LiteralPath $skillMd -TotalCount 12 -Encoding UTF8)
    $declared = [regex]::Match(($head -join "`n"), '(?m)^name:\s*(\S+)\s*$')
    if (-not $declared.Success) {
        $errors.Add("$($d.Name)/SKILL.md 的 frontmatter 缺少 name")
    } elseif ($declared.Groups[1].Value.Trim() -ne $d.Name) {
        $errors.Add("$($d.Name)/SKILL.md 的 name「$($declared.Groups[1].Value.Trim())」与目录名不一致")
    }
    $scanTargets.Add([pscustomobject]@{
        Label   = $d.Name
        Path    = $d.FullName
        GitPath = "$skillsDirGitPath/$($d.Name)"
        IsDir   = $true
    })
}
# 技能根目录的 README 也纳入；_map.md 是输入本身，不扫，避免自指
$readmePath = Join-Path $skillsDir 'README.md'
if (Test-Path -LiteralPath $readmePath) {
    $scanTargets.Add([pscustomobject]@{
        Label   = '(skills 根)'
        Path    = $readmePath
        GitPath = "$skillsDirGitPath/README.md"
        IsDir   = $false
    })
}

# ---- 2. 引用抽取与校验 ----
# 抽取口径：任意 `片段/…`，再按顶层片段判定。
#   · 必须「带文件扩展名」或「以 / 结尾」，否则视为行文片段（如 docs/decisions/ADR-）；
#   · 顶层片段必须是 ASCII（含中文的一律是行文，如「脚本/目录」）；
#   · 顶层片段在忽略表里的直接跳过。
$refPattern = '(?<![\w.\-/])([\w.\-]+(?:/[\w.\-/]+)+)'
$trimChars = [char[]] @('.', ',', ';', ':', ')', '）', '。', '，', '、', '」', '』')
$asciiTop = '^[A-Za-z._][A-Za-z0-9._\-]*$'

$checkedRefs = 0
$seen = [System.Collections.Generic.HashSet[string]]::new()

foreach ($target in $scanTargets) {
    $files = if ($target.IsDir) {
        @(Get-ChildItem -LiteralPath $target.Path -Recurse -File |
            Where-Object { $_.Extension -in @('.md', '.yaml', '.sh') })
    } else {
        @(Get-Item -LiteralPath $target.Path)
    }

    foreach ($file in $files) {
        $text = Get-Content -Raw -Encoding UTF8 $file.FullName
        foreach ($match in [regex]::Matches($text, $refPattern)) {
            $raw = $match.Value.TrimEnd($trimChars)
            if ([string]::IsNullOrWhiteSpace($raw)) { continue }
            if (-not ($raw.EndsWith('/') -or $raw -match '\.[A-Za-z0-9]+$')) { continue }
            $ref = $raw.TrimEnd('/')
            if ([string]::IsNullOrWhiteSpace($ref)) { continue }

            $top = $ref.Split('/')[0]
            if ($top -notmatch $asciiTop) { continue }
            if ($ignoreTop.Contains($top)) { continue }
            # 技能目录内的相对写法（如 diagnosing-bugs 的 scripts/xxx.sh）
            if ($target.IsDir -and (Test-Path -LiteralPath (Join-Path $target.Path $ref))) { continue }
            if (-not $seen.Add("$($target.Label)|$ref")) { continue }

            $kind = Get-PolicyKind -Ref $ref

            # 第 3 道关：顶层片段既不在仓库根、也没登记任何口径 ⇒ 目录被删了但技能还在引用
            if (-not (Test-RepoPath $top) -and [string]::IsNullOrWhiteSpace($kind)) {
                $errors.Add("$($target.Label) 引用了不存在的顶层片段「$top」（$ref）——" +
                            "该目录既不在仓库根，也未登记口径；请修引用，或登记进 .agents/skills/_map.md")
                continue
            }

            $exists = Test-RepoPath $ref
            switch ($kind) {
                '本机' {
                    if (-not $exists) {
                        $warnings.Add("$($target.Label) 引用本机资产 $ref ——本工作副本不存在（别人克隆里本就没有）")
                    }
                }
                '已退役' {
                    if ($exists) {
                        $errors.Add("$ref 登记为「已退役」，但它又存在了——退役被回退？确认后更新 _map.md")
                    }
                }
                { $_ -in @('外部', '相对') } { }
                default {
                    if (-not $exists) {
                        $errors.Add("$($target.Label) 引用了不存在的资产：$ref" +
                                    "（若它属本机/已退役/外部/相对，请登记进 .agents/skills/_map.md 第二节）")
                    } else {
                        $checkedRefs++
                        # 自指豁免：引用的资产就是技能自己的容器目录（如 skills README 引用 .agents/skills），
                        # 它必然不早于技能本身，报陈旧是误报。
                        $selfContainer = ($target.GitPath -eq $ref) -or $target.GitPath.StartsWith("$ref/")
                        if (-not $selfContainer) {
                            $assetAt = Get-LastCommitSeconds $ref
                            $skillAt = Get-LastCommitSeconds $target.GitPath
                            if ($skillAt -gt 0 -and $assetAt -gt $skillAt) {
                                $stales.Add("$($target.Label) 引用的 $ref 比技能本身更新——请复核技能正文是否已陈旧")
                            }
                        }
                    }
                }
            }
        }
    }
}

# ---- 3. 汇总 ----
# 陈旧（STALE）只提示、不阻断：它是 git 时间戳代理，资产一改就命中（改门禁脚本本身也会让 README 命中），
# 若拿来阻断，只会逼出"为了过门禁而空改文档"的动作。可阻断的是确定性的三项：
# 名实一致、引用存在、无未识别顶层片段（-Strict 下再加警告）。
Write-Host "== Agent 技能检查（扫描 $($scanTargets.Count) 个技能单元，存在性已校验 $checkedRefs 条）==" -ForegroundColor Cyan
foreach ($s in $stales) { Write-Host "  [STALE] $s" -ForegroundColor DarkYellow }
foreach ($w in $warnings) { Write-Host "  [WARN]  $w" -ForegroundColor Yellow }
foreach ($e in $errors) { Write-Host "  [ERROR] $e" -ForegroundColor Red }

$blocking = $errors.Count + $(if ($Strict) { $warnings.Count } else { 0 })
if ($blocking -gt 0) {
    Write-Host "技能门禁不通过：$($errors.Count) 个错误、$($warnings.Count) 条警告（另有 $($stales.Count) 条陈旧，仅提示）。" -ForegroundColor Red
    exit 1
}
if ($warnings.Count -gt 0 -or $stales.Count -gt 0) {
    Write-Host "技能门禁通过：$($warnings.Count) 条警告（另 $($stales.Count) 条陈旧，仅提示、请自行复核）。" -ForegroundColor Yellow
} else {
    Write-Host '技能门禁通过：引用全部存在、无未识别顶层片段、无陈旧。' -ForegroundColor Green
}
exit 0
