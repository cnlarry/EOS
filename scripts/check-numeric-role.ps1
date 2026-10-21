<#
.SYNOPSIS
    连库门禁：FIELDS.NUMERIC_ROLE 的完整性与一致性。

.DESCRIPTION
    依据 docs/decisions/ADR-038-数值列的语义标准化.md（决定四、决定七）。
    五项断言：
      1) 物理 float 列必须已登记 NUMERIC_ROLE（白名单见脚本内，只减不增）；
      2) NUMERIC_ROLE 只能取 AMOUNT/QTY/PRICE/RATE/PERCENT/NON_NUMERIC；
      3) 已登记语义的字段，F_TYPE 与物理类型都必须属于数值类
         （F_TYPE 是展示类型，nvarchar 对 nchar、date 对 datetime 属正常，不做逐字相等）；
      4) 数值列的显式显示格式必须带千分位（**所有语义族**，不限金额）：写成 `0.00` / `0.##` / `0.###`
         这类型式会盖掉语义族默认的分组，同一个列表里并排的数量列于是显示成 `600000.00`
         而不是 `600,000.00`（ADR-037 决定六的默认口径带千分位）。留空即走默认，视为合格；
         允许的写法是按位数加 `,` 的分组等价式（`#,##0.00`、`#,##0.##`、`#,##`）；
      5) 复制型视图的字段必须与源表同名字段登记同一语义族（视图是独立 T_ID，
         整表复制 FIELDS 时漏一列就静默丢语义）。

.PARAMETER Report
    只输出报告、始终返回 0（用于盘点）；默认发现违规即返回 1。
#>
param(
    [switch] $Report
)

$ErrorActionPreference = 'Stop'
try {
    [Console]::OutputEncoding = [Text.Encoding]::UTF8
    $root = Split-Path -Parent $PSScriptRoot
    . (Join-Path $root 'scripts/dev/eos-sql.ps1')

    # 白名单当前为空：两批历史缺口都已在 ADR-038 收口时清掉。
    #   INV_FREEZE.FREEZE_QTY / INV_RESERVE.RESERVE_QTY —— 批 B 已把它们转为 decimal，不再是 float；
    #   SFC_PROCEDURE 三个单位用量字段 —— 迁移 380 同步 F_TYPE 后，登记值与物理类型已一致。
    # 新增例外必须写清理由与清除条件，否则门禁会退化成口头约定。
    $allowUnregistered = @()
    $allowTypeMismatch = @()

    $problems = New-Object System.Collections.ArrayList

    # 1) 未登记的 float 物理列（结果按单列拼接返回，见 eos-sql.ps1 的文本行约定）
    $unregistered = Invoke-EosSqlQuery -Query @"
SELECT o.name + N'|' + c.name
FROM sys.columns c
JOIN sys.objects o ON c.object_id = o.object_id AND o.type = 'U'
JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE s.name = N'dbo' AND TYPE_NAME(c.user_type_id) = 'float'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.FIELDS f
      WHERE f.T_ID = o.name AND f.F_ID = c.name AND f.NUMERIC_ROLE IS NOT NULL)
ORDER BY o.name, c.name;
"@
    foreach ($r in @($unregistered)) {
        $key = ([string]$r).Trim()
        if (-not $key) { continue }
        if ($allowUnregistered -contains $key) { continue }
        [void]$problems.Add("未登记语义：$key")
    }

    # 2) 非法枚举值
    $badRole = Invoke-EosSqlQuery -Query @"
SELECT T_ID + N'|' + F_ID + N'|' + NUMERIC_ROLE
FROM dbo.FIELDS
WHERE NUMERIC_ROLE IS NOT NULL
  AND NUMERIC_ROLE NOT IN (N'AMOUNT', N'QTY', N'PRICE', N'RATE', N'PERCENT', N'NON_NUMERIC')
ORDER BY T_ID, F_ID;
"@
    foreach ($r in @($badRole)) {
        $key = ([string]$r).Trim()
        if ($key) { [void]$problems.Add("非法 NUMERIC_ROLE：$key") }
    }

    # 3) 已登记语义的字段，元数据类型与物理类型都必须属于数值类。
    #    注意 F_TYPE 是"展示类型"（nvarchar 对 nchar、date 对 datetime 属正常），不做逐字相等比较。
    $notNumeric = Invoke-EosSqlQuery -Query @"
SELECT f.T_ID + N'|' + f.F_ID + N'|' + ISNULL(f.F_TYPE, N'(空)') + N'|' + ISNULL(TYPE_NAME(c.user_type_id), N'(无物理列)')
FROM dbo.FIELDS f
LEFT JOIN sys.columns c
  ON c.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(f.T_ID)) AND c.name = f.F_ID
WHERE f.NUMERIC_ROLE IS NOT NULL
  AND (
      LOWER(ISNULL(f.F_TYPE, N'')) NOT IN (N'float', N'decimal', N'numeric', N'money', N'smallmoney', N'int', N'bigint', N'smallint', N'tinyint', N'real')
      OR (c.column_id IS NOT NULL AND LOWER(TYPE_NAME(c.user_type_id)) NOT IN (N'float', N'decimal', N'numeric', N'money', N'smallmoney', N'int', N'bigint', N'smallint', N'tinyint', N'real'))
  )
ORDER BY f.T_ID, f.F_ID;
"@
    foreach ($r in @($notNumeric)) {
        $text = ([string]$r).Trim()
        if (-not $text) { continue }
        $parts = $text -split '\|'
        $pair = "$($parts[0])|$($parts[1])"
        if ($allowTypeMismatch -contains $pair) { continue }
        [void]$problems.Add("已登记语义但类型非数值：$text（表|字段|F_TYPE|物理类型）")
    }

    # 4) 数值列的显式显示格式必须带千分位（所有语义族，不限金额）。
    #    判据：显式格式非空、且不含千分位占位符（`,`）即不合格——`0.00`（固定两位不分组）、
    #    `0.##` / `0.###`（去尾零且不分组）、`#`（全可选位且不分组）都在内。留空走语义族默认，合格。
    $ungroupedNumeric = Invoke-EosSqlQuery -Query @"
SELECT f.T_ID + N'|' + f.F_ID + N'|' + f.DISPLAY_FORMAT
FROM dbo.FIELDS AS f
WHERE LOWER(ISNULL(f.F_TYPE, N'')) IN (N'decimal', N'numeric', N'float', N'money', N'smallmoney', N'real')
  AND LTRIM(RTRIM(ISNULL(f.DISPLAY_FORMAT, N''))) <> N''
  AND CHARINDEX(N',', f.DISPLAY_FORMAT) = 0
ORDER BY f.T_ID, f.F_ID;
"@
    foreach ($r in @($ungroupedNumeric)) {
        $text = ([string]$r).Trim()
        if ($text) { [void]$problems.Add("数值显示格式缺千分位：$text（表|字段|格式；写 #,##0.00 一类分组格式，或留空）") }
    }

    # 5) 复制型视图的字段必须与源表同名字段登记同一语义族。
    #    视图是独立的 T_ID：把基表的 FIELDS 行整表复制过去时，复制列表里漏一列就会静默丢语义，
    #    金额于是掉进"其余数值"回落（800 显示成 800），而字段、格式、对齐看上去全都正常。
    #    清单是显式的「视图 ← 源表」，新增复制型视图时在这里补一行。
    $viewSource = @(
        @{ View = 'V_COP_ORDER_D_UNDELIVERED'; Source = 'COP_ORDER_D' }
        @{ View = 'V_COP_ORDER_D_UNPRODUCED'; Source = 'COP_ORDER_D' }
        @{ View = 'V_COP_SEND_D_UNRECONCILED'; Source = 'COP_SEND_D' }
        @{ View = 'V_PUR_RECEIVE_D_UNRECONCILED'; Source = 'PUR_RECEIVE_D' }
        @{ View = 'V_COP_ACCOUNT_M'; Source = 'COP_ACCOUNT_M' }
        @{ View = 'V_PUR_DUE_M'; Source = 'PUR_DUE_M' }
    )
    $pairsSql = ($viewSource | ForEach-Object { "SELECT N'$($_.View)' AS V, N'$($_.Source)' AS S" }) -join ' UNION ALL '
    $roleDrift = Invoke-EosSqlQuery -Query @"
WITH MAP AS ($pairsSql)
SELECT m.V + N'|' + f.F_ID + N'|' + ISNULL(f.NUMERIC_ROLE, N'(空)') + N'|' + ISNULL(s.NUMERIC_ROLE, N'(空)')
FROM MAP AS m
JOIN dbo.FIELDS AS f ON LTRIM(RTRIM(f.T_ID)) = m.V
JOIN dbo.FIELDS AS s ON LTRIM(RTRIM(s.T_ID)) = m.S AND LTRIM(RTRIM(s.F_ID)) = LTRIM(RTRIM(f.F_ID))
WHERE ISNULL(f.NUMERIC_ROLE, N'') <> ISNULL(s.NUMERIC_ROLE, N'')
ORDER BY m.V, f.F_ID;
"@
    foreach ($r in @($roleDrift)) {
        $text = ([string]$r).Trim()
        if ($text) { [void]$problems.Add("视图字段语义族与源表不一致：$text（视图|字段|视图语义|源表语义）") }
    }

    if ($problems.Count -eq 0) {
        Write-Output 'PASS 语义登记完整、枚举合法、F_TYPE 与物理类型一致、金额显示格式带千分位、视图字段语义族与源表一致'
        exit 0
    }

    Write-Output "FAIL 共 $($problems.Count) 项："
    $problems | Select-Object -First 40 | ForEach-Object { Write-Output "  $_" }
    if ($problems.Count -gt 40) { Write-Output "  ...（其余 $($problems.Count - 40) 项略）" }
    if ($Report) { Write-Output '（-Report 模式，不计为失败）'; exit 0 }
    exit 1
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
