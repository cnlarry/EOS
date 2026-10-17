# 一次性｜锚点：ADR-038 / 迁移 357（FIELDS.NUMERIC_ROLE 语义登记）
<#
.SYNOPSIS
    一次性脚本：推断 float 列的语义族并生成迁移 357（随 ADR-038 收口失效）

.DESCRIPTION
    从 db/bootstrap 只读推断每个 float 列的语义族，输出 docs/plans/numeric-role-full.csv。
    迁移 357（语义登记）已执行；本脚本日常只用于重算清单与差异核对，
    需要重新生成迁移时才传 -EmitSql。
    语义有变动时，建库基线需用 sync-bootstrap-numeric-types.ps1 一并同步（ADR-038 §5.3）。
    判定顺序：列名词根 > 字段中文名 > 同表佐证/同簇同质 > 表上下文 > 待确认。
    已拍板的 19 项人工结论硬编码在脚本内（$decided），改动需同步 ADR-038。

.PARAMETER EmitSql
    可选：输出语义登记迁移 SQL 的路径。语义登记（迁移 357）已执行，日常重算只更新 CSV。
#>
param([string] $EmitSql)
$ErrorActionPreference = 'Stop'
try {
    [Console]::OutputEncoding = [Text.Encoding]::UTF8
    $root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $schema = Get-Content (Join-Path $root 'db\bootstrap\10_schema.sql') -Raw -Encoding utf8
    $meta = Get-Content (Join-Path $root 'db\bootstrap\20_metadata.sql') -Raw -Encoding utf8
    $csvPath = Join-Path $root 'docs\plans\numeric-role-full.csv'

    # ---- 1. 物理 float 列 ----
    $cols = New-Object System.Collections.ArrayList
    foreach ($t in [regex]::Matches($schema, '(?s)CREATE TABLE \[dbo\]\.\[([A-Z0-9_]+)\]\((.*?)\r?\n\)\r?\n')) {
        $table = $t.Groups[1].Value
        $body = $t.Groups[2].Value
        $hasCurr = $body -match '\[CURR_ID\]'
        $hasUnit = $body -match '\[UNIT_ID\]'
        foreach ($c in [regex]::Matches($body, '\[([A-Za-z0-9_]+)\] \[float\]')) {
            [void]$cols.Add([pscustomobject]@{ Table = $table; Column = $c.Groups[1].Value; HasCurr = $hasCurr; HasUnit = $hasUnit })
        }
    }

    # ---- 2. 元数据 ----
    $tableDesc = @{}
    foreach ($m in [regex]::Matches($meta, "(?m)^\s*\(N'([^']*)',\s*N'((?:[^']|'')*)',\s*N'(?:TABLE|VIEW)'")) {
        $tableDesc[$m.Groups[1].Value] = $m.Groups[2].Value -replace "''", "'"
    }
    $fieldDesc = @{}
    foreach ($m in [regex]::Matches($meta, "(?m)^\s*\(N'([^']*)',\s*N'([A-Za-z0-9_]*)',\s*N'((?:[^']|'')*)',\s*N'([a-zA-Z0-9]*)'")) {
        $key = "$($m.Groups[1].Value)|$($m.Groups[2].Value)"
        if (-not $fieldDesc.ContainsKey($key)) { $fieldDesc[$key] = $m.Groups[3].Value -replace "''", "'" }
    }
    $modMaster = @{}; $modDetail = @{}
    $modPat = "(?m)^\s*\((\d+),\s*N'((?:[^']|'')*)',\s*N'((?:[^']|'')*)',\s*(?:NULL|\d+),\s*(?:NULL|\d+),\s*(?:NULL|\d+),\s*(?:NULL|\d+),\s*N'((?:[^']|'')*)',\s*N'((?:[^']|'')*)',\s*N'((?:[^']|'')*)'"
    foreach ($m in [regex]::Matches($meta, $modPat)) {
        $tag = "$($m.Groups[1].Value) $($m.Groups[2].Value -replace "''", "'")"
        $mt = $m.Groups[4].Value; $dt = $m.Groups[6].Value
        if ($mt) { $modMaster[$mt] = (@($modMaster[$mt]) + @($tag) | Where-Object { $_ }) -join ' / ' }
        if ($dt) { $modDetail[$dt] = (@($modDetail[$dt]) + @($tag) | Where-Object { $_ }) -join ' / ' }
    }

    # ---- 3. 判定 ----
    function Get-RoleByColumn([string]$n) {
        if ($n -match '(?i)PCT$|PERCENT|RATE$') { return 'PERCENT' }
        if ($n -match '(?i)PRICE') { return 'PRICE' }
        if ($n -match '(?i)AMOUNT|_SUM$|^SUM_|FEE|COST') { return 'AMOUNT' }
        if ($n -match '(?i)QTY|DAYS|OVERTIME|WORKTIME|WEIGHT|LENGTH|HEIGHT|WIDTH|MILEAGE|HOURS|HOUR|PCS|MULTIPLE|GRAVITY') { return 'QTY' }
        return ''
    }
    function Get-RoleByDesc([string]$d) {
        if ([string]::IsNullOrWhiteSpace($d)) { return @('', '无说明') }
        if ($d -match '有无|是否|类别|类型|编号|名称|项次|状态') { return @('NON_NUMERIC', '说明指向判定/编码') }
        if ($d -match '单价|价格|报价|售价|进价|底价|加工价') { return @('PRICE', '含价格词') }
        if ($d -match '汇率|换算率|折算率|转换率|系数|单耗|机时/件|件/机时') { return @('RATE', '含汇率/系数词') }
        # 明确比率标记优先；含"费/费率"的归金额，避免 decimal(9,6) 装不下大额
        if ($d -match '%|百分比|折扣|比例|负荷|比率') { return @('PERCENT', '含明确比率标记') }
        if ($d -match '费率|费用|费') { return @('AMOUNT', '含费用词') }
        if ($d -match '率') { return @('PERCENT', '含比率词') }
        if ($d -match '金额|总额|總額|合计|小计|余额|工资|工資|薪|津贴|獎|奖|补助|補助|社保|公积金|所得税|所得稅|补贴|扣款|扣|减项|減項|罚款|运费|加工费|成本|税额|费用|費用|预付|应收|应付|发票|返利|佣金|折让|押金|保证金|价税|厂服|水电|住宿|伙食|维修|維修|清洁|清潔|应发|实发|實發') { return @('AMOUNT', '含金额/薪资词') }
        if ($d -match '数量|數量|件数|数|重|长|長|宽|高|面积|体积|材积|米|吨|公斤|克|升|工时|時間|时间|人时|机时|天数|分鐘|分钟|小时|次数|寿命|壽命|含量|浓度|产能|張數|张数|包数|箱数|间距|厚度') { return @('QTY', '含数量/度量词') }
        if ($d -match '额|价|费|費|款') { return @('AMOUNT', '含金额单字') }
        return @('', '说明无金额/数量特征')
    }
    function Get-RoleByCluster([string]$table, [string]$col) {
        if ($table -match '^(HR|HRM)_WAGE_D$' -and $col -match '^WAGE_ITEM') { return 'AMOUNT' }
        if ($table -in @('HR_ADD_D', 'HR_SUBTRACT_D')) { return 'AMOUNT' }
        if ($col -match 'BASEPAY') { return 'AMOUNT' }
        if ($col -match '^(BE_LATE_FOR|LEAVE_EARLY)\d*$') { return 'QTY' }
        if ($col -match 'ODOMETER|RUN_METER') { return 'QTY' }
        if ($col -in @('MACHINE_HOUR_UNIT', 'MACHINE_UNIT_HOUR')) { return 'RATE' }
        if ($table -match '^SFC_' -and $col -match 'TIME|HOUR|CONGCHAI') { return 'QTY' }
        if ($table -eq 'QC_ANALYSIS_D' -and $col -match '^RESULT\d') { return 'QTY' }
        if ($table -eq 'QC_REWORK_M' -and $col -match '^AMERCE\d') { return 'QTY' }
        if ($table -eq 'QC_SAMPLE_M' -and $col -match '^SAMPLE_') { return 'NON_NUMERIC' }
        if ($table -match '^(COP|PUR)_MONTH_D$' -and $col -match '^CURRENT_') { return 'AMOUNT' }
        return ''
    }
    # 已拍板的人工结论（19 项，2026-10-17）
    $decided = @{
        'COP_ACCOUNT_DD_D|BACK_BAD'          = 'QTY'
        'COP_ACCOUNT_DD_D|BACK_MATERIAL'     = 'QTY'
        'COP_ORDER_M|SUBSCRIPTION'           = 'AMOUNT'
        'COP_PACK_D|BOX_CUBAGE'              = 'QTY'
        'PRODUCT|PRODUCE_ULLAGE'             = 'QTY'
        'PRODUCT_EDITION|PRODUCE_ULLAGE'     = 'QTY'
        'SAMPLE_PRO|PRODUCE_ULLAGE'          = 'QTY'
        'SFC_PAY_D|RANGE'                    = 'AMOUNT'
        'QC_COMPLAIN_M|AMERCE4'              = 'AMOUNT'
        'QC_COMPLAIN_M|AMERCE5'              = 'AMOUNT'
        'QC_EXCEPTION_M|AMERCE4'             = 'AMOUNT'
        'QC_EXCEPTION_M|AMERCE5'             = 'AMOUNT'
        'HR_WAGE_D|WAGE_ITEM37'              = 'NON_NUMERIC'
        'HRM_WAGE_D|WAGE_ITEM31'             = 'NON_NUMERIC'
        'HRM_WAGE_D|WAGE_ITEM36'             = 'NON_NUMERIC'
        'QC_SAMPLE_M|SAMPLE_SG'              = 'NON_NUMERIC'
        'QC_SAMPLE_M|SAMPLE_LC'              = 'NON_NUMERIC'
        'QC_SAMPLE_M|SAMPLE_XD'              = 'NON_NUMERIC'
        'WF_FUNCTION_INFO|serialno'          = 'NON_NUMERIC'
    }

    $rows = New-Object System.Collections.ArrayList
    foreach ($c in $cols) {
        $desc = $fieldDesc["$($c.Table)|$($c.Column)"]
        $key = "$($c.Table)|$($c.Column)"
        $role = ''; $source = ''; $note = ''

        if ($decided.ContainsKey($key)) { $role = $decided[$key]; $source = '人工拍板'; $note = "说明=$desc" }
        else {
            $byCol = Get-RoleByColumn $c.Column
            $byDesc, $why = Get-RoleByDesc $desc
            $byCluster = Get-RoleByCluster $c.Table $c.Column
            # 冲突消解：说明判为比率、但列名是"合计"结尾时，存的是金额而非比率。
            # 例：REBATE_SUM 说明为"现金折扣"，实测最大 21350，是折扣金额。
            if (($byDesc -eq 'PERCENT' -or $byDesc -eq 'RATE') -and $c.Column -match '(?i)_SUM$') {
                $byDesc = 'AMOUNT'; $why = '列名为合计，说明中的比率词描述的是计算口径'
            }
            if ($byDesc -in @('QTY', 'PERCENT', 'RATE', 'PRICE')) { $role = $byDesc; $source = '说明'; $note = $why }
            elseif ($byDesc -eq 'NON_NUMERIC') { $role = $byDesc; $source = '说明'; $note = $why }
            elseif ($byCluster) { $role = $byCluster; $source = '簇'; $note = "同簇同质（$why）" }
            elseif ($byCol) { $role = $byCol; $source = '列名词根'; $note = '按列名后缀/前缀' }
            elseif ($byDesc) { $role = $byDesc; $source = '说明'; $note = $why }
            elseif ($c.HasCurr -and -not $c.HasUnit) { $role = 'AMOUNT'; $source = '表上下文'; $note = "同表有 CURR_ID；说明=$desc" }
            elseif ($c.HasUnit -and -not $c.HasCurr) { $role = 'QTY'; $source = '表上下文'; $note = "同表有 UNIT_ID；说明=$desc" }
            else { $role = 'UNKNOWN'; $source = '未定'; $note = "说明=$desc" }
        }

        $modMasterText = $modMaster[$c.Table]; $modDetailText = $modDetail[$c.Table]
        [void]$rows.Add([pscustomobject]@{
                Module     = if ($modMasterText) { $modMasterText } else { $modDetailText }
                ModuleRole = if ($modMasterText) { '主表' } elseif ($modDetailText) { '明细' } else { '' }
                Table      = $c.Table
                TableDesc  = $tableDesc[$c.Table]
                Column     = $c.Column
                FieldDesc  = $desc
                Role       = $role
                Source     = $source
                Note       = $note
            })
    }

    $rows | Sort-Object Table, Column | Export-Csv -Path $csvPath -NoTypeInformation -Encoding utf8BOM

    # ---- 4. 生成迁移 SQL ----
    if (-not $EmitSql) {
        "TOTAL_COLS=$($cols.Count)"
        $rows | Group-Object Role | Sort-Object Count -Descending | ForEach-Object { "{0,-14} {1,5}" -f $_.Name, $_.Count }
        "CSV=$csvPath"
        "（未指定 -EmitSql，跳过迁移生成）"
        exit 0
    }
    $sqlPath = if ([IO.Path]::IsPathRooted($EmitSql)) { $EmitSql } else { Join-Path $root $EmitSql }

    $lines = New-Object System.Collections.ArrayList
    [void]$lines.Add('-- 357：数值列语义登记（FIELDS.NUMERIC_ROLE）')
    [void]$lines.Add('-- 由 scripts/oneshot/numeric-role-registry.ps1 依据 db/bootstrap 推断生成；')
    [void]$lines.Add('-- 语义族定义见 docs/decisions/ADR-038-数值列的语义标准化.md 第 2 节。')
    [void]$lines.Add('')
    [void]$lines.Add('IF COL_LENGTH(N''dbo.FIELDS'', N''NUMERIC_ROLE'') IS NULL')
    [void]$lines.Add('BEGIN')
    [void]$lines.Add('    ALTER TABLE [dbo].[FIELDS] ADD [NUMERIC_ROLE] [nvarchar](16) NULL;')
    [void]$lines.Add('END')
    [void]$lines.Add('GO')
    [void]$lines.Add('')

    $batch = 400
    $items = @($rows | Where-Object { $_.Role -ne 'UNKNOWN' })
    "ROLE_ROWS=$($items.Count)"
    for ($i = 0; $i -lt $items.Count; $i += $batch) {
        $slice = $items[$i..([Math]::Min($i + $batch - 1, $items.Count - 1))]
        [void]$lines.Add('-- 回填：只更新物理列存在且尚未登记的字段')
        [void]$lines.Add('UPDATE f')
        [void]$lines.Add('SET f.NUMERIC_ROLE = v.ROLE')
        [void]$lines.Add('FROM [dbo].[FIELDS] AS f')
        [void]$lines.Add('JOIN (VALUES')
        $vals = foreach ($r in $slice) {
            "    (N'$($r.Table)', N'$($r.Column)', N'$($r.Role)')"
        }
        for ($j = 0; $j -lt $vals.Count; $j++) {
            $suffix = if ($j -eq $vals.Count - 1) { '' } else { ',' }
            [void]$lines.Add($vals[$j] + $suffix)
        }
        [void]$lines.Add(') AS v(T_ID, F_ID, ROLE)')
        [void]$lines.Add('    ON f.T_ID = v.T_ID AND f.F_ID = v.F_ID')
        [void]$lines.Add('WHERE f.NUMERIC_ROLE IS NULL')
        [void]$lines.Add('  AND EXISTS (')
        [void]$lines.Add('      SELECT 1 FROM sys.columns AS c')
        [void]$lines.Add('      JOIN sys.objects AS o ON c.object_id = o.object_id AND o.type IN (''U'', ''V'')')
        [void]$lines.Add('      JOIN sys.schemas AS s ON o.schema_id = s.schema_id')
        [void]$lines.Add('      WHERE s.name = N''dbo'' AND o.name = f.T_ID AND c.name = f.F_ID)')
        [void]$lines.Add('GO')
        [void]$lines.Add('')
    }
    $lines -join "`r`n" | Set-Content -Path $sqlPath -Encoding utf8

    "TOTAL_COLS=$($cols.Count)"
    $rows | Group-Object Role | Sort-Object Count -Descending | ForEach-Object { "{0,-14} {1,5}" -f $_.Name, $_.Count }
    "---- 判定来源 ----"
    $rows | Group-Object Source | Sort-Object Count -Descending | ForEach-Object { "{0,-10} {1,5}" -f $_.Name, $_.Count }
    "CSV=$csvPath"
    "SQL=$sqlPath"
    exit 0
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
