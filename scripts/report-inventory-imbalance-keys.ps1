<#
.SYNOPSIS
    逐键定性库存不平键（余额对流水累计），并回答「是否仍在扩大」。

.DESCRIPTION
    `check-inventory-balance-identity.ps1` 只回答"平不平"，本脚本回答"为什么不平、还在不在变大"。

    判定规则（写死，可机器复核）：

      · **是否仍在扩大**：以"当前差异 − 该键在某切点之后的净动账"反推该切点的差异。
        若 `|该切点的差异| < |当前差异|`，说明切点之后的动账把它做大了 ⇒ **仍在扩大**。
        切点默认 2018-12-31（实测最后一批大额动账所在年份）；可 `-Cutoff` 覆盖。
      · **夹具残留**：料号以 `E2E` 开头（项目验收/端到端造数的料号前缀约定）。
      · **历史遗留**：以上都不是，即差异自切点起未被做大（或该键自切点起没有动账）。

    **口径局限（必须连着结论一起读）**：余额表只有"当前余额"，没有历史余额，
    所以"切点处的差异"是用 `当前余额 − 截至切点的流水` 反推的。它能证明
    "切点之后的新动账没有把差异做大"，**不能**证明"每一笔新单据本身都是平的"
    ——后者由对账门禁的棘轮（清单外新增即红）负责。

.PARAMETER Cutoff
    反推用时间切点，默认 `2018-12-31`。

.PARAMETER ConnectionString
    可选连接串；默认取 `MSSQL_ERP_CONN`。

.PARAMETER Markdown
    输出 Markdown 表格（供报告引用）而不是人读格式。

.EXAMPLE
    pwsh scripts/report-inventory-imbalance-keys.ps1 -Markdown > 逐键定性.md
#>
[CmdletBinding()]
param(
    [string] $Cutoff = '2018-12-31',
    [string] $ConnectionString,
    [switch] $Markdown
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

. (Join-Path $PSScriptRoot 'dev/eos-sql.ps1')

$tolerance = 0.0001
# 测试造数的料号前缀：这些键的差异由验收/端到端脚本留下，不是业务单据造成
$fixturePrefix = 'E2E'

$sql = @"
SET NOCOUNT ON;
WITH bal AS (SELECT DEPOT_ID, PRO_NO, SUM(QTY) AS B FROM dbo.INV_PRO_DEPOT GROUP BY DEPOT_ID, PRO_NO),
led AS (SELECT DEPOT_ID, PRO_NO,
               SUM(CASE WHEN IN_OUT = 'I' THEN ISNULL(QTY,0) ELSE -ISNULL(QTY,0) END) AS L,
               MAX(MUTUALITY_DATE) AS LAST_DT
          FROM dbo.INV_DEPOT_LOG GROUP BY DEPOT_ID, PRO_NO),
k AS (SELECT COALESCE(b.PRO_NO, l.PRO_NO) AS PRO_NO, COALESCE(b.DEPOT_ID, l.DEPOT_ID) AS DEPOT_ID,
             ISNULL(b.B,0) - ISNULL(l.L,0) AS DIFF,
             ISNULL(l.LAST_DT, CONVERT(datetime,'1900-01-01')) AS LAST_DT
        FROM bal b FULL OUTER JOIN led l ON l.PRO_NO = b.PRO_NO AND l.DEPOT_ID = b.DEPOT_ID),
n AS (SELECT DEPOT_ID, PRO_NO,
             SUM(CASE WHEN IN_OUT = 'I' THEN ISNULL(QTY,0) ELSE -ISNULL(QTY,0) END) AS NET
        FROM dbo.INV_DEPOT_LOG WHERE MUTUALITY_DATE > '$Cutoff' GROUP BY DEPOT_ID, PRO_NO)
SELECT CONCAT(RTRIM(k.PRO_NO), '|', RTRIM(k.DEPOT_ID), '|',
              CONVERT(NVARCHAR(30), CAST(k.DIFF AS DECIMAL(28,2))), '|',
              CONVERT(NVARCHAR(10), k.LAST_DT, 120), '|',
              CONVERT(NVARCHAR(30), CAST(ISNULL(n.NET, 0) AS DECIMAL(28,2))))
  FROM k LEFT JOIN n ON n.PRO_NO = k.PRO_NO AND n.DEPOT_ID = k.DEPOT_ID
 WHERE ABS(k.DIFF) > $tolerance
 ORDER BY ABS(k.DIFF) DESC, RTRIM(k.PRO_NO);
"@

$keys = foreach ($line in @(Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query $sql)) {
    $parts = "$line".Trim() -split '\|'
    if ($parts.Count -ne 5) { continue }
    $diff = [double]::Parse($parts[2], [Globalization.CultureInfo]::InvariantCulture)
    $net = [double]::Parse($parts[4], [Globalization.CultureInfo]::InvariantCulture)
    $then = $diff - $net
    $verdict, $advice = if ($parts[0] -like "$fixturePrefix*") {
        '夹具残留', '随该批验收/端到端造数一起清理（这些键不是业务单据造成的）'
    }
    elseif ([math]::Abs($then) -lt [math]::Abs($diff)) {
        '仍在扩大', "该键自 $Cutoff 起的动账仍在加大差异（$([math]::Round($then,2)) → $([math]::Round($diff,2))）：先查该键的最近单据"
    }
    else {
        '历史遗留', "自 $Cutoff 起未再变大（该切点差异 $([math]::Round($then,2)) → 当前 $([math]::Round($diff,2))）：按期初重述处理，不在本脚本内改数据"
    }
    [pscustomobject]@{
        料号 = $parts[0]; 库别 = $parts[1]; 差异 = $diff; 最后活动 = $parts[3]
        切点差异 = [math]::Round($then, 2); 定性 = $verdict; 处置建议 = $advice
    }
}

$keys = @($keys)

if ($Markdown) {
    Write-Output "| 料号 | 库别 | 差异 | 最后活动 | 定性 | $Cutoff 处差异 |"
    Write-Output '| --- | --- | ---: | --- | --- | ---: |'
    foreach ($key in $keys) {
        Write-Output ("| {0} | {1} | {2} | {3} | {4} | {5} |" -f `
            $key.料号, $key.库别, $key.差异, $key.最后活动, $key.定性, $key.切点差异)
    }
    exit 0
}

Write-Host "== 不平键 $($keys.Count) 个（切点 $Cutoff，阈值 $tolerance）=="
foreach ($group in ($keys | Group-Object 定性)) {
    $sum = ($group.Group | Measure-Object 差异 -Sum).Sum
    Write-Host ("  {0}：{1} 键，差异合计 {2}" -f $group.Name, $group.Count, [math]::Round($sum, 2))
}

$grew = @($keys | Where-Object { $_.定性 -eq '仍在扩大' })
Write-Host ''
Write-Host ("== 是否仍在扩大：{0} ==" -f $(if ($grew.Count -eq 0) { '否' } else { "是（$($grew.Count) 键）" }))
Write-Host ("  全部不平键的 |差异| 合计：当前 {0}" -f [math]::Round((($keys | ForEach-Object { [math]::Abs($_.差异) } | Measure-Object -Sum).Sum), 2))
Write-Host ("  同一组键在 $Cutoff 处的 |差异| 合计：{0}" -f [math]::Round((($keys | ForEach-Object { [math]::Abs($_.切点差异) } | Measure-Object -Sum).Sum), 2))
if ($grew.Count -gt 0) {
    Write-Host '  仍在扩大的键：'
    foreach ($key in $grew) { Write-Host "    $($key.料号)|$($key.库别)  $($key.切点差异) → $($key.差异)" }
}
