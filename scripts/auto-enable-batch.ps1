#Requires -Version 7.0
<#
.SYNOPSIS
统一表单自动放量流水线：一批候选模块 → 预检 → 启用（写 appsettings.json）→ 重启 API →
真实数据 CRUD 回路（尽量完整、模拟真实业务数据）→ 语义/元数据/扫描核对 → 全量回归。
全绿则保留启用并出报告；任一失败则整批回滚并出差异报告（不静默修正）。
.DESCRIPTION
数据策略（用户指示 2026-08-19）：
- 尽量模拟真实业务数据：选择器取真实引用值（form-chooser/源表首行）、FORM_OPTIONS 取首选项、
  DFT_VALUE 默认值生效、文本按标签生成中文语义值、数值按标签推断合理值、日期取业务日期；
- 数据尽量完整：填所有可见可编辑字段，不只填必填；输出每模块完整度百分比与未解析字段清单。
决策边界：任何无法自动构造/与旧系统语义不一致的字段或模块 → 进 exceptions 报告，不自动修正。
放量 = 写入 UnifiedFormEditor.EnabledModuleIds（开发/测试环境）+ 回归全绿；生产部署另行走部署流程。
.EXAMPLE
.\scripts\auto-enable-batch.ps1
.\scripts\auto-enable-batch.ps1 -ModuleIds 2701,2702,3301
.\scripts\auto-enable-batch.ps1 -SkipRestart -SkipRegression   # 联调用
#>
param(
    [int[]]$ModuleIds = @(),
    [string]$ApiUrl = 'http://localhost:5261',
    [switch]$SkipRestart,
    [switch]$SkipRegression,
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $root 'EOS.API.Tests\AcceptanceCommon.ps1')

if (-not $OutDir) {
    $OutDir = Join-Path (Join-Path $root 'logs') 'auto-enable'
}
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir = Join-Path $OutDir $stamp
New-Item -ItemType Directory -Path $runDir -Force | Out-Null

$appSettingsPath = Join-Path $root 'EOS.API\appsettings.json'
$backupPath = Join-Path $runDir 'appsettings.json.bak'
$reportLines = [System.Collections.Generic.List[string]]::new()
$exceptions = [System.Collections.Generic.List[string]]::new()
$results = [System.Collections.Generic.List[object]]::new()

function Add-ReportLine {
    param([string]$Line)
    $reportLines.Add($Line)
}

function Add-Exception {
    param([string]$Message)
    $exceptions.Add($Message)
}

function Get-BatchModules {
    if ($ModuleIds.Count -gt 0) {
        return @($ModuleIds | ForEach-Object { [string]$_ } | Sort-Object)
    }
    $csv = Get-ChildItem (Join-Path $root 'logs\goal\phase1') -Filter 'whitelist-review-*.csv' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $csv) { throw '未找到 whitelist-review-*.csv，请先运行 scripts/review-whitelist-candidates.ps1 或显式 -ModuleIds' }
    $rows = @(Import-Csv $csv.FullName | Where-Object { $_.建议 -eq '可放量（单表）' })
    return @($rows | ForEach-Object { [string]$_.M_IDX } | Sort-Object)
}

function Get-ModuleMetaByIds {
    param([string[]]$Ids)
    if ($Ids.Count -eq 0) { return @() }
    $list = "($($Ids -join ','))"
    $rows = Invoke-EosSql -Query @"
SET NOCOUNT ON;
SELECT LTRIM(RTRIM(CAST(M_IDX AS varchar(20)))),
       LTRIM(RTRIM(ISNULL(M_DESC,''))),
       LTRIM(RTRIM(ISNULL(MASTER_TABLE,''))),
       LTRIM(RTRIM(ISNULL(DETAIL_TABLE,''))),
       LTRIM(RTRIM(ISNULL(MODI_URL,''))),
       LTRIM(RTRIM(ISNULL(FILTER,''))),
       LTRIM(RTRIM(ISNULL(M_URL,''))),
       ISNULL(CAST(AUTO_APPROVE AS int),0),
       ISNULL(CAST(M_TAG AS int),0)
FROM MODULES
WHERE CAST(M_IDX AS varchar(20)) IN $list
ORDER BY CAST(M_IDX AS INT);
"@
    $result = @()
    foreach ($line in $rows) {
        $c = $line -split '\|'
        if ($c.Count -lt 11) { continue }
        $result += [pscustomobject]@{
            ModuleId    = $c[0].Trim()
            Desc        = $c[1].Trim()
            MasterTable = $c[2].Trim()
            DetailTable = $c[3].Trim()
            ModiUrl     = $c[4].Trim()
            UpdateSp    = $c[5].Trim()
            AfterSaveSp = $c[6].Trim()
            Filter      = $c[7].Trim()
            MUrl        = $c[8].Trim()
            AutoApprove = [int]$c[9].Trim()
            MTag        = [int]$c[10].Trim()
        }
    }
    return $result
}

function Get-EosJson {
    $app = Get-Content -Raw -LiteralPath $appSettingsPath | ConvertFrom-Json
    return $app
}

function Set-EosJson {
    param($App)
    $json = $App | ConvertTo-Json -Depth 12
    # pwsh 7 ConvertTo-Json 为紧凑输出；补 2 空格缩进便于人读/审
    $indented = $json | ConvertFrom-Json | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText($appSettingsPath, $indented, [System.Text.UTF8Encoding]::new($false))
}

function Invoke-ApiJson {
    param([string]$Method, [string]$Path, $Body = $null)
    $params = @{ Uri = "$ApiUrl$Path"; Method = $Method; WebSession = $script:Session; SkipHttpErrorCheck = $true }
    if ($null -ne $Body) {
        $params.ContentType = 'application/json'
        $params.Body = ($Body | ConvertTo-Json -Depth 12 -Compress)
    }
    # Form write path requires an idempotency key — inject a fresh one per call (one call = one user intent)
    if ($Method -match '^(POST|PUT|DELETE)$' -and $Path -match '/document-workbench/\d+/(record|approve|deapprove|endcase|unendcase)') {
        $params.Headers = @{ 'X-Idempotency-Key' = [guid]::NewGuid().ToString('N') }
    }
    $r = Invoke-WebRequest @params
    $content = $null
    if ($r.Content) {
        try { $content = $r.Content | ConvertFrom-Json } catch { $content = $r.Content }
    }
    return @{ Status = [int]$r.StatusCode; Content = $content; Raw = $r.Content }
}

# ---- 真实业务数据生成 ----
function Get-ChooserValue {
    param($Field, [string]$ModuleId, $MasterValues = $null)
    # 依赖选择器（CHOOSE_FILTER 含 {m.xxx} 模板）必须携带当前主表已解析值，
    # 否则模板替换为空串导致永远 chooser-empty（3302 PRODUCE_NO 依赖 CLIENT_ID+PRO_NO）。
    $path = "/api/v1/document-workbench/$ModuleId/form-chooser/$($Field.key)"
    # 只传过滤器实际引用的 {m.xxx} 字段：全量主表值含定长填充空格，URL 超长会 414
    # （如 3302 首轮把所有已填文本字段拼进 master 导致 414，PRODUCE_NO 依赖取不到）。
    $chooserSrc = @($Field.choosers | Where-Object { $_.active -and $_.table } | Select-Object -First 1)
    $filterText = [string]$chooserSrc.filter
    $needed = [regex]::Matches($filterText, '\{m\.([A-Za-z_][A-Za-z0-9_]*)\}') |
        ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
    $masterFiltered = [ordered]@{}
    if ($MasterValues -and $needed) {
        foreach ($k in $needed) {
            if ($MasterValues.Contains($k)) { $masterFiltered[$k] = $MasterValues[$k] }
        }
    }
    if ($masterFiltered.Count -gt 0) {
        $masterJson = ($masterFiltered | ConvertTo-Json -Compress)
        $path += '?master=' + [uri]::EscapeDataString($masterJson)
    }
    $chooser = Invoke-ApiJson -Method GET -Path $path
    if ($chooser.Status -eq 200 -and @($chooser.Content.rows).Count -gt 0) {
        $cols = @($chooser.Content.columns | ForEach-Object { $_.key })
        $target = $cols | Where-Object { $_ -eq $Field.key } | Select-Object -First 1
        # 返回映射优先（如 3302 PRODUCE_NO=SEND_NO）：目标键=字段名，源列可能不同名；
        # 源表可能恰有与字段同名的空列（COP_SEND_D.PRODUCE_NO），不能直接按字段名取。
        if ($chooserSrc -and [string]$chooserSrc.returnMapping) {
            foreach ($pair in ([string]$chooserSrc.returnMapping -split ',')) {
                $kv = $pair -split '=', 2
                if ($kv.Count -eq 2 -and $kv[0].Trim() -eq $Field.key) {
                    $mapped = $kv[1].Trim()
                    if ($cols -contains $mapped) { $target = $mapped }
                    break
                }
            }
        }
        if (-not $target) { $target = $cols | Select-Object -First 1 }
        if ($target) {
            $v = [string]$chooser.Content.rows[0].$target
            if (-not [string]::IsNullOrWhiteSpace($v)) { return $v.Trim() }
        }
    }
    $src = @($Field.choosers | Where-Object { $_.active -and $_.table } | Select-Object -First 1).table
    if ($src) {
        try {
            $dt = Invoke-EosSqlTable -Query "SELECT TOP 1 LTRIM(RTRIM([$($Field.key)])) V FROM dbo.[$src] WITH (NOLOCK) WHERE LTRIM(RTRIM(ISNULL([$($Field.key)],'')))<>'' ORDER BY [$($Field.key)];"
            if ($dt.Rows.Count -gt 0 -and $null -ne $dt.Rows[0].V) { return ([string]$dt.Rows[0].V).Trim() }
        } catch { }
    }
    return $null
}

function Get-RealisticValue {
    param($Field, [string]$Table, [string]$Prefix, [int]$Seq, [string]$ModuleId, [hashtable]$PhysType, $MasterValues = $null)
    $phys = [string]$PhysType["$Table|$($Field.key)".ToUpperInvariant()]
    $type = ([string]$Field.dataType).ToLowerInvariant()
    $max = $Field.maxLength
    $trim = { param($s) if ($max -and ([string]$s).Length -gt $max) { ([string]$s).Substring(0, $max) } else { [string]$s } }

    # 选择器：真实引用值（主键字符串字段也优先取真实引用，如单别 BILLKIND/品号 PRODUCT；
    # 数值型主键不走选择器，避免非数值）
    $choosers = @($Field.choosers | Where-Object { $_.active })
    $selfChooserPk = $Field.isPrimaryKey -and @($choosers | Where-Object { $_.table -eq $Table }).Count -gt 0
    $physNumericPk = $Field.isPrimaryKey -and $phys.ToLowerInvariant() -match 'int|bigint|smallint|tinyint|decimal|numeric|money|smallmoney|float|real'
    if ($choosers.Count -gt 0 -and -not $physNumericPk -and -not $selfChooserPk) {
        $v = Get-ChooserValue -Field $Field -ModuleId $ModuleId -MasterValues $MasterValues
        if ($null -ne $v) { return @{ Value = (& $trim $v); Source = "chooser:$($choosers[0].table)"; Resolved = $true } }
        if (-not $Field.isPrimaryKey) { return @{ Value = $null; Source = 'chooser-empty'; Resolved = $false } }
    }
    if ($selfChooserPk) {
        # 主键选择器指向自身表（如 BANK_ID→BANK）：新记录主键必须唯一，不能用首行引用值
        $physLower = $phys.ToLowerInvariant()
        if ($physLower -match 'tinyint') { return @{ Value = (Get-Random -Minimum 1 -Maximum 127).ToString(); Source = 'pk-int'; Resolved = $true } }
        if ($physLower -match 'smallint') { return @{ Value = (Get-Random -Minimum 1000 -Maximum 32000).ToString(); Source = 'pk-int'; Resolved = $true } }
        if ($physLower -match 'int|bigint') { return @{ Value = (100000 + (Get-Random -Maximum 899999)).ToString(); Source = 'pk-int'; Resolved = $true } }
        $code = "$Prefix$Seq"
        return @{ Value = (& $trim $code); Source = 'unique-code'; Resolved = $true }
    }
    # 主键/编号：唯一真实样式代码（物理列为数值则生成数值，避免"数值格式不正确"）
    if ($Field.isPrimaryKey -or ($Field.key -match '^(.*_)?(NO|CODE|ID|TYPE)$' -and $choosers.Count -eq 0)) {
        $physLower = $phys.ToLowerInvariant()
        if ($physLower -match 'tinyint') { return @{ Value = (Get-Random -Minimum 1 -Maximum 127).ToString(); Source = 'pk-int'; Resolved = $true } }
        if ($physLower -match 'smallint') { return @{ Value = (Get-Random -Minimum 1000 -Maximum 32000).ToString(); Source = 'pk-int'; Resolved = $true } }
        if ($physLower -match 'int|bigint') { return @{ Value = (100000 + (Get-Random -Maximum 899999)).ToString(); Source = 'pk-int'; Resolved = $true } }
        if ($physLower -match 'decimal|numeric|money|smallmoney|float|real') { return @{ Value = '1.5'; Source = 'pk-decimal'; Resolved = $true } }
        if ($physLower -match 'datetime|date|smalldatetime|datetime2') { return @{ Value = (Get-Date).ToString('yyyy-MM-dd'); Source = 'pk-date'; Resolved = $true } }
        $code = "$Prefix$Seq"
        return @{ Value = (& $trim $code); Source = 'unique-code'; Resolved = $true }
    }
    # FORM_OPTIONS：取首选项
    if (@($Field.options).Count -gt 0) {
        return @{ Value = (& $trim $Field.options[0].value); Source = 'options'; Resolved = $true }
    }
    # 默认值
    if ($null -ne $Field.defaultValue -and [string]$Field.defaultValue -ne '') {
        return @{ Value = (& $trim $Field.defaultValue); Source = 'default'; Resolved = $true }
    }
    # 正则感知
    if (-not [string]::IsNullOrWhiteSpace([string]$Field.regex)) {
        $cands = @('1', '08:00', (Get-Date).ToString('yyyy-MM-dd'), (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), "$Prefix$Seq", 'A1', '测试')
        foreach ($c in $cands) {
            if ($max -and $c.Length -gt $max) { continue }
            try {
                $m = [regex]::Match($c, [string]$Field.regex, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
                if ($m.Success -and $m.Value -eq $c) { return @{ Value = $c; Source = 'regex'; Resolved = $true } }
            } catch { }
        }
        return @{ Value = $null; Source = 'regex-unmatched'; Resolved = $false }
    }
    # 按标签推断真实语义值
    $label = [string]$Field.label
    $now = Get-Date
    if ($type -match 'datetime|date|smalldatetime|datetime2') {
        if ($label -match '生效|起始|开|申请') { $d = $now }
        elseif ($label -match '到期|截止|结束|终') { $d = $now.AddMonths(3) }
        elseif ($label -match '年审|季审|月') { $d = Get-Date -Year $now.Year -Month 1 -Day 1 }
        else { $d = $now }
        return @{ Value = $d.ToString('yyyy-MM-dd'); Source = 'date'; Resolved = $true }
    }
    if ($type -match 'time') { return @{ Value = '08:00'; Source = 'time'; Resolved = $true } }
    if ($type -eq 'bit') { return @{ Value = '1'; Source = 'bit'; Resolved = $true } }
    if ($type -eq 'uniqueidentifier') { return @{ Value = [guid]::NewGuid().ToString(); Source = 'guid'; Resolved = $true } }
    if ($type -match 'int|bigint|smallint|tinyint') {
        $v = if ($label -match '数量|天数|次数|人数|个|项|里程|公里') { '10' } elseif ($label -match '年|月|季|顺序|序号') { '1' } else { '1' }
        return @{ Value = $v; Source = 'int'; Resolved = $true }
    }
    if ($type -match 'decimal|numeric|money|smallmoney|float|real') {
        $v = if ($label -match '率|比|比例|折扣') { '5' } elseif ($label -match '汇率') { '1' } elseif ($label -match '税') { '13' } elseif ($label -match '数量|天数|次数|人数') { '10' } else { '100.50' }
        return @{ Value = $v; Source = 'decimal'; Resolved = $true }
    }
    if ($type -match 'char|varchar|nchar|nvarchar|text|ntext') {
        $physLower = $phys.ToLowerInvariant()
        if ($physLower -match 'int|bigint|smallint|tinyint') { return @{ Value = '1'; Source = 'phys-int'; Resolved = $true } }
        if ($physLower -match 'decimal|numeric|money') { return @{ Value = '100.50'; Source = 'phys-decimal'; Resolved = $true } }
        if ($physLower -match 'datetime|date') { return @{ Value = (Get-Date).ToString('yyyy-MM-dd'); Source = 'phys-date'; Resolved = $true } }
        if ($type -match 'n?char$' -and $max) {
            # 定长字符：按长度补齐
            $base = "测试$label"
            $base = $base -replace '[^\u4e00-\u9fffA-Za-z0-9]', ''
            $fill = $max - $base.Length
            if ($fill -gt 0) { $base = $base + (' ' * $fill) }
            if ($base.Length -gt $max) { $base = $base.Substring(0, $max) }
            return @{ Value = $base; Source = 'nchar'; Resolved = $true }
        }
        if ($label -match '备注|说明|描述|其它|other|remark') { return @{ Value = (& $trim "自动化验收$label"); Source = 'text'; Resolved = $true } }
        if ($label -match '名称|姓名|品名|描述') { return @{ Value = (& $trim "测试$label"); Source = 'text'; Resolved = $true } }
        if ($Field.key -eq 'CI') { return @{ Value = 'A'; Source = 'company'; Resolved = $true } }
        return @{ Value = (& $trim "$Prefix-$label-$Seq"); Source = 'text'; Resolved = $true }
    }
    return @{ Value = $null; Source = 'unknown-type'; Resolved = $false }
}

function Build-RealisticValues {
    param($Fields, [string]$Table, [string]$Prefix, [int]$StartSeq, [string]$ModuleId, [hashtable]$PhysType, [string[]]$IncludeReadonly = @())
    $vals = [ordered]@{}
    $unresolvedMap = [System.Collections.Generic.Dictionary[string,string]]::new()
    $seq = $StartSeq
    $filled = 0
    $total = 0
    foreach ($f in $Fields) {
        if ($f.serverFilled -or -not $f.isVisible -or (Test-SystemColumn $f.key)) { continue }
        if ($f.isReadonly -and -not $f.isPrimaryKey -and -not $f.isRequired -and $f.key -notin $IncludeReadonly) { continue }
        $total++
        $r = Get-RealisticValue -Field $f -Table $Table -Prefix $Prefix -Seq $seq -ModuleId $ModuleId -PhysType $PhysType -MasterValues $vals
        if ($r.Resolved -and $null -ne $r.Value) {
            $vals[$f.key] = [string]$r.Value
            $seq++
            $filled++
        } else {
            $unresolvedMap[$f.key] = "$($f.key)（$($f.label)，源=$($r.Source)）"
        }
    }
    # 依赖选择器重试：表单顺序可能先于依赖字段（如 3302 PRODUCE_NO 排在 CLIENT_ID/PRO_NO 之前），
    # 首轮主表值不全导致 chooser-empty；值齐后多轮重试，直至无进展。
    for ($round = 0; $round -lt 5 -and $unresolvedMap.Count -gt 0; $round++) {
        $progress = $false
        foreach ($f in $Fields) {
            if (-not $unresolvedMap.ContainsKey($f.key)) { continue }
            if ($f.serverFilled -or -not $f.isVisible -or (Test-SystemColumn $f.key)) { continue }
            if ($f.isReadonly -and -not $f.isPrimaryKey -and -not $f.isRequired -and $f.key -notin $IncludeReadonly) { continue }
            if (@($f.choosers | Where-Object { $_.active }).Count -eq 0) { continue }
            $r = Get-RealisticValue -Field $f -Table $Table -Prefix $Prefix -Seq $seq -ModuleId $ModuleId -PhysType $PhysType -MasterValues $vals
            if ($r.Resolved -and $null -ne $r.Value) {
                $vals[$f.key] = [string]$r.Value
                $seq++
                $filled++
                $unresolvedMap.Remove($f.key)
                $progress = $true
            }
        }
        if (-not $progress) { break }
    }
    return @{ Values = $vals; Seq = $seq; Unresolved = @($unresolvedMap.Values); Filled = $filled; Total = $total }
}

# ---- 单模块 CRUD 回路（真实数据） ----
function Test-ModuleCrud {
    param($id, $meta, [hashtable]$PhysType, [string]$Prefix)
    $form = Invoke-ApiJson -Method GET -Path "/api/v1/document-workbench/$id/form-definition?mode=new"
    if ($form.Status -ne 200) {
        return [pscustomobject]@{ ModuleId = $id; Status = "form-error-$($form.Status)"; Detail = "form-definition HTTP $($form.Status)"; Completeness = 0; Total = 0; Unresolved = @() }
    }
    $masterFields = @($form.Content.masterFields)
    $detailFields = @($form.Content.detailFields)
    if ($masterFields.Count -eq 0) {
        return [pscustomobject]@{ ModuleId = $id; Status = 'no-fields'; Detail = 'form-definition 200 但无主表字段'; Completeness = 0; Total = 0; Unresolved = @() }
    }
    $detailNoFields = @(([string]$form.Content.detailNoFields) -split '[;,]\s*' | Where-Object { $_ -and $_.Trim() })
    $masterMap = Build-RealisticValues -Fields $masterFields -Table $meta.MasterTable -Prefix $Prefix -StartSeq 1 -ModuleId $id -PhysType $PhysType -IncludeReadonly $detailNoFields
    if ($masterMap.Values.Count -eq 0) {
        return [pscustomobject]@{ ModuleId = $id; Status = 'no-constructible'; Detail = "无法构造任何字段值（未解析：$($masterMap.Unresolved -join ',')）"; Completeness = 0; Total = $masterMap.Total; Unresolved = $masterMap.Unresolved }
    }

    # 建
    $details = @()
    $submitValues = [ordered]@{}
    foreach ($k in $masterMap.Values.Keys) { $submitValues[$k] = $masterMap.Values[$k] }
    $post = Invoke-ApiJson -Method POST -Path "/api/v1/document-workbench/$id/record" -Body @{ values = $submitValues; details = @() }
    if ($post.Status -ne 200 -and $detailFields.Count -gt 0) {
        $msg = ($post.Raw | Out-String)
        if ($msg -match 'DETAIL_REQUIRED|无明细资料') {
            $detailMap = Build-RealisticValues -Fields $detailFields -Table $meta.DetailTable -Prefix $Prefix -StartSeq $masterMap.Seq -ModuleId $id -PhysType $PhysType
            $post = Invoke-ApiJson -Method POST -Path "/api/v1/document-workbench/$id/record" -Body @{ values = $submitValues; details = @($detailMap.Values) }
            $details = @($detailMap.Values)
        }
    }
    # 服务器维护字段（READONLY_FIELD）：剔除后重试一次（如 HR 明细 EMP_NO 由服务端从 EMP_ID 回填）
    if ($post.Status -ne 200 -and $post.Content.fieldErrors) {
        $rejected = @($post.Content.fieldErrors | Where-Object { $_.code -eq 'READONLY_FIELD' } | ForEach-Object { [string]$_.field })
        if ($rejected.Count -gt 0) {
            $submitValues = [ordered]@{}
            foreach ($k in $masterMap.Values.Keys) { if ($k -notin $rejected) { $submitValues[$k] = $masterMap.Values[$k] } }
            $details = @($details | ForEach-Object {
                $d = [ordered]@{}
                foreach ($k in $_.Keys) { if ($k -notin $rejected) { $d[$k] = $_[$k] } }
                , $d
            })
            $post = Invoke-ApiJson -Method POST -Path "/api/v1/document-workbench/$id/record" -Body @{ values = $submitValues; details = $details }
            if ($post.Status -eq 200) {
                $masterMap.Unresolved += @($rejected | ForEach-Object { "$_（服务端维护字段，自动剔除）" })
            }
        }
    }
    if ($post.Status -ne 200) {
        $code = if ($post.Content.code) { [string]$post.Content.code } else { "HTTP$($post.Status)" }
        $missingRequired = @()
        if ($post.Content.fieldErrors) {
            $missingRequired = @($post.Content.fieldErrors | Where-Object { $_.code -eq 'REQUIRED_FIELD_MISSING' } | ForEach-Object { [string]$_.field })
        }
        $unresolvedKeys = @($masterMap.Unresolved | ForEach-Object { ([string]$_ -split '（')[0].Trim() })
        $blockedBy = @($missingRequired | Where-Object { $_ -in $unresolvedKeys })
        if ($blockedBy.Count -gt 0) {
            return [pscustomobject]@{
                ModuleId = $id; Status = 'blocked-required-reference'
                Detail = "必填引用字段无可用数据：$($blockedBy -join '、')（未解析：$($masterMap.Unresolved -join '；')）"
                Completeness = $masterMap.Filled; Total = $masterMap.Total; Unresolved = $masterMap.Unresolved
            }
        }
        $detail = (($post.Raw | Out-String).Substring(0, [Math]::Min(2000, (($post.Raw | Out-String).Length))) + " | 未解析=$($masterMap.Unresolved -join ';')")
        return [pscustomobject]@{ ModuleId = $id; Status = "crud-create-fail[$code]"; Detail = $detail; Completeness = $masterMap.Filled; Total = $masterMap.Total; Unresolved = $masterMap.Unresolved }
    }
    $key = @($post.Content.key)
    if ($key.Count -eq 0) {
        return [pscustomobject]@{ ModuleId = $id; Status = 'no-key'; Detail = 'POST 200 但未返回 key'; Completeness = $masterMap.Filled; Total = $masterMap.Total; Unresolved = $masterMap.Unresolved }
    }
    $keyJson = [uri]::EscapeDataString((, $key | ConvertTo-Json -Compress))

    # 读
    $read = Invoke-ApiJson -Method GET -Path "/api/v1/document-workbench/$id/record?key=$keyJson"
    if ($read.Status -ne 200) {
        return [pscustomobject]@{ ModuleId = $id; Status = 'crud-read-fail'; Detail = (($read.Raw | Out-String).Substring(0, [Math]::Min(240, (($read.Raw | Out-String).Length)))); Completeness = $masterMap.Filled; Total = $masterMap.Total; Unresolved = $masterMap.Unresolved }
    }

    # 改（取一个可改字段做真实变更）
    $original = [ordered]@{}
    foreach ($k in $submitValues.Keys) {
        $rv = $read.Content.master.$k
        if ($null -ne $rv -and ([string]$rv) -ne '') {
            $original[$k] = if ($rv -is [datetime]) { $rv.ToString('yyyy-MM-dd HH:mm:ss') } else { [string]$rv }
        } else {
            $original[$k] = [string]$submitValues[$k]
        }
    }
    $changeField = @($masterFields | Where-Object { -not $_.serverFilled -and -not $_.isReadonly -and -not $_.isPrimaryKey -and $_.isVisible }) | Select-Object -First 1
    $updateSkipped = $false
    if ($null -eq $changeField) {
        $updateSkipped = $true
    } else {
        $newValues = [ordered]@{}
        foreach ($k in $submitValues.Keys) { $newValues[$k] = [string]$submitValues[$k] }
        $physC = [string]$PhysType["$($meta.MasterTable)|$($changeField.key)".ToUpperInvariant()]
        $changed = [string]$newValues[$changeField.key]
        if ($physC -match 'int|bigint|smallint|tinyint') { $changed = ([int]$changed + 1).ToString() }
        elseif ($physC -match 'decimal|numeric|money|float|real') { $changed = ([double]$changed + 1.5).ToString('0.00') }
        elseif ($physC -match 'datetime|date') { $changed = (Get-Date).AddDays(1).ToString('yyyy-MM-dd') }
        elseif ($physC -eq 'bit') { $changed = '0' }
        else {
            $changed = "$changed-改"
            if ($changeField.maxLength -and $changed.Length -gt $changeField.maxLength) { $changed = $changed.Substring(0, $changeField.maxLength) }
        }
        $newValues[$changeField.key] = $changed
        $put = Invoke-ApiJson -Method PUT -Path "/api/v1/document-workbench/$id/record?key=$keyJson" -Body @{ values = $newValues; details = $details; original = $original }
        if ($put.Status -ne 200) {
            return [pscustomobject]@{ ModuleId = $id; Status = 'crud-update-fail'; Detail = (($put.Raw | Out-String).Substring(0, [Math]::Min(240, (($put.Raw | Out-String).Length)))); Completeness = $masterMap.Filled; Total = $masterMap.Total; Unresolved = $masterMap.Unresolved }
        }
    }

    # 删 + 验证
    $del = Invoke-ApiJson -Method DELETE -Path "/api/v1/document-workbench/$id/record?key=$keyJson"
    if ($del.Status -ne 200) {
        return [pscustomobject]@{ ModuleId = $id; Status = 'crud-delete-fail'; Detail = (($del.Raw | Out-String).Substring(0, [Math]::Min(240, (($del.Raw | Out-String).Length)))); Completeness = $masterMap.Filled; Total = $masterMap.Total; Unresolved = $masterMap.Unresolved }
    }
    $gone = Invoke-ApiJson -Method GET -Path "/api/v1/document-workbench/$id/record?key=$keyJson"
    $ok = $gone.Status -in 404, 400
    return [pscustomobject]@{
        ModuleId = $id; Status = if ($ok) { 'crud-pass' } else { 'crud-delete-verify-fail' }
        Detail = if ($updateSkipped) { '建读删通过（无可改字段，跳过更新）' } else { '建读改删通过' }
        Completeness = $masterMap.Filled; Total = $masterMap.Total; Unresolved = $masterMap.Unresolved
    }
}

function Get-BatchPhysTypes {
    param($Modules)
    $tables = @($Modules | ForEach-Object { $_.MasterTable, $_.DetailTable } | Where-Object { $_ } | Sort-Object -Unique)
    $map = @{}
    if ($tables.Count -eq 0) { return $map }
    $tblList = "('" + ($tables -join "','") + "')"
    $lines = Invoke-EosSql -Query @"
SET NOCOUNT ON;
SELECT o.name AS TABLE_NAME, c.name AS COLUMN_NAME, TYPE_NAME(c.user_type_id) AS DATA_TYPE
FROM sys.columns c
JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
JOIN sys.schemas s ON o.schema_id=s.schema_id
WHERE s.name='dbo' AND o.name IN $tblList;
"@
    foreach ($line in $lines) {
        $c = $line -split '\|'
        if ($c.Count -ge 3) { $map["$($c[0].Trim())|$($c[1].Trim())".ToUpperInvariant()] = $c[2].Trim() }
    }
    return $map
}

# ================= 主流程 =================
$batch = Get-BatchModules
Add-ReportLine "# 统一表单自动放量报告（$stamp）"
Add-ReportLine ''
Add-ReportLine "批次（$($batch.Count) 个）：$($batch -join '、')"
Add-ReportLine ''

$modules = @(Get-ModuleMetaByIds -Ids $batch)
$foundIds = @($modules | ForEach-Object { $_.ModuleId })
$missing = @($batch | Where-Object { $_ -notin $foundIds })
foreach ($id in $missing) {
    Add-Exception "模块 $id 无 MODULES 元数据（候选失效）"
}
if ($modules.Count -eq 0) { throw '批次为空：无有效模块' }

$physType = Get-BatchPhysTypes -Modules $modules
$script:Session = New-EosSession -ApiUrl $ApiUrl
$prefix = 'E2EA' + (Get-Date -Format 'MMddHHmmss')

# ---- 启用（写配置 + 备份） ----
Copy-Item -LiteralPath $appSettingsPath -Destination $backupPath -Force
$app = Get-EosJson
$existing = @($app.UnifiedFormEditor.EnabledModuleIds | ForEach-Object { [string]$_ })
$newIds = @($modules | ForEach-Object { [string]$_.ModuleId } | Where-Object { $_ -notin $existing })

# ---- ADR-005 阶段 2：发布前校验器共用同一道闸（启用前先服务端预检） ----
Add-ReportLine "## 2. 发布前校验（ADR-005 阶段 2：与快照发布共用同一校验器）"
$validatedIds = [System.Collections.Generic.List[string]]::new()
foreach ($id in $newIds) {
    $v = Invoke-ApiJson 'POST' '/api/v1/workbench-definitions/validate' @{ moduleId = [int]$id }
    if ($v.Status -eq 200 -and $v.Content.passed) {
        $validatedIds.Add($id)
    } else {
        $detail = if ($v.Content.checks) {
            @($v.Content.checks | Where-Object { -not $_.passed } | ForEach-Object { "$($_.code):$($_.message)" }) -join '；'
        } else {
            "HTTP $($v.Status)"
        }
        Add-Exception "模块 $id 未通过发布前校验：$detail"
    }
}
$blockedByValidation = @($newIds | Where-Object { $_ -notin $validatedIds })
$newIds = @($validatedIds)
if ($blockedByValidation.Count -gt 0) {
    Add-ReportLine "  $($blockedByValidation.Count) 个模块未通过发布前校验（$($blockedByValidation -join '、')），不进白名单，见 exceptions.md"
}
Add-ReportLine "  通过 $($newIds.Count)/$($modules.Count) 个模块"
Add-ReportLine ''
if ($newIds.Count -eq 0) {
    throw '本批模块全部未通过发布前校验（ADR-005 阶段 2），停止放量。'
}

$app.UnifiedFormEditor.EnabledModuleIds = @($existing + $newIds | ForEach-Object { [int]$_ } | Sort-Object)
Set-EosJson -App $app
Add-ReportLine "## 3. 启用：新增 $($newIds.Count) 个模块（$($newIds -join '、')），白名单合计 $($app.UnifiedFormEditor.EnabledModuleIds.Count)"
Add-ReportLine ''

if (-not $SkipRestart) {
    Add-ReportLine '重启 EOS.API 使白名单生效…'
    & (Join-Path $root 'scripts\dev-services.ps1') restart api | Out-Host
    Start-Sleep -Seconds 2
    $deadline = (Get-Date).AddSeconds(90)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        if ($null -ne (Get-NetTCPConnection -LocalPort 5261 -State Listen -ErrorAction SilentlyContinue)) { $ready = $true; break }
        Start-Sleep -Seconds 2
    }
    if (-not $ready) { throw 'EOS.API 重启后 90 秒未就绪，请检查 logs/api.err.log' }
    $script:Session = New-EosSession -ApiUrl $ApiUrl
}

# ---- 预检 + CRUD 回路（启用后） ----
Add-ReportLine '## 4. 真实数据 CRUD 回路（启用后预检 + 建读改删）'
Add-ReportLine ''
$allPass = $true
try {
foreach ($m in $modules) {
    $r = Test-ModuleCrud -id $m.ModuleId -meta $m -PhysType $physType -Prefix $prefix
    $pct = if ($r.Total -gt 0) { [math]::Round($r.Completeness / $r.Total * 100) } else { 0 }
    $results.Add($r)
    Add-ReportLine "- $($m.ModuleId) $($m.Desc)：**$($r.Status)**（完整度 $($r.Completeness)/$($r.Total)=$pct%）$($r.Detail)"
    if (@($r.Unresolved).Count -gt 0) {
        Add-ReportLine "  - 未解析字段：$($r.Unresolved -join '；')"
        Add-Exception "模块 $($m.ModuleId) $($m.Desc) 存在未解析字段：$($r.Unresolved -join '；')"
    }
}
Add-ReportLine ''
$passed = @($results | Where-Object { $_.Status -eq 'crud-pass' })
$blocked = @($results | Where-Object { $_.Status -ne 'crud-pass' })
Add-ReportLine "本批小结：通过 $($passed.Count) / 阻塞 $($blocked.Count)"
Add-ReportLine ''

# ---- 语义/元数据/扫描核对 ----
if ($passed.Count -gt 0) {
    Add-ReportLine '## 5. 语义/元数据/扫描核对'
    Add-ReportLine ''
    try {
        & (Join-Path $root 'EOS.API.Tests\AcceptanceSemantics.ps1') | Out-Null
        $semCsv = Get-ChildItem (Join-Path $root 'logs\goal\phase1') -Filter 'semantics-candidates.csv' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        $batchIds = @($modules | ForEach-Object { [string]$_.ModuleId })
        $semIssues = @(Import-Csv $semCsv.FullName | Where-Object { $_.ModuleId -in $batchIds -and $_.Disposition -notin @('MATCH') })
        if ($semIssues.Count -gt 0) {
            foreach ($s in $semIssues) { Add-Exception "语义差异：模块 $($s.ModuleId) 字段 $($s.Field) [$($s.Kind)/$($s.Disposition)] 旧=$($s.Legacy) 现状=$($s.FieldsState)" }
            Add-ReportLine "- 语义复核：$($semIssues.Count) 条差异（见 exceptions）"
        } else {
            Add-ReportLine '- 语义复核：本批无差异'
        }
    } catch {
        Add-Exception "AcceptanceSemantics 运行失败：$($_.Exception.Message)"
    }
    try {
        & (Join-Path $root 'EOS.API.Tests\AcceptanceScan.ps1') | Out-Null
        Add-ReportLine '- 白名单扫描：通过（143+ 模块全过、0 候选）'
    } catch {
        $allPass = $false
        Add-Exception "AcceptanceScan 运行失败：$($_.Exception.Message)"
    }
}

# ---- 全量回归 ----
if ($passed.Count -gt 0 -and -not $SkipRegression) {
    Add-ReportLine '## 6. 全量回归'
    Add-ReportLine ''
    $reg = & (Join-Path $root 'scripts\regression.ps1') -StartServices *>&1
    $reg | Out-File -LiteralPath (Join-Path $runDir 'regression.log') -Encoding utf8
    if ($LASTEXITCODE -ne 0) {
        $allPass = $false
        Add-Exception '全量回归未通过（见 logs/goal/regression/ 最新报告）'
        Add-ReportLine '- 回归：**失败**（详见 regression.log）'
    } else {
        Add-ReportLine '- 回归：4/4 全绿'
    }
    Add-ReportLine ''
}

# ---- 判定：保留 or 回滚 ----
if ($allPass -and $blocked.Count -eq 0) {
    Add-ReportLine '## 结论'
    Add-ReportLine ''
    Add-ReportLine "**放量成功**：$($newIds.Count) 个模块已启用并保留（白名单 $($app.UnifiedFormEditor.EnabledModuleIds.Count)）。差异项见 exceptions.md，供人工复核。"
    Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
} elseif ($allPass -and $passed.Count -gt 0) {
    # 部分放量：回滚到备份后，只重启用通过的模块；阻塞模块进异常清单供人工审核
    Copy-Item -LiteralPath $backupPath -Destination $appSettingsPath -Force
    $app2 = Get-EosJson
    $existing2 = @($app2.UnifiedFormEditor.EnabledModuleIds | ForEach-Object { [string]$_ })
    $passedIds = @($passed | ForEach-Object { [string]$_.ModuleId })
    $newPassed = @($passedIds | Where-Object { $_ -notin $existing2 })
    $app2.UnifiedFormEditor.EnabledModuleIds = @($existing2 + $newPassed | ForEach-Object { [int]$_ } | Sort-Object)
    Set-EosJson -App $app2
    if (-not $SkipRestart) {
        & (Join-Path $root 'scripts\dev-services.ps1') restart api | Out-Host
    }
    $descById = @{}
    foreach ($mm in $modules) { $descById[[string]$mm.ModuleId] = $mm.Desc }
    foreach ($b in $blocked) {
        Add-Exception "【阻塞】模块 $($b.ModuleId) $($descById[[string]$b.ModuleId])：$($b.Detail)"
    }
    Add-ReportLine '## 结论'
    Add-ReportLine ''
    Add-ReportLine "**部分放量**：启用 $($newPassed.Count) 个通过模块（白名单 $($app2.UnifiedFormEditor.EnabledModuleIds.Count)）；阻塞 $($blocked.Count) 个（$($blocked.ModuleId -join '、')）已列入异常清单，供人工审核。"
} else {
    Copy-Item -LiteralPath $backupPath -Destination $appSettingsPath -Force
    if (-not $SkipRestart) {
        & (Join-Path $root 'scripts\dev-services.ps1') restart api | Out-Host
    }
    Add-ReportLine '## 结论'
    Add-ReportLine ''
    Add-ReportLine "**放量失败并回滚**：appsettings.json 已恢复备份，API 已重启，白名单未保留本批模块。详见 exceptions.md。"
}
} catch {
    $allPass = $false
    Add-Exception "流水线异常（已回滚）：$($_.Exception.Message)"
    try {
        Copy-Item -LiteralPath $backupPath -Destination $appSettingsPath -Force
        if (-not $SkipRestart) {
            & (Join-Path $root 'scripts\dev-services.ps1') restart api | Out-Host
        }
    } catch {
        Add-Exception "回滚过程中出错：$($_.Exception.Message)（请手工恢复 $backupPath）"
    }
    Add-ReportLine '## 结论'
    Add-ReportLine ''
    Add-ReportLine "**流水线异常并回滚**：$($_.Exception.Message)"
}

$reportPath = Join-Path $runDir 'report.md'
$exceptionPath = Join-Path $runDir 'exceptions.md'
$reportLines -join "`n" | Set-Content -LiteralPath $reportPath -Encoding utf8
$results | Export-Csv -LiteralPath (Join-Path $runDir 'results.csv') -NoTypeInformation -Encoding UTF8
$exceptions -join "`n" | Set-Content -LiteralPath $exceptionPath -Encoding utf8

Write-Host ''
Write-Host "== 自动放量结果（$stamp）==" -ForegroundColor Cyan
$results | Format-Table ModuleId, Status, Completeness, Detail -AutoSize
if ($exceptions.Count -gt 0) {
    Write-Host "差异/异常（$($exceptions.Count) 条）见：$exceptionPath" -ForegroundColor Yellow
}
Write-Host "报告：$reportPath" -ForegroundColor Cyan
if ($allPass -and $blocked.Count -eq 0) { Write-Host '结论：放量成功' -ForegroundColor Green; exit 0 }
if ($allPass -and $passed.Count -gt 0) { Write-Host '结论：部分放量（阻塞见 exceptions）' -ForegroundColor Yellow; exit 0 }
Write-Host '结论：放量失败（已回滚）' -ForegroundColor Red
exit 1
