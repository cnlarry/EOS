#Requires -Version 7.0
<#
.SYNOPSIS
    连库门禁：FIELDS.F_TYPE 必须与物理列的数值族一致（声明得比物理宽，脏值会绕过校验变成 500）。

.DESCRIPTION
    背景（2026-10-17 实测）：模块 18010110 的 `BED_SUM` 物理是 `smallint`，`F_TYPE` 却写成 `float`。
    校验器按 **F_TYPE** 选转换分支——`float` 走 `double.TryParse`（40 位数字合法、且不做范围校验），
    `smallint` 走 `int.Parse`（溢出 → 可读的 400 `INVALID_VALUE`）。于是"声明比物理宽"的列，
    任何越界值都能绕过校验漏到 SQL，再以 `500 算术溢出` 收场。
    迁移 405 收的是"字符串族 → 数值/日期/bit"，迁移 406 收的是数值族内部；本门禁防止**再次配错**。

    判据只覆盖**族别不同**的四种组合（float/real/money ↔ 整型/定点），
    `decimal` 与 `numeric` 是同义别名不算不一致；字符串族与数值/日期族的比较见迁移 405 的断言，
    这里不重复（避免两处判据漂移时互相打架）。

    连库运行，不在 CI 的静态检查里——CI 没有数据库。本地按需运行，或由收口脚本串起来。

.PARAMETER SelfTest
    判别性自检：用合成样本对驱动同一条判据，合规必须放行、违规必须命中。
    判据本身是**唯一真源**（同一个 SQL 片段既跑真实库也跑样本），不做两份实现。

.EXAMPLE
    pwsh scripts/check-field-type-alignment.ps1
    pwsh scripts/check-field-type-alignment.ps1 -SelfTest
#>
param(
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'

<#
  判据（唯一真源）：{0} 是"声明类型"表达式，{1} 是"物理类型"表达式。
  格式化成两处使用时，除占位符外完全相同——这样自检通过就代表真实库用的也是同一条判据。
#>
$script:MismatchPredicate = @"
(
  (LTRIM(RTRIM(ISNULL({0},''))) IN ('float','real','money','smallmoney')
   AND {1} IN ('smallint','int','bigint','tinyint','decimal','numeric'))
  OR
  (LTRIM(RTRIM(ISNULL({0},''))) IN ('smallint','int','bigint','tinyint','decimal','numeric')
   AND {1} IN ('float','real','money','smallmoney'))
)
AND NOT (LTRIM(RTRIM(ISNULL({0},'')))='numeric' AND {1}='decimal')
AND NOT (LTRIM(RTRIM(ISNULL({0},'')))='decimal' AND {1}='numeric')
"@

function Format-Predicate([string] $Declared, [string] $Physical) {
    return $script:MismatchPredicate -f $Declared, $Physical
}

try {
    [Console]::OutputEncoding = [Text.Encoding]::UTF8
    $root = Split-Path -Parent $PSScriptRoot
    . (Join-Path $PSScriptRoot 'dev/eos-sql.ps1')

    if ($SelfTest) {
        # 合成样本：真实库上现在可能是 0 条，光看"0 条"证明不了判据有效
        $cases = @(
            @{ Declared = 'float'; Physical = 'smallint'; Expect = $true },
            @{ Declared = 'smallint'; Physical = 'float'; Expect = $true },
            @{ Declared = 'money'; Physical = 'decimal'; Expect = $true },
            @{ Declared = 'decimal'; Physical = 'numeric'; Expect = $false },
            @{ Declared = 'numeric'; Physical = 'decimal'; Expect = $false },
            @{ Declared = 'nvarchar'; Physical = 'smallint'; Expect = $false },
            @{ Declared = 'int'; Physical = 'int'; Expect = $false }
        )
        $rows = ($cases | ForEach-Object {
            "('{0}','{1}')" -f $_.Declared, $_.Physical
        }) -join ','
        $sql = @"
SET NOCOUNT ON;
DECLARE @c TABLE (declared sysname, physical sysname);
INSERT @c VALUES $rows;
SELECT CONCAT(c.declared, '/', c.physical)
FROM @c c WHERE $(Format-Predicate 'c.declared' 'c.physical');
"@
        $hits = @(Invoke-EosSqlQuery -Query $sql | ForEach-Object { $_.Trim() })
        $expected = @($cases | Where-Object { $_.Expect } | ForEach-Object { "$($_.Declared)/$($_.Physical)" })
        $missing = @($expected | Where-Object { $hits -notcontains $_ })
        $extra = @($hits | Where-Object { $expected -notcontains $_ })
        if ($missing.Count -gt 0 -or $extra.Count -gt 0) {
            Write-Output "FAIL 自检：漏判 [$($missing -join ', ')] 误判 [$($extra -join ', ')]"
            exit 1
        }
        Write-Output "PASS 自检：$($expected.Count) 条应命中、$($cases.Count - $expected.Count) 条应放行，结论全对"
        exit 0
    }

    $predicate = Format-Predicate 'f.F_TYPE' 'TYPE_NAME(c.user_type_id)'
    $sql = @"
SET NOCOUNT ON;
SELECT CONCAT(LTRIM(RTRIM(f.T_ID)), '.', LTRIM(RTRIM(f.F_ID)),
              '  声明=', LTRIM(RTRIM(ISNULL(f.F_TYPE, '<空>'))),
              '  物理=', TYPE_NAME(c.user_type_id),
              '(', c.precision, ',', c.scale, ')')
  FROM dbo.FIELDS f
  JOIN sys.columns c
    ON c.object_id = OBJECT_ID('dbo.' + LTRIM(RTRIM(f.T_ID)))
   AND c.name = LTRIM(RTRIM(f.F_ID))
 WHERE $predicate
 ORDER BY f.T_ID, f.F_ID;
"@
    $offenders = @(Invoke-EosSqlQuery -Query $sql | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
    if ($offenders.Count -gt 0) {
        Write-Output "FAIL F_TYPE 与物理列数值族不一致（声明比物理宽，越界值会绕过校验变成 500），共 $($offenders.Count) 处："
        $offenders | Select-Object -First 20 | ForEach-Object { Write-Output "  $_" }
        if ($offenders.Count -gt 20) { Write-Output "  …… 其余 $($offenders.Count - 20) 处略" }
        Write-Output '  修法：迁移把 F_TYPE 对齐到物理列类型（见 405 / 406），并对受影响模块标脏 + 重发布。'
        exit 1
    }

    Write-Output 'PASS F_TYPE 与物理列的数值族一致'
    exit 0
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
