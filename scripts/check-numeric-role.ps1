<#
.SYNOPSIS
    连库门禁：FIELDS.NUMERIC_ROLE 的完整性与一致性。

.DESCRIPTION
    依据 docs/decisions/ADR-038-数值列的语义标准化.md（决定四、决定七）。
    三项断言：
      1) 物理 float 列必须已登记 NUMERIC_ROLE（白名单见脚本内，只减不增）；
      2) NUMERIC_ROLE 只能取 AMOUNT/QTY/PRICE/RATE/PERCENT/NON_NUMERIC；
      3) 已登记语义的字段，F_TYPE 与物理类型都必须属于数值类
         （F_TYPE 是展示类型，nvarchar 对 nchar、date 对 datetime 属正常，不做逐字相等）。

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

    if ($problems.Count -eq 0) {
        Write-Output 'PASS 语义登记完整、枚举合法、F_TYPE 与物理类型一致'
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
