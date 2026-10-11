#Requires -Version 7.0
<#
.SYNOPSIS
    决策记录索引：把 docs/decisions/ADR-*.md 抽成一张表（编号 / 标题 / 状态 / 日期 / 决策者 / 交叉引用），并守住它与决策目录一致。

.DESCRIPTION
    回答的是"**哪篇 ADR 还能照着做**"——35 篇决策记录里，有的已收口、有的实施中、有的仍是草案，
    而状态只写在正文里（且写法不统一：bullet、引用块、甚至塞在 H1 后缀里）。只按文件名找，
    很容易把**草案当既定设计**照抄进代码。

    本脚本**只做抽取，不做人工归类**——人工归类一定会腐坏（本仓的教训：手写地图必须自带"落后即红"）。
    索引里每一格都来自 ADR 原文：状态就是作者写的那句话（过长则截断并标注），
    日期 / 决策者同理。所以它可以被 `-Check` 与决策目录逐项对齐，不存在"第二真源"。

    顺带产出**头部待补清单**：哪些篇目缺日期 / 缺决策者 / 把状态埋在引用块里。
    这是给将来做头部规范化的人一份可执行的工作单——本脚本不去改决策记录（历史不追改）。

.PARAMETER Root
    仓库根；默认由脚本位置推导。

.PARAMETER OutFile
    索引落盘路径（相对仓库根），默认 `docs/guide/_index/adrs.md`。

.PARAMETER Check
    只校验、不写文件；不成立即退出码 1（CI 用这条）。

.PARAMETER Json
    打印结构化数据（供 AI 助手或其它脚本消费），不写文件。

.PARAMETER SelfTest
    正反自检：用合成的 ADR 文本驱动解析器，覆盖三种头部写法与截断、交叉引用排除自身。

.EXAMPLE
    pwsh scripts/gen-adr-index.ps1            # 重新生成索引
    pwsh scripts/gen-adr-index.ps1 -Check     # 校验（CI 用这条）
    pwsh scripts/gen-adr-index.ps1 -Json      # 机器可读
    pwsh scripts/gen-adr-index.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string] $OutFile = 'docs/guide/_index/adrs.md',
    [switch] $Check,
    [switch] $Json,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$AdrDir = 'docs/decisions'
$StatusExcerptMax = 60

# ---------- 纯函数：解析一篇 ADR ----------
function Get-AdrEntry {
    <#
      输入：文件名（ADR-001-前端基础.md）与正文。
      输出：@{ Number; SlugTitle; Title; Status; StatusSource; Date; Decider; Refs }
        · StatusSource = bullet（`- 状态：…`）| quote（`> **状态：…**`）| missing
        · Refs = 正文里提到的其它 ADR 编号（不含自身）——只作导航，**不代表取代关系**
      纯函数（输入是字符串），便于 -SelfTest 用合成文本驱动。
    #>
    param(
        [Parameter(Mandatory)][string] $FileName,
        [Parameter(Mandatory)][AllowEmptyString()][string] $Text
    )

    $numberMatch = [regex]::Match($FileName, 'ADR-(\d{3})')
    $number = if ($numberMatch.Success) { $numberMatch.Groups[1].Value } else { '' }
    $slug = [regex]::Replace($FileName, '^ADR-\d+-', '') -replace '\.md$', ''

    $title = ''
    $h1 = [regex]::Match($Text, '(?m)^#\s+(?<t>.+)$')
    if ($h1.Success) {
        $title = $h1.Groups['t'].Value.Trim()
        $title = [regex]::Replace($title, '^ADR-\d+\s*[：:]\s*', '')
    }
    if ($title -eq '') { $title = $slug }
    $title = $title -replace '\*\*', ''

    $status = ''
    $statusSource = 'missing'
    $bullet = [regex]::Match($Text, '(?m)^-\s*状态：\s*(?<s>.+?)\s*$')
    if ($bullet.Success) {
        $status = $bullet.Groups['s'].Value
        $statusSource = 'bullet'
    } else {
        $quote = [regex]::Match($Text, '(?m)^>\s*\*\*状态[:：]\s*(?<s>.+?)\*\*')
        if ($quote.Success) {
            $status = $quote.Groups['s'].Value
            $statusSource = 'quote'
        }
    }
    $status = ($status -replace '\*\*', '').Trim()

    $dateMatch = [regex]::Match($Text, '(?m)^-\s*日期：\s*(?<d>\d{4}-\d{2}-\d{2})')
    $date = if ($dateMatch.Success) { $dateMatch.Groups['d'].Value } else { '' }

    $deciderMatch = [regex]::Match($Text, '(?m)^-\s*决策者：\s*(?<v>.+?)\s*$')
    $decider = if ($deciderMatch.Success) { $deciderMatch.Groups['v'].Value.Trim() } else { '' }

    $versionMatch = [regex]::Match($Text, '(?m)^-\s*版本：\s*(?<v>.+?)\s*$')
    $version = if ($versionMatch.Success) { $versionMatch.Groups['v'].Value.Trim() } else { '' }

    $refs = [System.Collections.Generic.List[string]]::new()
    foreach ($m in [regex]::Matches($Text, 'ADR-(\d{3})')) {
        $ref = $m.Groups[1].Value
        if ($ref -eq $number -or $refs.Contains($ref)) { continue }
        $refs.Add($ref)
    }

    return [pscustomobject]@{
        Number       = $number
        SlugTitle    = $slug
        Title        = $title
        Status       = $status
        StatusSource = $statusSource
        Version      = $version
        Date         = $date
        Decider      = $decider
        Refs         = @($refs | Sort-Object)
    }
}

# ---------- 纯函数：指纹 ----------
function Get-AdrFingerprint {
    <#
      只覆盖"索引语义"（编号 / 标题 / 状态原文 / 日期 / 决策者 / 交叉引用），不含任何时间或行号。
    #>
    param([object[]] $Entries)

    $sb = [System.Text.StringBuilder]::new()
    foreach ($e in ($Entries | Sort-Object Number)) {
        $null = $sb.AppendLine(@($e.Number, $e.Title, $e.Status, $e.StatusSource, $e.Version, $e.Date, $e.Decider, ($e.Refs -join ',')) -join '|')
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($sb.ToString())
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return ([System.BitConverter]::ToString($hash) -replace '-', '').Substring(0, 16).ToLowerInvariant()
}

# ---------- 自检 ----------
if ($SelfTest) {
    $bulletText = @'
# ADR-001：使用 Tabler 作为可隔离的前端视觉基础

- 状态：已接受
- 版本：v3
- 日期：2026-08-02
- 决策者：项目团队

## 背景

见 ADR-007 的修订。
'@
    $e = Get-AdrEntry -FileName 'ADR-001-前端基础.md' -Text $bulletText
    if ($e.Number -ne '001' -or $e.Title -ne '使用 Tabler 作为可隔离的前端视觉基础') { throw "自检失败：编号/标题解析不对（$($e.Number) / $($e.Title)）" }
    if ($e.Status -ne '已接受' -or $e.StatusSource -ne 'bullet') { throw "自检失败：bullet 状态解析不对（$($e.Status) / $($e.StatusSource)）" }
    if ($e.Version -ne 'v3') { throw "自检失败：版本解析不对（$($e.Version)）" }
    if ($e.Date -ne '2026-08-02' -or $e.Decider -ne '项目团队') { throw '自检失败：日期/决策者解析不对' }
    if (@($e.Refs) -notcontains '007') { throw '自检失败：交叉引用没抓到 ADR-007' }
    if (@($e.Refs) -contains '001') { throw '自检失败：交叉引用不该包含自身' }
    Write-Host '  [PASS] 正向自检：bullet 头部 + 交叉引用（排除自身）'

    $quoteText = @'
# ADR-018：单据操作按钮——**已定稿，实施中 v8**

> **状态：已定稿并进入实施（v8，2026-09-23）**。§8 讨论清单全部转"已定"。
'@
    $q = Get-AdrEntry -FileName 'ADR-018-单据动作按钮.md' -Text $quoteText
    if ($q.StatusSource -ne 'quote') { throw "自检失败：引用块里的状态没识别（$($q.StatusSource)）" }
    if ($q.Status -notmatch '^已定稿并进入实施') { throw "自检失败：引用块状态内容不对（$($q.Status)）" }
    if ($q.Title -notmatch '单据操作按钮' -or $q.Title -match '\*\*') { throw "自检失败：标题里的粗体标记没清掉（$($q.Title)）" }
    if ($q.Date -ne '' -or $q.Decider -ne '') { throw '自检失败：缺日期/决策者时应为空串（供头部待补清单用）' }
    Write-Host '  [PASS] 反向自检：引用块状态 / 缺日期 / 标题去粗体'

    $long = ('已接受（' + ('x' * 200) + '）')
    $longText = "# ADR-099：长状态`n`n- 状态：$long`n"
    $l = Get-AdrEntry -FileName 'ADR-099-长状态.md' -Text $longText
    if ($l.Status.Length -le $StatusExcerptMax) { throw '自检失败：长状态应原样保留（截断只在渲染时做，判定要用全文）' }
    Write-Host '  [PASS] 边界自检：长状态原样保留，截断只发生在渲染'

    $fp1 = Get-AdrFingerprint -Entries @($e)
    $fp2 = Get-AdrFingerprint -Entries @($e)
    if ($fp1 -ne $fp2) { throw '自检失败：同一输入两次指纹不一致（CI 会随机红）' }
    $e2 = Get-AdrEntry -FileName 'ADR-001-前端基础.md' -Text ($bulletText -replace '已接受', '草案')
    if ((Get-AdrFingerprint -Entries @($e2)) -eq $fp1) { throw '自检失败：状态变了指纹却没变（索引落后不会被发现）' }
    Write-Host '  [PASS] 指纹自检：同输入同值、状态变则变'

    Write-Host '-- SELFTEST OK'
    exit 0
}

# ---------- 采集 ----------
$adrDir = Join-Path $Root $AdrDir
if (-not (Test-Path -LiteralPath $adrDir)) { throw "找不到决策目录：$adrDir" }

$entries = [System.Collections.Generic.List[object]]::new()
foreach ($file in (Get-ChildItem -LiteralPath $adrDir -File -Filter 'ADR-*.md' | Sort-Object Name)) {
    $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    $entries.Add((Get-AdrEntry -FileName $file.Name -Text $text))
}
$entries = @($entries | Sort-Object Number)

$noStatus = @($entries | Where-Object StatusSource -eq 'missing')
$quoteStatus = @($entries | Where-Object StatusSource -eq 'quote')
$noDate = @($entries | Where-Object Date -eq '')
$noDecider = @($entries | Where-Object Decider -eq '')
# 头部三项（状态 / 日期 / 决策者）齐全——口径见 CONTRIBUTING.md「架构决策记录」。
# 版本**不参与判定**：它是"有修订才写"的可选项，强制补 v1 只会往决策记录里塞无信息的内容。
$conformCount = @($entries | Where-Object { $_.Status -ne '' -and $_.Date -ne '' -and $_.Decider -ne '' }).Count
$versionedCount = @($entries | Where-Object { $_.Version -ne '' }).Count
$fingerprint = Get-AdrFingerprint -Entries $entries

# ---------- 渲染 ----------
function Format-Cell {
    param([string] $Value, [int] $Max = 120)
    $v = ($Value -replace '\s+', ' ').Trim() -replace '\|', '\|'
    if ($v.Length -gt $Max) { $v = $v.Substring(0, $Max - 1) + '…' }
    return $v
}

function New-AdrIndexLines {
    param(
        [object[]] $Entries,
        [object[]] $NoStatus,
        [object[]] $QuoteStatus,
        [object[]] $NoDate,
        [object[]] $NoDecider,
        [string] $Fingerprint
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('<!-- adr-index-fingerprint: ' + $Fingerprint + ' -->')
    $lines.Add('# 决策记录索引（`docs/decisions/`）')
    $lines.Add('')
    $lines.Add('> 本文件由 [`scripts/gen-adr-index.ps1`](../../../scripts/gen-adr-index.ps1) **生成**，不要手改；')
    $lines.Add('> `pwsh scripts/gen-adr-index.ps1 -Check` 在 CI 里校验它与决策目录一致（新增 / 改了 ADR 后没重新生成即红）。')
    $lines.Add('>')
    $lines.Add('> **每格都来自 ADR 原文，没有人工归类**——状态就是作者写的那句话（过长只做截断）。')
    $lines.Add('> 人工归类一定会腐坏，所以这里不设"我判定它是已收口"这一类结论：**要判断能不能照着做，读状态原文**。')
    $lines.Add('> 最后一列是"正文里提到的其它 ADR"，**只作导航，不代表取代关系**。')
    $lines.Add('')
    $lines.Add('## 一、总览')
    $lines.Add('')
    # 注意：方法调用里的逗号会截断 -f 的操作数，多参数的格式化必须整体加括号。
    $conform = @($Entries | Where-Object { $_.Status -ne '' -and $_.Date -ne '' -and $_.Decider -ne '' })
    $versioned = @($Entries | Where-Object { $_.Version -ne '' })
    $lines.Add(('- 决策记录 **{0}** 篇；**头部三项（状态 / 日期 / 决策者）齐全的 {1} 篇**（口径见 `CONTRIBUTING.md`「架构决策记录」）' -f $Entries.Count, $conform.Count))
    $lines.Add(('- 状态写法：bullet `- 状态：` **{0}** 篇 · 引用块 `> **状态：**` **{1}** 篇 · **无状态行 {2} 篇**' -f `
            ($Entries.Count - $NoStatus.Count - $QuoteStatus.Count), $QuoteStatus.Count, $NoStatus.Count))
    $lines.Add(('- 版本（**按需**：有修订才写）**{0}** 篇写了 · 缺日期 **{1}** 篇 · 缺决策者 **{2}** 篇（详见第三节）' -f `
            $versioned.Count, $NoDate.Count, $NoDecider.Count))
    $lines.Add('')
    $lines.Add('## 二、索引')
    $lines.Add('')
    $lines.Add('| 编号 | 标题 | 状态（原文） | 版本 | 日期 | 决策者 | 提到的其它 ADR |')
    $lines.Add('|---|---|---|---|---|---|---|')
    foreach ($e in $Entries) {
        $status = if ($e.Status -eq '') { '**（无状态行）**' } else { Format-Cell $e.Status $StatusExcerptMax }
        if ($e.StatusSource -eq 'quote') { $status = $status + '〔引用块〕' }
        $version = if ($e.Version -ne '') { Format-Cell $e.Version 16 } else { '—' }
        $date = if ($e.Date -ne '') { $e.Date } else { '—' }
        $decider = if ($e.Decider -ne '') { $e.Decider } else { '—' }
        $refs = if (@($e.Refs).Count -gt 0) { (@($e.Refs) | ForEach-Object { "ADR-$_" }) -join '、' } else { '—' }
        $lines.Add(('| ADR-{0} | {1} | {2} | {3} | {4} | {5} | {6} |' -f `
                $e.Number, (Format-Cell $e.Title 60), $status, $version, $date, (Format-Cell $decider 20), (Format-Cell $refs 90)))
    }
    $lines.Add('')
    $lines.Add('## 三、头部缺项（口径：状态 / 日期 / 决策者**必填**，版本**按需**）')
    $lines.Add('')
    $lines.Add('本节由 `-Check` 一并校验：**有缺项即 CI 报红**（口径与写法见 `CONTRIBUTING.md`「架构决策记录」）。')
    $lines.Add('状态允许写成 `- 状态：` bullet（新篇目一律用这种）或引用块 `> **状态：**`（历史篇目的既成写法，两种都能被本索引识别）。')
    $lines.Add('')
    $work = [System.Collections.Generic.List[string]]::new()
    foreach ($e in $NoStatus) { $work.Add(('- ADR-{0}：**无状态行**（状态可能塞在 H1 后缀里）' -f $e.Number)) }
    foreach ($e in $NoDate) { $work.Add(('- ADR-{0}：缺 `- 日期：`' -f $e.Number)) }
    foreach ($e in $NoDecider) { $work.Add(('- ADR-{0}：缺 `- 决策者：`（写 `用户（Larry）+ 开发 Agent`）' -f $e.Number)) }
    if ($work.Count -eq 0) {
        $lines.Add('（无：每篇都有状态、日期与决策者）')
    } else {
        foreach ($line in $work) { $lines.Add($line) }
    }
    $lines.Add('')
    return @($lines)
}

$indexLines = New-AdrIndexLines -Entries $entries -NoStatus $noStatus -QuoteStatus $quoteStatus `
    -NoDate $noDate -NoDecider $noDecider -Fingerprint $fingerprint
$indexContent = ($indexLines -join "`n") + "`n"
$outPath = Join-Path $Root $OutFile

if ($Json) {
    @($entries) | ConvertTo-Json -Depth 4
    exit 0
}

if ($Check) {
    $failures = [System.Collections.Generic.List[string]]::new()

    # 解析完整性：每篇 ADR 都必须解析出编号与标题（否则是解析器跟不上写法，不是"目录很干净"）
    $broken = @($entries | Where-Object { $_.Number -eq '' -or $_.Title -eq '' })
    foreach ($e in $broken) { $failures.Add("ADR 文件解析不出编号或标题：$($e.SlugTitle)——解析器需要跟上写法") }
    $adrCount = @(Get-ChildItem -LiteralPath $adrDir -File -Filter 'ADR-*.md').Count
    if ($entries.Count -ne $adrCount) {
        $failures.Add("决策目录有 $adrCount 篇 ADR，索引只解析出 $($entries.Count) 篇——解析器漏读了文件")
    }

    # 头部三项必填（状态 / 日期 / 决策者）——口径见 CONTRIBUTING.md「架构决策记录」。
    # 版本按需，故不校验；这里不做"历史遗留豁免"：写 ADR 时补齐本来就是一行的事。
    foreach ($e in $entries) {
        $missing = [System.Collections.Generic.List[string]]::new()
        if ($e.Status -eq '') { $missing.Add('状态') }
        if ($e.Date -eq '') { $missing.Add('日期') }
        if ($e.Decider -eq '') { $missing.Add('决策者') }
        if ($missing.Count -gt 0) {
            $failures.Add("ADR-$($e.Number) 缺头部必填项：$($missing -join '、')——写法见 CONTRIBUTING.md「架构决策记录」（状态 bullet、日期 `YYYY-MM-DD`、决策者 `用户（Larry）+ 开发 Agent`）")
        }
    }

    if (-not (Test-Path -LiteralPath $outPath)) {
        $failures.Add("索引 $OutFile 不存在：跑一次 pwsh scripts/gen-adr-index.ps1 生成它")
    } else {
        $diskText = Get-Content -LiteralPath $outPath -Raw -Encoding UTF8
        $fpMatch = [regex]::Match($diskText, 'adr-index-fingerprint:\s*(?<fp>[0-9a-f]+)')
        $diskFp = if ($fpMatch.Success) { $fpMatch.Groups['fp'].Value } else { '' }
        if ($diskFp -eq '') {
            $failures.Add("索引 $OutFile 里没有 adr-index-fingerprint：跑一次 pwsh scripts/gen-adr-index.ps1 重新生成")
        } elseif ($diskFp -ne $fingerprint) {
            $failures.Add("索引 $OutFile 已落后于决策目录（指纹 $diskFp ≠ $fingerprint）：跑一次 pwsh scripts/gen-adr-index.ps1 重新生成并提交")
        }
    }

    if ($failures.Count -gt 0) {
        Write-Host '== 决策记录索引门禁 ==' -ForegroundColor Cyan
        foreach ($line in $failures) { Write-Host "  [FAIL] $line" -ForegroundColor Red }
        Write-Host ("-- FAIL（ADR {0} 篇，索引解析 {1} 篇）" -f $adrCount, $entries.Count)
        exit 1
    }
    Write-Host ("PASS 决策记录索引：{0} 篇与决策目录一致；头部三项齐全 {1} 篇（版本按需，{2} 篇写了）；引用块状态 {3} 篇" -f `
            $entries.Count, $conformCount, $versionedCount, $quoteStatus.Count)
    exit 0
}

$outDir = Split-Path -Parent $outPath
if (-not (Test-Path -LiteralPath $outDir)) { $null = New-Item -ItemType Directory -Path $outDir -Force }
Set-Content -LiteralPath $outPath -Value $indexContent -Encoding utf8 -NoNewline

Write-Host '== 决策记录索引已生成 ==' -ForegroundColor Cyan
Write-Host "  文件：$OutFile"
Write-Host ("  ADR {0} 篇：头部三项齐全 {1} 篇（版本按需，{2} 篇写了）；状态 bullet {3} · 引用块 {4} · 无状态行 {5}" -f `
        $entries.Count, $conformCount, $versionedCount, ($entries.Count - $noStatus.Count - $quoteStatus.Count), $quoteStatus.Count, $noStatus.Count)
Write-Host ("  缺日期 {0} 篇 · 缺决策者 {1} 篇（见索引第三节）" -f `
        $noDate.Count, $noDecider.Count)
exit 0
