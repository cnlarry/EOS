<#
.SYNOPSIS
    报表归属迁移的「取数结果零变化」对拍：改造前抓基线，改造后逐张比对。

.DESCRIPTION
    报表的归属锚点从承载模块（REPORT.R_M_IDX）搬到业务模块（REPORT.M_IDX）、筛选条件随行
    搬家（SYSQR_DEFAULT / SYSQR_DA / SYSQR_USER 的 M_IDX 被改指、SERIAL_NO 被重排）时，
    最容易出的错不是报错，而是**同一张报表悄悄换了一份数据**：条件挂错了模块、序号重排后
    与前端的筛选项错位、取数表跟着宿主变了——接口照样 200，列还有、行还有，只是内容不是原来那份。

    故本脚本是这条改造链**唯一可执行的判据**：
      - `-Capture <file>`：对全部报表取"列清单 + 首页行集"落盘为基线；
      - `-Compare <file>`：重新取一遍并逐张比对，任何一处变化即 exit 1。

    取数**走真实 API**（`GET /api/v1/reports/{moduleId}/definition` 与
    `POST /api/v1/reports/{moduleId}/query`），与用户实际路径同源——自己去拼 SQL 会另造一套口径，
    口径不一致时"对拍通过"反而是假证据。

    比对粒度具名到 (moduleId, reportId)，差异分四类（本脚本另加第五类以覆盖数据源切换，
    前四类即需求口径）：
      - 列集合变化（列清单本身）；
      - 行数变化（首页行数或 total 变化）；
      - 行内容变化（行内容规范化哈希变化）；
      - 取数失败（definition 或 query 非 200）；
      - 数据源变化（table ⇄ aggregate）。

    比对时的 moduleId **取自基线文件**而非当场重读库：改造过程里宿主模块号会被改，
    重新读库会把"迁移改了归属"误判成"抓不到该报表"。库内新增的报表按"基线中不存在"单独列出，
    不计入失败。

    `-Compare` 前请先确认 API 已重启到包含本次改动的构建——否则比的是同一个旧进程，等于什么都没比
    （"基线一致"会变成假通过）。

.PARAMETER ApiUrl
    运行中的 API 地址。

.PARAMETER Capture
    抓基线：把全部报表的列清单与首页行集写入该文件（改造前执行，越早越好）。

.PARAMETER Compare
    比对：重新抓取并与该基线文件比对，有差异即 exit 1。

.PARAMETER SelfTest
    判别力自检：把同一套比对逻辑用在合成样本上——完全一致必须判无差异；
    人为改一行、人为删一列必须**恰好点名那一张**（不连库、不连 API）。

.PARAMETER PageSize
    每张报表抓取的行数（首页）。默认 50，保持对拍成本可控。

.PARAMETER ModuleId
    只处理指定宿主模块（试点/排障用；不指定即 REPORT 全表）。

.PARAMETER UseAttribution
    按报表**当前的归属模块**（`REPORT.M_IDX`）取数，而不是基线里记录的模块号。
    报表归属从承载页搬到业务模块后，端点参数的含义随之改变：老地址会集体 404，
    但"同一张报表的取数结果有没有变"仍然要问——用本开关把每张报表重新按新归属寻址，
    差异里剩下的才是真正的回归（其余会表现为"新可达"）。

.PARAMETER TimeoutSec
    整体超时（默认 1800 秒）。

.EXAMPLE
    pwsh scripts/compare-report-rowset-parity.ps1 -Capture logs\report-rowset-baseline.json
    pwsh scripts/compare-report-rowset-parity.ps1 -Compare logs\report-rowset-baseline.json
    pwsh scripts/compare-report-rowset-parity.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [string] $ApiUrl = 'http://localhost:5261',
    [string] $Capture,
    [string] $Compare,
    [int] $PageSize = 50,
    [int[]] $ModuleId,
    [int] $TimeoutSec = 1800,
    [switch] $UseAttribution,
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

function Get-EosRowsetSignature {
    <#
     列清单 + 行集的规范化签名。行内取值按**声明列顺序**展开、以不可见分隔符连接，
     再对整段做 SHA256——顺序参与签名（列顺序变了就是渲染顺序变了）。
     不做 Trim：nchar 的尾空格本身就是取数口径的一部分，抹掉就等于放行"结尾被截断"这类回归。
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $Columns,
        [Parameter(Mandatory)][AllowEmptyCollection()][AllowNull()] $Rows
    )

    $keys = @($Columns)
    $builder = [System.Text.StringBuilder]::new()
    foreach ($row in @($Rows)) {
        if ($null -eq $row) { continue }
        foreach ($key in $keys) {
            $value = $null
            if ($row -is [System.Management.Automation.PSCustomObject]) {
                $property = $row.PSObject.Properties[$key]
                if ($property) { $value = $property.Value }
            }
            elseif ($row -is [System.Collections.IDictionary]) {
                if ($row.Contains($key)) { $value = $row[$key] }
            }
            [void]$builder.Append([System.Convert]::ToString($value, [System.Globalization.CultureInfo]::InvariantCulture))
            [void]$builder.Append([char]0x1F)
        }
        [void]$builder.Append([char]0x1E)
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($builder.ToString())
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return [System.Convert]::ToHexString($hash).ToLowerInvariant()
}

function Get-EosShortHash {
    <# 哈希摘要前缀（诊断信息用；空值不炸）。 #>
    param([AllowNull()][string] $Hash)
    if ([string]::IsNullOrEmpty($Hash)) { return '<空>' }
    return $Hash.Substring(0, [Math]::Min(12, $Hash.Length))
}

function Get-EosRowsetDiff {
    <#
     逐张比对基线与现值，返回差异描述数组。每条含 Key / ModuleId / ReportId / Kinds / Detail。
     key 为 "moduleId|reportId"，两侧都用它对齐——不靠数组下标（顺序无关）。
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()] $Baseline,
        [Parameter(Mandatory)][AllowEmptyCollection()] $Current
    )

    $currentByKey = @{}
    foreach ($item in $Current) { $currentByKey["$($item.moduleId)|$($item.reportId)"] = $item }

    $diffs = @()
    foreach ($base in $Baseline) {
        $key = "$($base.moduleId)|$($base.reportId)"
        if (-not $currentByKey.ContainsKey($key)) {
            $diffs += [pscustomobject]@{
                Key = $key; ModuleId = $base.moduleId; ReportId = $base.reportId
                Kinds = '取数失败'; Detail = '基线有、现有无'
            }
            continue
        }
        $now = $currentByKey[$key]

        $baseOk = ($base.definitionStatus -eq 200) -and ($base.queryStatus -eq 200)
        $nowOk = ($now.definitionStatus -eq 200) -and ($now.queryStatus -eq 200)
        $kinds = @()
        $details = @()

        # 本来取不到数、现在取到了：不是回归（部分报表在旧判据下恒 404，改造后本就会变可达）。
        # 但也不能不报——它意味着这张报表此前从未被对拍覆盖过，登记出来免得被当成"比过了"。
        if (-not $baseOk -and $nowOk) {
            $diffs += [pscustomobject]@{
                Key = $key; ModuleId = $base.moduleId; ReportId = $base.reportId
                Kinds = '新可达'; Detail = ("状态 {0}/{1} -> {2}/{3}" -f $base.definitionStatus, $base.queryStatus, $now.definitionStatus, $now.queryStatus)
            }
            continue
        }

        if ($baseOk -and -not $nowOk) {
            $kinds += '取数失败'
            $details += ("状态 {0}/{1} -> {2}/{3} {4}" -f $base.definitionStatus, $base.queryStatus, $now.definitionStatus, $now.queryStatus, $now.error)
        }
        elseif (-not $nowOk) {
            # 两侧都取不到数：不算回归，但状态码本身变了要报（例如 404 变 403，说明权限门换了位置）
            if (($base.definitionStatus -ne $now.definitionStatus) -or ($base.queryStatus -ne $now.queryStatus)) {
                $kinds += '取数失败'
                $details += ("状态 {0}/{1} -> {2}/{3} {4}" -f $base.definitionStatus, $base.queryStatus, $now.definitionStatus, $now.queryStatus, $now.error)
            }
        }

        if ($nowOk -and $baseOk) {
            $baseColumns = @($base.columns)
            $nowColumns = @($now.columns)
            if (($baseColumns -join "`u{1}") -ne ($nowColumns -join "`u{1}")) {
                $kinds += '列集合变化'
                $details += ("列 {0} -> {1}" -f ($baseColumns -join ','), ($nowColumns -join ','))
            }
            if ($base.dataSource -ne $now.dataSource) {
                $kinds += '数据源变化'
                $details += ("数据源 {0} -> {1}" -f $base.dataSource, $now.dataSource)
            }
            if (($base.rowCount -ne $now.rowCount) -or ($base.total -ne $now.total)) {
                $kinds += '行数变化'
                $details += ("行数 {0}/{1} -> {2}/{3}" -f $base.rowCount, $base.total, $now.rowCount, $now.total)
            }
            if ($base.hash -ne $now.hash) {
                $kinds += '行内容变化'
                $details += ("内容哈希 {0} -> {1}" -f (Get-EosShortHash $base.hash), (Get-EosShortHash $now.hash))
            }
        }

        if ($kinds.Count -gt 0) {
            $diffs += [pscustomobject]@{
                Key = $key; ModuleId = $base.moduleId; ReportId = $base.reportId
                Kinds = ($kinds -join '+'); Detail = ($details -join '；')
            }
        }
    }
    return $diffs
}

# ---------------------------------------------------------------- 判别力自检
function Test-EosRowsetDiscrimination {
    $baseline = @(
        [pscustomobject]@{ moduleId = 1401; reportId = 'COP_ORDER_1'; definitionStatus = 200; queryStatus = 200
            dataSource = 'table'; columns = @('ORDER_NO', 'CLIENT_ID', 'QTY'); rowCount = 2; total = 2; hash = 'aaa'; error = '' },
        [pscustomobject]@{ moduleId = 1401; reportId = 'COP_ORDER_2'; definitionStatus = 200; queryStatus = 200
            dataSource = 'table'; columns = @('ORDER_NO', 'QTY'); rowCount = 1; total = 1; hash = 'bbb'; error = '' }
    )
    $identical = @(
        [pscustomobject]@{ moduleId = 1401; reportId = 'COP_ORDER_1'; definitionStatus = 200; queryStatus = 200
            dataSource = 'table'; columns = @('ORDER_NO', 'CLIENT_ID', 'QTY'); rowCount = 2; total = 2; hash = 'aaa'; error = '' },
        [pscustomobject]@{ moduleId = 1401; reportId = 'COP_ORDER_2'; definitionStatus = 200; queryStatus = 200
            dataSource = 'table'; columns = @('ORDER_NO', 'QTY'); rowCount = 1; total = 1; hash = 'bbb'; error = '' }
    )
    # 第一张的一行内容变了（行数不变）
    $rowChanged = @(
        [pscustomobject]@{ moduleId = 1401; reportId = 'COP_ORDER_1'; definitionStatus = 200; queryStatus = 200
            dataSource = 'table'; columns = @('ORDER_NO', 'CLIENT_ID', 'QTY'); rowCount = 2; total = 2; hash = 'zzz'; error = '' },
        [pscustomobject]@{ moduleId = 1401; reportId = 'COP_ORDER_2'; definitionStatus = 200; queryStatus = 200
            dataSource = 'table'; columns = @('ORDER_NO', 'QTY'); rowCount = 1; total = 1; hash = 'bbb'; error = '' }
    )
    # 第二张少了一列
    $columnRemoved = @(
        [pscustomobject]@{ moduleId = 1401; reportId = 'COP_ORDER_1'; definitionStatus = 200; queryStatus = 200
            dataSource = 'table'; columns = @('ORDER_NO', 'CLIENT_ID', 'QTY'); rowCount = 2; total = 2; hash = 'aaa'; error = '' },
        [pscustomobject]@{ moduleId = 1401; reportId = 'COP_ORDER_2'; definitionStatus = 200; queryStatus = 200
            dataSource = 'table'; columns = @('ORDER_NO'); rowCount = 1; total = 1; hash = 'bbb'; error = '' }
    )
    # 第二张取数失败
    $fetchFailed = @(
        [pscustomobject]@{ moduleId = 1401; reportId = 'COP_ORDER_1'; definitionStatus = 200; queryStatus = 200
            dataSource = 'table'; columns = @('ORDER_NO', 'CLIENT_ID', 'QTY'); rowCount = 2; total = 2; hash = 'aaa'; error = '' },
        [pscustomobject]@{ moduleId = 1401; reportId = 'COP_ORDER_2'; definitionStatus = 404; queryStatus = 404
            dataSource = ''; columns = @(); rowCount = 0; total = 0; hash = ''; error = 'not found' }
    )

    $bad = @()

    $same = @(Get-EosRowsetDiff -Baseline $baseline -Current $identical)
    if ($same.Count -ne 0) { $bad += "完全一致本应判无差异，实得 $($same.Count) 条" }

    $one = @(Get-EosRowsetDiff -Baseline $baseline -Current $rowChanged)
    if ($one.Count -ne 1 -or $one[0].ReportId -ne 'COP_ORDER_1' -or $one[0].Kinds -ne '行内容变化') {
        $bad += "改一行应恰好点名 COP_ORDER_1 的行内容变化，实得 $($one.Count) 条：$(($one | ForEach-Object { "$($_.ReportId)/$($_.Kinds)" }) -join ';')"
    }

    $col = @(Get-EosRowsetDiff -Baseline $baseline -Current $columnRemoved)
    if ($col.Count -ne 1 -or $col[0].ReportId -ne 'COP_ORDER_2' -or $col[0].Kinds -ne '列集合变化') {
        $bad += "删一列应恰好点名 COP_ORDER_2 的列集合变化，实得 $($col.Count) 条：$(($col | ForEach-Object { "$($_.ReportId)/$($_.Kinds)" }) -join ';')"
    }

    $fail = @(Get-EosRowsetDiff -Baseline $baseline -Current $fetchFailed)
    if ($fail.Count -ne 1 -or $fail[0].ReportId -ne 'COP_ORDER_2' -or $fail[0].Kinds -ne '取数失败') {
        $bad += "取数失败应恰好点名 COP_ORDER_2，实得 $($fail.Count) 条：$(($fail | ForEach-Object { "$($_.ReportId)/$($_.Kinds)" }) -join ';')"
    }

    # 旧判据下恒 404、改造后可达：算"新可达"（信息项），不得当成回归拦下来
    $unreachableBaseline = @(
        [pscustomobject]@{ moduleId = 1401; reportId = 'HR_ABSENT_List'; definitionStatus = 404; queryStatus = 0
            dataSource = ''; columns = @(); rowCount = 0; total = 0; hash = ''; error = 'definition HTTP 404' }
    )
    $nowReachable = @(
        [pscustomobject]@{ moduleId = 1401; reportId = 'HR_ABSENT_List'; definitionStatus = 200; queryStatus = 200
            dataSource = 'table'; columns = @('EMP_ID'); rowCount = 1; total = 1; hash = 'ccc'; error = '' }
    )
    $newly = @(Get-EosRowsetDiff -Baseline $unreachableBaseline -Current $nowReachable)
    if ($newly.Count -ne 1 -or $newly[0].Kinds -ne '新可达') {
        $bad += "由 404 转可达应判新可达，实得 $($newly.Count) 条：$(($newly | ForEach-Object { $_.Kinds }) -join ';')"
    }

    # 两侧都取不到数、状态码也没变：不算差异（否则每跑一次都会把历史失败重复报一遍）
    $stillFailing = @(Get-EosRowsetDiff -Baseline $unreachableBaseline -Current $unreachableBaseline)
    if ($stillFailing.Count -ne 0) { $bad += "两侧同为 404 本应判无差异，实得 $($stillFailing.Count) 条" }

    # 哈希函数本身要有判别力：改一个字符必须变，换个列顺序也必须变
    $h1 = Get-EosRowsetSignature -Columns @('A') -Rows @([pscustomobject]@{ A = '1' })
    $h2 = Get-EosRowsetSignature -Columns @('A') -Rows @([pscustomobject]@{ A = '2' })
    $h3 = Get-EosRowsetSignature -Columns @('A') -Rows @([pscustomobject]@{ A = '1' })
    if ($h1 -eq $h2) { $bad += '哈希对取值不敏感（1 与 2 同哈希）' }
    if ($h1 -ne $h3) { $bad += '哈希对相同输入不稳定' }
    $h4 = Get-EosRowsetSignature -Columns @('A', 'B') -Rows @([pscustomobject]@{ A = '1'; B = '2' })
    $h5 = Get-EosRowsetSignature -Columns @('B', 'A') -Rows @([pscustomobject]@{ A = '1'; B = '2' })
    if ($h4 -eq $h5) { $bad += '哈希对列顺序不敏感' }

    if ($bad.Count -gt 0) {
        $script:SelfTestMessage = "-SelfTest 判别力不符：$($bad -join '；')"
        return $false
    }
    $script:SelfTestMessage = '-SelfTest 判别力：一致判无差异；改一行、删一列、取数失败各自恰好点名对应报表；404 转可达判为新可达、两侧同为 404 判无差异；哈希对取值与列顺序均敏感。'
    return $true
}

if ($SelfTest) {
    if (Test-EosRowsetDiscrimination) {
        Write-Output "PASS $script:SelfTestMessage"
        exit 0
    }
    Write-Output "FAIL $script:SelfTestMessage"
    exit 3
}

$deadline = (Get-Date).AddSeconds($TimeoutSec)

function Get-EosCurrentAttribution {
    <# 报表编号 → 当前归属模块（REPORT.M_IDX）。只读。 #>
    . (Join-Path $PSScriptRoot 'dev\eos-sql.ps1')
    $rows = @(Invoke-EosSqlQuery -Query @'
SET NOCOUNT ON;
SELECT CONCAT(LTRIM(RTRIM(r.REPORT_ID)), '|', CAST(r.M_IDX AS varchar(20))) FROM dbo.REPORT r WITH (NOLOCK);
'@ | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $map = @{}
    foreach ($line in $rows) {
        $parts = $line -split '\|'
        if ($parts.Count -lt 2) { continue }
        $map[$parts[0].Trim()] = [int]$parts[1].Trim()
    }
    return $map
}

function Set-EosAttributedModuleId {
    <# 按当前归属重写每张报表的 moduleId；库内已不存在的报表保持原值（会在比对时报"基线有、现有无"）。 #>
    param([AllowEmptyCollection()] $Items, [Parameter(Mandatory)] $Attribution)
    foreach ($item in $Items) {
        if ($Attribution.ContainsKey($item.ReportId)) { $item.ModuleId = $Attribution[$item.ReportId] }
    }
    return $Items
}

function Get-EosReportInventory {
    <# 报表清单：REPORT 全表，按**归属模块**（REPORT.M_IDX，端点参数就是它）分组。只读。 #>
    param([int[]] $ModuleIds)

    . (Join-Path $PSScriptRoot 'dev\eos-sql.ps1')
    $filter = ''
    if ($ModuleIds) { $filter = " AND r.M_IDX IN ($($ModuleIds -join ','))" }
    $query = @"
SET NOCOUNT ON;
SELECT CONCAT(LTRIM(RTRIM(r.REPORT_ID)), '|', CAST(r.M_IDX AS varchar(20)), '|', LTRIM(RTRIM(ISNULL(r.REPORT_NAME, ''))))
FROM dbo.REPORT r WITH (NOLOCK)
WHERE 1 = 1$filter
ORDER BY r.M_IDX, r.REPORT_ID;
"@
    $rows = Invoke-EosSqlQuery -Query $query
    $items = @()
    foreach ($line in $rows) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parts = $line -split '\|'
        if ($parts.Count -lt 3) { continue }
        $items += [pscustomobject]@{
            ReportId = $parts[0].Trim()
            ModuleId = [int]$parts[1].Trim()
            ReportName = $parts[2].Trim()
        }
    }
    return $items
}

function Get-EosRowsetSnapshot {
    <# 逐张报表取"定义 + 首页行集"，返回签名快照数组。 #>
    param(
        [Parameter(Mandatory)] $Session,
        [Parameter(Mandatory)] [string] $ApiUrl,
        [Parameter(Mandatory)] [AllowEmptyCollection()] $Items,
        [int] $PageSize = 50
    )

    $snapshots = @()
    $index = 0
    foreach ($item in $Items) {
        $index++
        if ($index % 20 -eq 1) {
            Write-Information -MessageData ("  取数进度 {0}/{1}" -f $index, $Items.Count) -InformationAction Continue
        }
        if ((Get-Date) -gt $deadline) {
            throw "整体超时（$TimeoutSec 秒），已处理 $index/$($Items.Count) 张。"
        }

        $encoded = [uri]::EscapeDataString($item.ReportId)
        $definitionStatus = 0
        $queryStatus = 0
        $dataSource = ''
        $columns = @()
        $rowCount = 0
        $total = 0
        $hash = ''
        $error = ''

        try {
            $definition = Invoke-EosApi -Method 'GET' `
                -Path "/api/v1/reports/$($item.ModuleId)/definition?reportId=$encoded" `
                -Session $Session -ApiUrl $ApiUrl
            $definitionStatus = $definition.Status
            if ($definition.Status -eq 200 -and $definition.Content) {
                $dataSource = [string]$definition.Content.dataSource
                $columns = @($definition.Content.columns | ForEach-Object { [string]$_.key })
            }
            else {
                $error = "definition HTTP $($definition.Status)"
            }
        }
        catch {
            $definitionStatus = -1
            $error = "definition 异常：$($_.Exception.Message)"
        }

        if ($definitionStatus -eq 200) {
            try {
                $query = Invoke-EosApi -Method 'POST' `
                    -Path "/api/v1/reports/$($item.ModuleId)/query?page=1&pageSize=$PageSize&reportId=$encoded" `
                    -Body @{ values = @{}; valuesTo = @{} } -Session $Session -ApiUrl $ApiUrl
                $queryStatus = $query.Status
                if ($query.Status -eq 200 -and $query.Content) {
                    $rows = @($query.Content.rows)
                    $rowCount = $rows.Count
                    $total = [int]$query.Content.total
                    $hash = Get-EosRowsetSignature -Columns $columns -Rows $rows
                }
                else {
                    $error = "$error query HTTP $($query.Status)"
                }
            }
            catch {
                $queryStatus = -1
                $error = "$error query 异常：$($_.Exception.Message)"
            }
        }

        $snapshots += [pscustomobject]@{
            moduleId         = $item.ModuleId
            reportId         = $item.ReportId
            reportName       = $item.ReportName
            definitionStatus = $definitionStatus
            queryStatus      = $queryStatus
            dataSource       = $dataSource
            columns          = $columns
            rowCount         = $rowCount
            total            = $total
            hash             = $hash
            error            = $error.Trim()
        }
    }
    return $snapshots
}

# ---------------------------------------------------------------- 会话与版本
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

# ---------------------------------------------------------------- 抓基线
if ($Capture) {
    try {
        $items = Get-EosReportInventory -ModuleIds $ModuleId
    }
    catch {
        Write-Output "FAIL 报表清单读取失败：$($_.Exception.Message)"
        exit 2
    }
    if (@($items).Count -eq 0) {
        Write-Output 'FAIL 报表清单为空。'
        exit 2
    }
    if ($UseAttribution) {
        $items = Set-EosAttributedModuleId -Items $items -Attribution (Get-EosCurrentAttribution)
        Write-Output '按当前归属模块取数（-UseAttribution）'
    }

    Write-Output "报表 $($items.Count) 张；API $ApiUrl（commit=$apiVersion）"
    $started = Get-Date
    $snapshots = Get-EosRowsetSnapshot -Session $session -ApiUrl $ApiUrl -Items $items -PageSize $PageSize
    Write-Output ("取数完成，用时 {0:N0} 秒" -f ((Get-Date) - $started).TotalSeconds)

    $payload = [pscustomobject]@{
        capturedAt = (Get-Date).ToString('o')
        apiCommit  = $apiVersion
        pageSize   = $PageSize
        reportCount = @($snapshots).Count
        reports    = $snapshots
    }
    $json = $payload | ConvertTo-Json -Depth 8 -Compress
    $directory = Split-Path -Parent $Capture
    if ($directory -and -not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    [System.IO.File]::WriteAllText($Capture, $json, [Text.UTF8Encoding]::new($false))

    $failed = @($snapshots | Where-Object { $_.definitionStatus -ne 200 -or $_.queryStatus -ne 200 })
    $aggregate = @($snapshots | Where-Object { $_.dataSource -eq 'aggregate' })
    Write-Output ("基线已写入 {0}（报表 {1} 张，{2:N1} MB；agg 型 {3} 张；取数失败 {4} 张）" -f `
        $Capture, @($snapshots).Count, ((Get-Item $Capture).Length / 1MB), $aggregate.Count, $failed.Count)
    foreach ($item in $failed) {
        Write-Output ("  取数失败 module={0} report={1} {2}" -f $item.moduleId, $item.reportId, $item.error)
    }
    exit 0
}

# ---------------------------------------------------------------- 比对
if (-not (Test-Path -LiteralPath $Compare)) {
    Write-Output "FAIL 基线文件不存在：$Compare"
    exit 2
}
$baseline = (Get-Content -Raw -LiteralPath $Compare) | ConvertFrom-Json -Depth 8
# 直接沿用基线行（它们带全套字段：状态、列清单、行数、哈希），只按需改写模块号。
# 另起一个只有三个字段的对象会让比对拿不到任何可比字段——那等于把"比过了"变成"没比"。
$baselineItems = @($baseline.reports)
if ($UseAttribution) {
    $attribution = Get-EosCurrentAttribution
    $rewritten = 0
    foreach ($item in $baselineItems) {
        $key = [string]$item.reportId
        if ($attribution.ContainsKey($key) -and [int]$item.moduleId -ne $attribution[$key]) {
            $item.moduleId = $attribution[$key]
            $rewritten++
        }
    }
    Write-Output "按当前归属模块取数（-UseAttribution）：改写 $rewritten 张报表的模块号"
}

Write-Output "基线 {$($baseline.capturedAt)} 报表 $($baselineItems.Count) 张；现 API $ApiUrl（commit=$apiVersion）"
if ($baseline.apiCommit -and $apiVersion -and $baseline.apiCommit -eq $apiVersion) {
    Write-Output "WARN 现行 API commit 与基线相同（$apiVersion）——若本次改动尚未重启生效，比对结果不具判别力。"
}

$started = Get-Date
$snapshots = Get-EosRowsetSnapshot -Session $session -ApiUrl $ApiUrl -Items $baselineItems -PageSize $PageSize
Write-Output ("取数完成，用时 {0:N0} 秒" -f ((Get-Date) - $started).TotalSeconds)

$allDiffs = @(Get-EosRowsetDiff -Baseline $baselineItems -Current $snapshots)
$diffs = @($allDiffs | Where-Object { $_.Kinds -ne '新可达' })
$newlyReachable = @($allDiffs | Where-Object { $_.Kinds -eq '新可达' })
foreach ($item in $diffs) {
    Write-Output ("DIFF module={0} report={1} [{2}] {3}" -f $item.ModuleId, $item.ReportId, $item.Kinds, $item.Detail)
}
foreach ($item in $newlyReachable) {
    Write-Output ("INFO 新可达 module={0} report={1} {2}（此前从未参与对拍）" -f $item.ModuleId, $item.ReportId, $item.Detail)
}

# 库内新增的报表：单独列出，不计入失败（对拍的口径是"原来那些没变"）
try {
    $currentItems = Get-EosReportInventory -ModuleIds $ModuleId
    $baselineKeys = @{}
    foreach ($row in $baselineItems) { $baselineKeys["$($row.moduleId)|$($row.reportId)"] = $true }
    $added = @($currentItems | Where-Object { -not $baselineKeys.ContainsKey("$($_.ModuleId)|$($_.ReportId)") })
    if ($added.Count -gt 0) {
        Write-Output ("INFO 库内新增报表 {0} 张（未参与比对）：{1}" -f $added.Count, (($added | ForEach-Object { "$($_.ModuleId)/$($_.ReportId)" }) -join ', '))
    }
}
catch { }

$currentFailed = @($snapshots | Where-Object { $_.definitionStatus -ne 200 -or $_.queryStatus -ne 200 })
Write-Output ("reports={0} failed={1} differences={2} newlyReachable={3}" -f @($snapshots).Count, $currentFailed.Count, $diffs.Count, $newlyReachable.Count)
foreach ($item in $currentFailed) {
    Write-Output ("  取数失败 module={0} report={1} {2}" -f $item.moduleId, $item.reportId, $item.error)
}

if ($diffs.Count -gt 0) {
    Write-Output 'FAIL 报表取数结果与基线不一致（列集合、行数、行内容、数据源、取数状态任一变化都算回归）。'
    exit 1
}
Write-Output 'PASS 全部报表的列清单与首页行集与基线逐张一致。'
exit 0
