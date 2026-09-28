<#
.SYNOPSIS
    模块级表单版式改造的「观感零变化」对拍：改造前抓基线，改造后逐模块比对。

.DESCRIPTION
    版式从字段级（FIELDS.FORM_*）搬到模块级（MODULE_FORM_LAYOUT）时，运行时的定义会多出
    一个版式段、表单定义会多出设计权标志与字段占位属性。这些是**有意新增**的；除此之外
    任何一处变化都是回归——而这个改造触碰的是 279 个模块的表单渲染路径，`dotnet build`
    与单测都发现不了"少了一个字段""顺序变了""宽度变了"。

    故本脚本是这个改造**唯一可执行的判据**：
      - `-Capture <file>`：对全部工作台模块取"按当前配置重建的表单定义 JSON"落盘为基线；
      - `-Compare <file>`：重新取一遍并与基线逐字比对，**只放行新增的版式段与版式专用键**。

    取两样东西（都按当前配置现算，不读快照）：
      1. 工作台定义 JSON（`POST /api/v1/workbench-definitions/validate` 返回的 definitionJson）
         ——版式段在这里；
      2. 统一表单定义 JSON（`GET /api/v1/document-workbench/{id}/form-definition?mode=view`）
         ——用户实际看到的表单在这里。

    比对前先做**同构归一化**（两侧同一函数）：只放行**整体新增**的段与标志
    （`formLayout` 版式段、`canFormDesign` 设计权标志），以及取值等于默认值的键
    （`rowSpan=1`、`sectionId=null`）。**既有键的取值一律参与比对**——
    `span`/`newLine`/`cellGroup`/`cellRole`/`tabNo` 正是版式回归的落点
    （如"每行 4 个字段退化为 2 个"就落在 `span` 上）。差异打印到字段级（模块号 + JSON 路径 + 两侧取值）。

    模块清单取自库内 `MODULES.M_URL='/workbench'`（与需求口径一致），需连库；定义需重建，
    需 API 在线且账号有系统管理 Setup 权限。

.PARAMETER ApiUrl
    运行中的 API 地址。

.PARAMETER Capture
    抓基线：把归一化后的定义写入该文件（改造前执行，越早越好）。

.PARAMETER Compare
    比对：重新抓取并与该基线文件比对，有差异即 exit 1。

.PARAMETER SelfTest
    判别力自检：把同一套归一化与比对用在合成样本上——整体新增的段/标志/默认值键必须放行，
    列跨度、行跨度、复合格、页签、字段顺序五类变化必须逐条命中。不连库、不连 API。

.PARAMETER TimeoutSec
    整体超时（默认 1800 秒）。

.PARAMETER ModuleId
    只处理指定模块（试点/排障用；不指定即全部工作台模块）。

.EXAMPLE
    pwsh scripts/compare-form-layout-parity.ps1 -Capture C:\Temp\form-layout-baseline.json
    pwsh scripts/compare-form-layout-parity.ps1 -Compare C:\Temp\form-layout-baseline.json
    pwsh scripts/compare-form-layout-parity.ps1 -Capture C:\Temp\pilot.json -ModuleId 1201,1406
#>
[CmdletBinding()]
param(
    [string] $ApiUrl = 'http://localhost:5261',
    [string] $Capture,
    [string] $Compare,
    [int] $TimeoutSec = 1800,
    [int[]] $ModuleId,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

if (-not $Capture -and -not $Compare -and -not $SelfTest) {
    Write-Output 'FAIL 必须指定 -Capture <file> 或 -Compare <file>（或 -SelfTest）。'
    exit 2
}
if ($Capture -and $Compare) {
    Write-Output 'FAIL -Capture 与 -Compare 不能同时指定。'
    exit 2
}

# 归一化时**整键剔除**的键（大小写不敏感：定义快照用 PascalCase，表单定义接口用 camelCase）：
# FormLayout        —— 模块级版式段（新增段，整体放行）
# CanFormDesign     —— 表单定义下发的设计权标志（新增标志）
# 注意：只放行"整体新增"的段与标志。Span / NewLine / CellGroup / CellRole / TabNo 是**既有键**，
# 它们的**取值**正是版式回归的落点（如每行 4 个字段退化为 2 个），必须参与比对。
$script:IgnoredKeys = @('formlayout', 'canformdesign')

# 取值等于默认值即视同"缺省"剔除的键：RowSpan 默认 1、SectionId 默认空。
# 旧基线里没有这两个键，但它们与"默认值"语义相同——剔除后旧基线仍可比，
# 而 1→2 这类**行高回归**、或新增分节这类**真版式变化**照样能被抓到
# （早先整键剔除 rowspan/sectionid 会让这两类变化完全不可见）。
$script:DefaultedKeys = @{ 'rowspan' = '1'; 'sectionid' = '' }

function ConvertTo-EosParityNode {
    <# 递归剔除"整体新增"的键、以及取值等于默认值的键；返回 PSObject/数组/标量。 #>
    param([AllowNull()] $Value)

    if ($null -eq $Value) { return $null }
    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        $result = [ordered]@{}
        foreach ($property in $Value.PSObject.Properties) {
            $name = $property.Name.ToLowerInvariant()
            if ($script:IgnoredKeys -contains $name) { continue }
            if ($script:DefaultedKeys.ContainsKey($name) -and "$($property.Value)" -eq $script:DefaultedKeys[$name]) { continue }
            $result[$property.Name] = ConvertTo-EosParityNode -Value $property.Value
        }
        return [pscustomobject]$result
    }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        $items = @()
        foreach ($item in $Value) { $items += , (ConvertTo-EosParityNode -Value $item) }
        return , $items
    }
    return $Value
}

function ConvertTo-EosParityText {
    <# JSON 文本 → 归一化后的紧凑文本（两侧同法，故可逐字比较）。空/非法输入原样返回。 #>
    param([AllowNull()][string] $Json)

    if ([string]::IsNullOrWhiteSpace($Json)) { return '' }
    try {
        $node = $Json | ConvertFrom-Json -Depth 100
    }
    catch {
        return "RAW:$Json"
    }
    $normalized = ConvertTo-EosParityNode -Value $node
    return ($normalized | ConvertTo-Json -Depth 100 -Compress)
}

function Get-EosParitySnapshot {
    <# 逐模块取定义与表单，返回归一化后的快照数组。 #>
    param(
        [Parameter(Mandatory)] $Session,
        [Parameter(Mandatory)] [string] $ApiUrl,
        [Parameter(Mandatory)] [int[]] $ModuleIds
    )

    $snapshots = @()
    $index = 0
    foreach ($moduleId in $ModuleIds) {
        $index++
        # 进度走「信息流」而非成功流：函数的成功流只允许返回快照数组，混入文本会被调用方当成数据。
        if ($index % 25 -eq 1) {
            Write-Information -MessageData ("  取数进度 {0}/{1}" -f $index, $ModuleIds.Count) -InformationAction Continue
        }

        $definitionJson = $null
        $title = ''
        $passed = $false
        $failedChecks = @()
        try {
            $validation = Invoke-EosApi -Method 'POST' -Path '/api/v1/workbench-definitions/validate' `
                -Body @{ moduleId = $moduleId } -Session $Session -ApiUrl $ApiUrl
            if ($validation.Status -eq 200 -and $validation.Content) {
                $title = [string]$validation.Content.title
                $passed = [bool]$validation.Content.passed
                $definitionJson = [string]$validation.Content.definitionJson
                $failedChecks = @($validation.Content.checks | Where-Object { -not $_.passed } | ForEach-Object { [string]$_.code })
            }
            else {
                $failedChecks = @("validate_http_$($validation.Status)")
            }
        }
        catch {
            $failedChecks = @("validate_error_$($_.Exception.Message)")
        }

        # 表单定义：view 模式只需浏览权限，对全部工作台模块可用
        $formJson = $null
        $formStatus = 0
        try {
            $form = Invoke-EosApi -Method 'GET' -Path "/api/v1/document-workbench/$moduleId/form-definition?mode=view" `
                -Session $Session -ApiUrl $ApiUrl
            $formStatus = $form.Status
            if ($form.Status -eq 200) { $formJson = $form.Raw }
        }
        catch {
            $formStatus = -1
        }

        $snapshots += [pscustomobject]@{
            moduleId     = $moduleId
            title        = $title
            passed       = $passed
            failedChecks = $failedChecks
            formStatus   = $formStatus
            definition   = ConvertTo-EosParityText -Json $definitionJson
            form         = ConvertTo-EosParityText -Json $formJson
        }
    }
    return $snapshots
}

function Get-EosDiffPath {
    <# 递归比对两个节点，把差异按 JSON 路径收集（最多 $Limit 条）。 #>
    param(
        [AllowNull()] $Left,
        [AllowNull()] $Right,
        [string] $Path,
        [Parameter(Mandatory)] $Out,
        [int] $Limit = 20
    )

    if ($Out.Count -ge $Limit) { return }
    if ($null -eq $Left -and $null -eq $Right) { return }

    $leftIsObject = $Left -is [System.Management.Automation.PSCustomObject]
    $rightIsObject = $Right -is [System.Management.Automation.PSCustomObject]
    if ($leftIsObject -or $rightIsObject) {
        if (-not ($leftIsObject -and $rightIsObject)) {
            $Out.Add("$Path : 一侧是对象、另一侧是 $($Right.GetType().Name)")
            return
        }
        foreach ($property in $Left.PSObject.Properties) {
            if (-not ($Right.PSObject.Properties.Name -contains $property.Name)) {
                $Out.Add("$Path.$($property.Name) : 基线有、现无")
                if ($Out.Count -ge $Limit) { return }
            }
        }
        foreach ($property in $Right.PSObject.Properties) {
            if (-not ($Left.PSObject.Properties.Name -contains $property.Name)) {
                $Out.Add("$Path.$($property.Name) : 基线无、现有")
                if ($Out.Count -ge $Limit) { return }
            }
        }
        foreach ($property in $Left.PSObject.Properties) {
            if (-not ($Right.PSObject.Properties.Name -contains $property.Name)) { continue }
            Get-EosDiffPath -Left $property.Value -Right $Right.PSObject.Properties[$property.Name].Value `
                -Path "$Path.$($property.Name)" -Out $Out -Limit $Limit
            if ($Out.Count -ge $Limit) { return }
        }
        return
    }

    $leftIsArray = $Left -is [System.Collections.IEnumerable] -and $Left -isnot [string]
    $rightIsArray = $Right -is [System.Collections.IEnumerable] -and $Right -isnot [string]
    if ($leftIsArray -or $rightIsArray) {
        if (-not ($leftIsArray -and $rightIsArray)) {
            $Out.Add("$Path : 一侧是数组、另一侧不是")
            return
        }
        $leftItems = @($Left); $rightItems = @($Right)
        if ($leftItems.Count -ne $rightItems.Count) {
            $Out.Add("$Path : 元素数 $($leftItems.Count) -> $($rightItems.Count)")
        }
        $count = [Math]::Min($leftItems.Count, $rightItems.Count)
        for ($i = 0; $i -lt $count; $i++) {
            Get-EosDiffPath -Left $leftItems[$i] -Right $rightItems[$i] -Path "$Path[$i]" -Out $Out -Limit $Limit
            if ($Out.Count -ge $Limit) { return }
        }
        return
    }

    if ([string]$Left -ne [string]$Right) {
        $Out.Add("$Path : '$Left' -> '$Right'")
    }
}

function Write-EosDiffDetail {
    param([string] $Key, [string] $BaselineText, [string] $CurrentText, [int] $Limit = 20)

    $out = [System.Collections.Generic.List[string]]::new()
    $left = $null; $right = $null
    try { if ($BaselineText) { $left = $BaselineText | ConvertFrom-Json -Depth 100 } } catch { }
    try { if ($CurrentText) { $right = $CurrentText | ConvertFrom-Json -Depth 100 } } catch { }
    if ($null -eq $left -or $null -eq $right) {
        Write-Output "    $Key : 无法解析为 JSON（长度 $($BaselineText.Length) -> $($CurrentText.Length)）"
        Write-Output ("      基线…{0}…" -f $BaselineText.Substring(0, [Math]::Min(160, $BaselineText.Length)))
        Write-Output ("      现值…{0}…" -f $CurrentText.Substring(0, [Math]::Min(160, $CurrentText.Length)))
        return
    }
    Get-EosDiffPath -Left $left -Right $right -Path '$' -Out $out -Limit $Limit
    if ($out.Count -eq 0) { Write-Output "    $Key : 路径级比对无差异（差异在序列化细节）" }
    foreach ($line in $out) { Write-Output "    $Key $line" }
}

# ---------------------------------------------------------------- 判别力自检
<#
 把同一套归一化 + 比对用在合成样本上：整体新增的段与标志必须放行，
 而版式**取值**的任何变化（列跨度、行跨度、复合格、页签）必须被抓到。
 没有这层自检，"比对通过"可能只是"什么都没比"——本脚本早先正是因为整键剔除
 rowspan/sectionid，而对行高与分节的变化完全失明。
#>
function Test-EosParityDiscrimination {
    # 基线样本：改造前形态（无 rowSpan / sectionId / canFormDesign / formLayout）
    $baseline = '{"moduleId":1406,"columns":4,"masterFields":[' +
        '{"key":"A","span":1,"newLine":false,"cellGroup":"G","cellRole":1,"tabNo":1},' +
        '{"key":"B","span":1,"newLine":false,"cellGroup":null,"cellRole":0,"tabNo":1}]}'

    $cases = [ordered]@{
        # ① 完全相同 ⇒ 必须判"无差异"
        'IDENTICAL'        = @{ Json = $baseline; Expect = 'same' }
        # ② 只多了新增段 / 新增标志 / 取值等于默认值的键 ⇒ 必须判"无差异"
        'NEW_KEYS_ALLOWED' = @{
            Json   = '{"moduleId":1406,"columns":4,"canFormDesign":true,"formLayout":{"columns":4},"masterFields":[' +
                     '{"key":"A","span":1,"newLine":false,"cellGroup":"G","cellRole":1,"tabNo":1,"rowSpan":1,"sectionId":null},' +
                     '{"key":"B","span":1,"newLine":false,"cellGroup":null,"cellRole":0,"tabNo":1,"rowSpan":1,"sectionId":null}]}'
            Expect = 'same'
        }
        # ③ 列跨度 1→2（每行 4 个字段退化为 2 个）⇒ 必须抓到
        'SPAN_CHANGED'     = @{
            Json   = '{"moduleId":1406,"columns":4,"masterFields":[' +
                     '{"key":"A","span":2,"newLine":false,"cellGroup":"G","cellRole":1,"tabNo":1},' +
                     '{"key":"B","span":1,"newLine":false,"cellGroup":null,"cellRole":0,"tabNo":1}]}'
            Expect = 'diff'
        }
        # ④ 行跨度 1→2（整键剔除 rowSpan 时代的失明形态）⇒ 必须抓到
        'ROWSPAN_CHANGED'  = @{
            Json   = '{"moduleId":1406,"columns":4,"masterFields":[' +
                     '{"key":"A","span":1,"newLine":false,"cellGroup":"G","cellRole":1,"tabNo":1,"rowSpan":2},' +
                     '{"key":"B","span":1,"newLine":false,"cellGroup":null,"cellRole":0,"tabNo":1}]}'
            Expect = 'diff'
        }
        # ⑤ 复合格被拆 ⇒ 必须抓到
        'CELLGROUP_LOST'   = @{
            Json   = '{"moduleId":1406,"columns":4,"masterFields":[' +
                     '{"key":"A","span":1,"newLine":false,"cellGroup":null,"cellRole":0,"tabNo":1},' +
                     '{"key":"B","span":1,"newLine":false,"cellGroup":null,"cellRole":0,"tabNo":1}]}'
            Expect = 'diff'
        }
        # ⑥ 页签归属变化 ⇒ 必须抓到
        'TAB_CHANGED'      = @{
            Json   = '{"moduleId":1406,"columns":4,"masterFields":[' +
                     '{"key":"A","span":1,"newLine":false,"cellGroup":"G","cellRole":1,"tabNo":2},' +
                     '{"key":"B","span":1,"newLine":false,"cellGroup":null,"cellRole":0,"tabNo":1}]}'
            Expect = 'diff'
        }
        # ⑦ 字段顺序互换 ⇒ 必须抓到
        'ORDER_CHANGED'    = @{
            Json   = '{"moduleId":1406,"columns":4,"masterFields":[' +
                     '{"key":"B","span":1,"newLine":false,"cellGroup":null,"cellRole":0,"tabNo":1},' +
                     '{"key":"A","span":1,"newLine":false,"cellGroup":"G","cellRole":1,"tabNo":1}]}'
            Expect = 'diff'
        }
    }

    $baseText = ConvertTo-EosParityText -Json $baseline
    $bad = @()
    foreach ($name in $cases.Keys) {
        $case = $cases[$name]
        $text = ConvertTo-EosParityText -Json $case.Json
        $actual = if ($text -eq $baseText) { 'same' } else { 'diff' }
        if ($actual -ne $case.Expect) { $bad += "$name(期望 $($case.Expect)，实得 $actual)" }
    }

    if ($bad.Count -gt 0) {
        $script:SelfTestMessage = "-SelfTest 判别力不符：$($bad -join '；')"
        return $false
    }
    $script:SelfTestMessage = '-SelfTest 判别力：完全相同与「仅新增段/标志/默认值」判无差异；列跨度、行跨度、复合格、页签、字段顺序五类变化逐条命中。'
    return $true
}

if ($SelfTest) {
    if (Test-EosParityDiscrimination) {
        Write-Output "PASS $script:SelfTestMessage"
        exit 0
    }
    Write-Output "FAIL $script:SelfTestMessage"
    exit 3
}

# ---------------------------------------------------------------- 取模块清单
try {
    . (Join-Path $PSScriptRoot 'dev\eos-sql.ps1')
    $moduleLines = Invoke-EosSqlQuery -Query "SELECT CAST(m.M_IDX AS varchar(20)) FROM dbo.MODULES m WHERE LTRIM(RTRIM(ISNULL(m.M_URL,'')))='/workbench' ORDER BY m.M_IDX;"
    $moduleIds = @($moduleLines | ForEach-Object { [int]$_.Trim() } | Where-Object { $_ -gt 0 })
    if ($ModuleId) {
        $requested = @($ModuleId)
        $moduleIds = @($moduleIds | Where-Object { $requested -contains $_ })
        if ($moduleIds.Count -eq 0) {
            Write-Output "FAIL 指定的模块不在工作台模块清单内：$($requested -join ',')"
            exit 2
        }
    }
}
catch {
    Write-Output "FAIL 工作台模块清单读取失败：$($_.Exception.Message)"
    exit 2
}
if ($moduleIds.Count -eq 0) {
    Write-Output 'FAIL 工作台模块清单为空（MODULES.M_URL='/workbench'）。'
    exit 2
}

try {
    . (Join-Path $PSScriptRoot '..\EOS.API.Tests\AcceptanceCommon.ps1')
    $session = New-EosSession -ApiUrl $ApiUrl
}
catch {
    Write-Output "FAIL API 会话建立失败：$($_.Exception.Message)"
    exit 2
}

$apiVersion = 'unknown'
try {
    $version = Invoke-WebRequest -Uri "$ApiUrl/health/version" -SkipHttpErrorCheck
    $rawVersion = $version.Content
    if ($rawVersion -is [byte[]]) { $rawVersion = [Text.Encoding]::UTF8.GetString($rawVersion) }
    $apiVersion = ($rawVersion | ConvertFrom-Json).commit
}
catch { }

Write-Output "工作台模块 $($moduleIds.Count) 个；API $ApiUrl（commit=$apiVersion）"
$started = Get-Date
$snapshots = Get-EosParitySnapshot -Session $session -ApiUrl $ApiUrl -ModuleIds $moduleIds
Write-Output ("取数完成，用时 {0:N0} 秒" -f ((Get-Date) - $started).TotalSeconds)

# ---------------------------------------------------------------- 抓基线
if ($Capture) {
    $payload = [pscustomobject]@{
        capturedAt = (Get-Date).ToString('o')
        apiCommit  = $apiVersion
        moduleCount = $snapshots.Count
        modules    = $snapshots
    }
    $json = $payload | ConvertTo-Json -Depth 8 -Compress
    $directory = Split-Path -Parent $Capture
    if ($directory -and -not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    [System.IO.File]::WriteAllText($Capture, $json, [Text.UTF8Encoding]::new($false))
    Write-Output ("PASS 基线已写入 {0}（模块 {1} 个，{2:N1} MB）" -f $Capture, $snapshots.Count, ((Get-Item $Capture).Length / 1MB))
    exit 0
}

# ---------------------------------------------------------------- 比对
if (-not (Test-Path -LiteralPath $Compare)) {
    Write-Output "FAIL 基线文件不存在：$Compare"
    exit 2
}
$baseline = (Get-Content -Raw -LiteralPath $Compare) | ConvertFrom-Json -Depth 8
$baselineById = @{}
foreach ($row in $baseline.modules) { $baselineById[[int]$row.moduleId] = $row }

$differences = 0
$missing = 0
foreach ($current in $snapshots) {
    if (-not $baselineById.ContainsKey($current.moduleId)) {
        Write-Output "DIFF module $($current.moduleId) $($current.title)：基线中不存在（新增模块）"
        $differences++
        continue
    }
    $base = $baselineById[$current.moduleId]
    $diffs = @()
    if ($base.definition -ne $current.definition) { $diffs += 'definition' }
    if ($base.form -ne $current.form) { $diffs += 'form' }
    if ($base.formStatus -ne $current.formStatus) { $diffs += "formStatus($($base.formStatus)->$($current.formStatus))" }
    if ($diffs.Count -eq 0) { continue }

    $differences++
    Write-Output "DIFF module $($current.moduleId) $($current.title)：$($diffs -join ', ')"
    if ($base.definition -ne $current.definition) {
        Write-EosDiffDetail -Key 'definition' -BaselineText $base.definition -CurrentText $current.definition
    }
    if ($base.form -ne $current.form) {
        Write-EosDiffDetail -Key 'form' -BaselineText $base.form -CurrentText $current.form
    }
}
foreach ($row in $baseline.modules) {
    if (-not ($snapshots.moduleId -contains [int]$row.moduleId)) {
        Write-Output "DIFF module $($row.moduleId) $($row.title)：基线有、现有无"
        $missing++
        $differences++
    }
}

Write-Output ("modules={0} baseline={1} differences={2}" -f $snapshots.Count, @($baseline.modules).Count, $differences)
if ($differences -gt 0) {
    Write-Output "FAIL 表单定义发生回归（除新增的版式段与版式专用键 $($script:IgnoredKeys -join '/') 外必须逐字一致）。"
    exit 1
}
Write-Output 'PASS 表单定义与基线逐字一致（仅新增版式段与版式专用键）。'
exit 0
