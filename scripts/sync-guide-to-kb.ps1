#Requires -Version 7.0
<#
.SYNOPSIS
开发手册与决策记录 → 知识库同步（按内容三分推送）。

.DESCRIPTION
把"只在文档里"的知识推进知识库，供助手检索：

  · 规范与坑   docs/guide/03-工程规范与硬约束.md 全文 + 其余非机制篇目的「禁止 / 不要」节
               → 集合 kb_guide_conventions
  · 设计动因   docs/decisions/*.md 全文
               → 集合 kb_guide_rationale
  · 机制篇目   （元数据、权限、工作台、表单、选择器、报表等"系统怎么运作"的篇章）**一律不进库**：
               机制的真值在元数据与代码注册表里，进库即造第二真源，助手会偏向读文档
               （机制问题由能力目录 describe_mechanism 现算）。

推送走既有入库端点 POST /api/v1/assistant/kb/documents（同一套敏感拦截、引用复核与分块口径），
不另造入库路径；该端点要求登录身份并按当前用户重新授权（入库门 = 模块 2302 的 CanSetup），
默认用开发账号登录（-UserId / -Password 可覆盖）。

三条工程量：

  1. 幂等：内容哈希（SHA-256 十六进制大写，与入库端点同一算法）与库内该来源的现役版本比对，
     未变**不入库**；重灌只发生在内容真的变了的时候。
  2. 标题层级：入库端点按段落切块、不认 Markdown 标题，因此**推送前**把每个段落所在的
     标题路径写进段落前缀（〔一级 › 二级 › 三级〕），保证切出来的块带着层级、不是断了上下文的碎段。
  3. 来源可溯：SOURCE_URI 记录文档在仓库里的位置（含节锚点），重灌时同一来源的版本号递增。

.PARAMETER Only
只跑点名的子集（按文件名做子串匹配，可多个）：试点时用，如 -Only '03','ADR-016'。

.PARAMETER Plan
只算不推：打印"将新增 / 将跳过"与内容哈希，不调 API（库不可连时退化为只算）。

.PARAMETER Verify
只核对不推送：断言知识库里没有机制篇目、也没有 docs/eos-development/ 的业务知识（F4 口径）。

.PARAMETER BaseUrl
API 基地址，默认 http://localhost:5261。

.PARAMETER UserId
入库账号，默认开发账号 admin。入库端点按当前用户重新授权（门是模块 2302 的 CanSetup），
换账号或换环境时用 -UserId / -Password 覆盖。

.PARAMETER Password
入库账号口令，默认与开发账号一致。

.EXAMPLE
pwsh scripts/sync-guide-to-kb.ps1 -Plan
pwsh scripts/sync-guide-to-kb.ps1 -Verify
pwsh scripts/sync-guide-to-kb.ps1 -Only '03'
pwsh scripts/sync-guide-to-kb.ps1
#>
[CmdletBinding()]
param(
    [string[]] $Only = @(),
    [switch] $Plan,
    [switch] $Verify,
    [switch] $SelfTest,
    [string] $BaseUrl = 'http://localhost:5261',
    [string] $UserId = 'admin',
    [string] $Password = 'admin',
    [string] $GuideDir,
    [string] $DecisionsDir,
    [string] $ConnectionString
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $GuideDir) { $GuideDir = Join-Path $root 'docs/guide' }
if (-not $DecisionsDir) { $DecisionsDir = Join-Path $root 'docs/decisions' }

$ConventionsCollection = 'kb_guide_conventions'
$RationaleCollection = 'kb_guide_rationale'

# 开发手册的读者是开发/实施工程师，可见性与知识库入库门同档（CONSULTANT 档 = 模块 2302 的 CanSetup）。
$Visibility = 'CONSULTANT'

<#
  机制篇目（不进库）。判据是**内容类型**：讲"系统怎么运作、数据怎么存、字段从哪来"的篇章，
  其真值在元数据与代码注册表里，进库就是第二真源。列的"规范 / 坑 / 动因"三类的篇目不在此列。
#>
$MechanismDocs = @(
    '04-API契约与前后端协作.md',
    '10-元数据模型.md',
    '12-元数据消费.md',
    '20-认证与会话.md',
    '21-授权模型.md',
    '40-统一工作台.md',
    '41-定义快照与发布.md',
    '42-统一表单.md',
    '43-表单版式设计.md',
    '44-受控查询与条件模板.md',
    '45-受限表达式与虚拟字段.md',
    '46-统一选择器.md',
    '47-单据行为效果引擎.md',
    '48-生命周期与审批流.md',
    '50-统一报表.md',
    '51-报表可视化设计器.md',
    '52-报表打印版式.md'
)

# 「规范与坑」节：标题里出现这些词的节才抽（抽的是约束本身，不是机制叙述）。
$SectionPattern = '(禁止|不要|不得|红线|边界|约束|纪律|坑)'

function Test-Only {
    param([string] $Name)
    if ($Only.Count -eq 0) { return $true }
    foreach ($needle in $Only) {
        if ($Name -like "*$needle*") { return $true }
    }
    return $false
}

function Get-ContentHash {
    param([string] $Text)
    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
}

function ConvertTo-SqlLiteral {
    param([string] $Value)
    return "'" + ($Value -replace "'", "''") + "'"
}

<#
  响应正文按 UTF-8 解码。WebRequest 在部分响应（如错误体）上把 Content 交给 byte[]，
  直接管道给 ConvertFrom-Json 会丢掉 code 与 message——错误码是要据以判断前置的，丢不得。
#>
function Get-ResponseText {
    param($Response)
    if ($null -eq $Response -or $null -eq $Response.Content) { return '' }
    if ($Response.Content -is [byte[]]) { return [Text.Encoding]::UTF8.GetString($Response.Content) }
    return [string]$Response.Content
}

<#
  来源 URI：文档在仓库里的位置（整篇取路径，抽节加节锚点）。
  锚点是给人看的，但会进 SQL 字面量，命令行路径对**直引号**敏感，故统一去掉直引号并归一空白——
  业务标识（路径、节标题）本身不受影响。
#>
function New-SourceUri {
    param([string] $RelativePath, [string] $Anchor)
    $uri = "repo://$RelativePath"
    if ($Anchor) { $uri = "$uri#$Anchor" }
    return (($uri -replace '"', '') -replace '\s+', ' ').Trim()
}

<#
  把段落所在的标题路径写进段落前缀：切块只看段落与句末，不认 Markdown 标题，
  不补前缀的话"这条约束属于谁"会在切块后丢掉。
#>
function Add-HeadingPrefix {
    param([string] $Markdown, [string] $DocTitle)

    $stack = [System.Collections.Generic.List[string]]::new()
    $output = [System.Collections.Generic.List[string]]::new()
    $paragraph = [System.Collections.Generic.List[string]]::new()

    $flush = {
        if ($paragraph.Count -eq 0) { return }
        $path = if ($stack.Count -gt 0) { $stack -join ' › ' } else { $DocTitle }
        [void]$output.Add("〔$path〕" + $paragraph[0])
        for ($i = 1; $i -lt $paragraph.Count; $i++) { [void]$output.Add($paragraph[$i]) }
        $paragraph.Clear()
    }

    foreach ($line in ($Markdown -replace "`r`n", "`n").Split("`n")) {
        $heading = [regex]::Match($line, '^(#{1,6})\s+(.+?)\s*$')
        if ($heading.Success) {
            & $flush
            $level = $heading.Groups[1].Value.Length
            $text = $heading.Groups[2].Value
            while ($stack.Count -ge $level) { $stack.RemoveAt($stack.Count - 1) }
            [void]$stack.Add($text)
            continue
        }
        if ([string]::IsNullOrWhiteSpace($line)) {
            & $flush
            [void]$output.Add('')
            continue
        }
        [void]$paragraph.Add($line)
    }
    & $flush
    return ($output -join "`n").Trim()
}

<# 按标题把一篇 Markdown 切成 (标题, 正文) 列表；标题层级不入正文。 #>
function Split-MarkdownSections {
    param([string] $Markdown)

    $sections = [System.Collections.Generic.List[object]]::new()
    $currentTitle = ''
    $buffer = [System.Collections.Generic.List[string]]::new()

    $flush = {
        if ($currentTitle -and $buffer.Count -gt 0) {
            $sections.Add([pscustomobject]@{
                Title   = $currentTitle
                Content = ($buffer -join "`n").Trim()
            })
        }
        $buffer.Clear()
    }

    foreach ($line in ($Markdown -replace "`r`n", "`n").Split("`n")) {
        $heading = [regex]::Match($line, '^(#{1,6})\s+(.+?)\s*$')
        if ($heading.Success) {
            & $flush
            $currentTitle = $heading.Groups[2].Value
            continue
        }
        if ($currentTitle) { [void]$buffer.Add($line) }
    }
    & $flush
    return $sections
}

function Get-DocTitle {
    param([string] $Markdown, [string] $Fallback)
    foreach ($line in ($Markdown -replace "`r`n", "`n").Split("`n")) {
        $heading = [regex]::Match($line, '^#\s+(.+?)\s*$')
        if ($heading.Success) { return $heading.Groups[1].Value }
    }
    return $Fallback
}

# ---- -SelfTest：标题前缀与哈希口径的正反自检（不连库、不调 API）----
if ($SelfTest) {
    $failures = [System.Collections.Generic.List[string]]::new()

    $sample = "# 顶层标题`n`n正文第一段。`n`n## 二级标题`n`n正文第二段。`n`n### 三级标题`n`n正文第三段。"
    $prefixed = Add-HeadingPrefix -Markdown $sample -DocTitle '示例'
    foreach ($expected in @('〔顶层标题〕', '〔顶层标题 › 二级标题〕', '〔顶层标题 › 二级标题 › 三级标题〕')) {
        if ($prefixed -notmatch [regex]::Escape($expected)) { $failures.Add("标题层级丢失：缺少 $expected") }
    }

    $paragraphs = ($prefixed -replace "`r`n", "`n") -split "`n`n" | Where-Object { $_.Trim() -ne '' }
    $bare = @($paragraphs | Where-Object { $_.Trim() -notmatch '^〔' })
    if ($bare.Count -gt 0) { $failures.Add("有 $($bare.Count) 个段落没有标题路径前缀（切块会丢上下文）") }

    # 反例：无标题的纯文本仍要带上文档标题兜底，不能变成裸段。
    $plain = Add-HeadingPrefix -Markdown "没有标题的正文。" -DocTitle '兜底标题'
    if ($plain -notmatch '^〔兜底标题〕') { $failures.Add('无标题文档未回落文档标题作前缀') }

    # 哈希口径与入库端点一致（SHA-256 十六进制大写：同一段文本必须得到同一个哈希）。
    $a = Get-ContentHash '同文本'
    $b = Get-ContentHash '同文本'
    $c = Get-ContentHash '同文本2'
    if ($a -ne $b) { $failures.Add('同一内容两次哈希不一致（幂等判据会失效）') }
    if ($a -eq $c) { $failures.Add('不同内容得到同一哈希（幂等判据会误跳过）') }
    if ($a -notmatch '^[0-9A-F]{64}$') { $failures.Add("哈希形态不是 64 位大写十六进制：$a") }

    # 来源 URI 里的直引号必须被清掉（它会进 SQL 字面量与命令行，未清会让连库比对炸掉）。
    $uri = New-SourceUri -RelativePath 'docs/guide/61-x.md' -Anchor '二、防探测 404 与"权限问题要明说"的边界'
    if ($uri -match '"') { $failures.Add('来源 URI 仍含直引号') }
    if ($uri -notmatch '^repo://docs/guide/61-x\.md#') { $failures.Add("来源 URI 形态不对：$uri") }

    if ($failures.Count -eq 0) {
        Write-Host 'PASS 标题前缀、哈希口径与来源 URI 自检通过（正例 5 项 / 反例 2 项）。' -ForegroundColor Green
        exit 0
    }

    $failures | ForEach-Object { Write-Host "FAIL $_" -ForegroundColor Red }
    exit 1
}

# ---- 收集待推送的文档 ----
$items = [System.Collections.Generic.List[object]]::new()

foreach ($file in (Get-ChildItem -LiteralPath $GuideDir -Filter '*.md' -File | Sort-Object Name)) {
    if ($file.Name -in @('README.md', '_map.md')) { continue }
    if (-not (Test-Only $file.Name)) { continue }
    $relative = "docs/guide/$($file.Name)"
    $markdown = Get-Content -Raw -Encoding UTF8 $file.FullName
    $docTitle = Get-DocTitle -Markdown $markdown -Fallback $file.BaseName
    if ($MechanismDocs -contains $file.Name) { continue }

    if ($file.Name -eq '03-工程规范与硬约束.md') {
        $items.Add([pscustomobject]@{
            Collection = $ConventionsCollection
            Title      = "开发手册 · $docTitle"
            SourceUri  = (New-SourceUri -RelativePath $relative)
            Content    = (Add-HeadingPrefix -Markdown $markdown -DocTitle $docTitle)
            Path       = $relative
        })
        continue
    }

    foreach ($section in (Split-MarkdownSections -Markdown $markdown)) {
        if ($section.Title -notmatch $SectionPattern) { continue }
        if ($section.Content.Length -lt 40) { continue }
        $items.Add([pscustomobject]@{
            Collection = $ConventionsCollection
            Title      = "开发手册 · $docTitle › $($section.Title)"
            SourceUri  = (New-SourceUri -RelativePath $relative -Anchor $section.Title)
            Content    = (Add-HeadingPrefix -Markdown $section.Content -DocTitle "$docTitle › $($section.Title)")
            Path       = "$relative#$($section.Title)"
        })
    }
}

foreach ($file in (Get-ChildItem -LiteralPath $DecisionsDir -Filter '*.md' -File | Sort-Object Name)) {
    if (-not (Test-Only $file.Name)) { continue }
    $relative = "docs/decisions/$($file.Name)"
    $markdown = Get-Content -Raw -Encoding UTF8 $file.FullName
    $docTitle = Get-DocTitle -Markdown $markdown -Fallback $file.BaseName
    $items.Add([pscustomobject]@{
        Collection = $RationaleCollection
        Title      = "设计动因 · $docTitle"
        SourceUri  = (New-SourceUri -RelativePath $relative)
        Content    = (Add-HeadingPrefix -Markdown $markdown -DocTitle $docTitle)
        Path       = $relative
    })
}

foreach ($item in $items) {
    Add-Member -InputObject $item -NotePropertyName ContentHash -NotePropertyValue (Get-ContentHash $item.Content) -Force
}


# ---- -Verify：只核对库内没有"不该进库的东西" ----
if ($Verify) {
    . (Join-Path $PSScriptRoot 'dev/eos-sql.ps1')
    $violations = [System.Collections.Generic.List[string]]::new()
    foreach ($name in $MechanismDocs) {
        $uri = New-SourceUri -RelativePath "docs/guide/$name"
        $hits = @(Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query (
            "SELECT CONCAT(DOC_ID, CHAR(124), STATUS, CHAR(124), SOURCE_URI) FROM dbo.KB_DOCUMENT WITH (NOLOCK) " +
            "WHERE SOURCE_URI LIKE $(ConvertTo-SqlLiteral "$uri%");"))
        foreach ($hit in $hits) { $violations.Add("机制篇目已进库：$hit") }
    }

    $foreign = @(Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query (
        "SELECT CONCAT(DOC_ID, CHAR(124), SOURCE_URI) FROM dbo.KB_DOCUMENT WITH (NOLOCK) " +
        "WHERE SOURCE_URI LIKE '%docs/eos-development/%';"))
    foreach ($hit in $foreign) { $violations.Add("业务知识库被混灌：$hit") }

    if ($violations.Count -eq 0) {
        Write-Host "PASS 知识库内没有机制篇目，也没有 docs/eos-development/ 的业务知识（机制篇目 $($MechanismDocs.Count) 篇已排除）。" -ForegroundColor Green
        exit 0
    }

    $violations | ForEach-Object { Write-Host "FAIL $_" -ForegroundColor Red }
    exit 1
}

# ---- 与库内现役版本比对（幂等判据） ----
$dbReady = $true
$eosSql = Join-Path $PSScriptRoot 'dev/eos-sql.ps1'
$existing = @{}
if (Test-Path $eosSql) {
    try {
        . $eosSql
        $null = Invoke-EosSqlQuery -Query "SELECT 1;" -ConnectionString $ConnectionString
    }
    catch {
        $dbReady = $false
        Write-Host "[WARN] 库不可连，跳过幂等比对（只能 -Plan 预览）：$($_.Exception.Message)" -ForegroundColor Yellow
    }
}
else {
    $dbReady = $false
    Write-Host "[WARN] 未找到 $eosSql，跳过幂等比对。" -ForegroundColor Yellow
}

if ($dbReady) {
    foreach ($item in $items) {
        $query = "SELECT CONCAT(DOC_ID, CHAR(124), VERSION, CHAR(124), CONTENT_HASH) FROM dbo.KB_DOCUMENT WITH (NOLOCK) " +
                 "WHERE COLLECTION_ID=$(ConvertTo-SqlLiteral $item.Collection) " +
                 "AND SOURCE_URI=$(ConvertTo-SqlLiteral $item.SourceUri) AND STATUS=N'active';"
        $rows = @(Invoke-EosSqlQuery -Query $query -ConnectionString $ConnectionString)
        if ($rows.Count -gt 0) { $existing[$item.SourceUri] = ([string]$rows[0]).Trim() }
    }
}

# ---- 推送 ----
$created = 0
$skipped = 0
$failed = 0
$blocked = 0
$planned = 0
$apiReady = $false
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
if (-not $Plan) {
    try {
        $health = Invoke-WebRequest -Uri "$BaseUrl/health/live" -TimeoutSec 5 -UseBasicParsing
        $apiReady = $health.StatusCode -eq 200
    }
    catch {
        $apiReady = $false
    }
    if (-not $apiReady) {
        Write-Host "FAIL API 不在线（$BaseUrl/health/live 不可用）。请启动服务后重跑；本脚本不代劳启停服务。" -ForegroundColor Red
        Write-Host "     （只想看会推什么可以加 -Plan）" -ForegroundColor DarkGray
        exit 3
    }

    # 入库端点要求登录身份（库里按当前用户重新授权：入库门是模块 2302 的 CanSetup）。
    try {
        Invoke-RestMethod -Uri "$BaseUrl/api/v1/auth/login" -Method Post -ContentType 'application/json' `
            -Body (@{ userId = $UserId; password = $Password; rememberMe = $false } | ConvertTo-Json) `
            -WebSession $session | Out-Null
    }
    catch {
        Write-Host "FAIL 登录失败（$UserId）：$($_.Exception.Message)" -ForegroundColor Red
        Write-Host "     入库要求一个具备配置维护权限的账号，用 -UserId / -Password 指定。" -ForegroundColor DarkGray
        exit 3
    }
}

foreach ($item in $items) {
    $label = "$($item.Collection) · $($item.Title)"
    $current = if ($existing.ContainsKey($item.SourceUri)) { $existing[$item.SourceUri] } else { '' }

    if ($current) {
        $parts = $current -split '\|'
        if ($parts.Count -ge 3 -and $parts[2].Trim() -eq $item.ContentHash) {
            Write-Host "  跳过  $label （内容未变，现役 docId=$($parts[0]) version=$($parts[1])）" -ForegroundColor DarkGray
            $skipped++
            continue
        }
    }

    $action = if ($current) { '重灌' } else { '新增' }
    if ($Plan -or -not $apiReady) {
        Write-Host "  待$action $label （hash=$($item.ContentHash.Substring(0, 12))…，$($item.Content.Length) 字）" -ForegroundColor Cyan
        $planned++
        continue
    }

    $body = @{
        collectionId = $item.Collection
        title        = $item.Title
        sourceUri    = $item.SourceUri
        content      = $item.Content
        visibility   = $Visibility
    } | ConvertTo-Json -Depth 4 -Compress

    $response = Invoke-WebRequest -Uri "$BaseUrl/api/v1/assistant/kb/documents" -Method Post `
        -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body)) `
        -WebSession $session -TimeoutSec 300 -UseBasicParsing -SkipHttpErrorCheck
    # 错误体必须解码后再判断：只看状态码会丢掉 code（KB_SENSITIVE_BLOCKED / KB_EMBEDDING_NOT_CONFIGURED…）。
    $text = Get-ResponseText $response
    $code = ''
    if ($text) {
        try { $code = ($text | ConvertFrom-Json).code } catch { $code = '' }
    }

    if ($response.StatusCode -eq 200) {
        $payload = $text | ConvertFrom-Json
        Write-Host "  $action  $label （docId=$($payload.docId) reused=$($payload.reused) 块数=$($payload.chunks)）" -ForegroundColor Green
        if ($payload.reused) { $skipped++ } else { $created++ }
        continue
    }

    if ($code -eq 'KB_EMBEDDING_NOT_CONFIGURED') {
        Write-Host "  失败  $label —— 向量模型未接线（KB_EMBEDDING_NOT_CONFIGURED，HTTP $($response.StatusCode)）" -ForegroundColor Red
        Write-Host "         通道已建，检索在 embedding 就绪前不可用；接线后重跑本脚本即可。" -ForegroundColor Yellow
        $blocked++
        continue
    }

    Write-Host "  失败  $label —— HTTP $($response.StatusCode) $code $text" -ForegroundColor Red
    $failed++
}

Write-Host ""
Write-Host ("== 同步结果：候选 {0} 篇 ｜ 新增 {1} ｜ 跳过 {2} ｜ 待推 {3} ｜ 失败 {4} ｜ 向量未接线 {5} ==" -f `
    $items.Count, $created, $skipped, $planned, $failed, $blocked) -ForegroundColor Cyan
Write-Host "   集合：$ConventionsCollection（规范与坑）、$RationaleCollection（设计动因）；可见性 $Visibility"
if ($Plan) { Write-Host "   （-Plan：只算不推）" -ForegroundColor DarkGray }

if ($failed -gt 0) { exit 1 }
if ($blocked -gt 0) { exit 4 }
exit 0
