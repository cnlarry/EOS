<#
.SYNOPSIS
    可用量一致性常态门禁：逐键断言 `USEABLE_QTY = QTY − Σ(有效冻结) − Σ(有效预留)`
    （ADR-020 §9.7 D7-②/⑧、§10 WS-25）。

.DESCRIPTION
    可用量是**存列**的（每行冗余快照）：换来出库校验在移动引擎里免于实时聚合的代价，
    代价则是**一定会分叉**——只要有一条写入路径忘了同步，那一行就永远偏着，
    而且偏的方向恰好是"多出可出的量"：出库校验会放行本该被冻结拦住的货，**没人会发现**。
    本脚本把那句话变成可复算的机器断言：

      ① 逐键恒等——对 `INV_PRO_DEPOT` 的每一行（四键：料号 / 库别 / 库位 / 批次）断言
         `USEABLE_QTY = QTY − Σ(INV_FREEZE.FREEZE_QTY + INV_RESERVE.RESERVE_QTY)`，
         占用只计 `STATUS = 'A'` 的行（`'C'` 是已取消 / 已释放，服务侧同样不计入）。
         差异大于容差即 FAIL。**棘轮只减不增**：基线写在本脚本里，出现基线之外的差异键即 FAIL。
      ② 可用量列不得为 NULL——NULL 会让恒等式两侧一起变成"空"，是最容易被吞掉的一类分叉。
      ③ 占用行卫生——`STATUS` 只允许 `A` / `C`，数量列不得为负；取值跑偏只可能来自直连改库。

    **一处刻意不做的豁免**：服务侧对"来源单据已结案"的占用做**惰性判定**
    （不要求释放钩子一定跑到），本脚本按 `STATUS='A'` 全计入，两口径只在
    "来源已结案、但仍挂着 A 状态"的行上分叉——**那种行正是要抓的**（D7-⑥：来源取消 / 关闭即释放），
    所以这里不豁免：命中即 FAIL，处置是**去释放**，不是来放宽门禁。

    **三处同源**（D7-⑧）：出库校验读的、报表与选择器下发的、本门禁断言的，都是同一列；
    口径的**唯一出口**是 `EOS.API/Data/Inventory/InventoryAvailabilityService.cs`，
    读取侧只**下发**它物化的值（`InventoryQueryService` 连同列一起给），不自己聚合占用表。

    只读：脚本不修改任何数据（`-SelfTest` 造的合成行在 finally 里删干净）。

.PARAMETER ConnectionString
    可选连接串；默认取 MSSQL_ERP_CONN（与仓库其它脚本一致）。

.PARAMETER SelfTest
    正反自检：造一行"可用量偏 10"的合成数据，断言检测器**命中**；再撤掉占用，断言**不再命中**。
    用于证明门禁确实会失败，而不是永远绿灯。

.EXAMPLE
    pwsh scripts/check-inventory-availability.ps1
    pwsh scripts/check-inventory-availability.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string] $ConnectionString,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

. (Join-Path $PSScriptRoot 'dev/eos-sql.ps1')

$tolerance = 0.0001

# 棘轮基线（只减不增）：差异键数 / 可用量 NULL 行数 / 占用行取值跑偏数。
# 立项时三项都为 0（余额 1797 行、有效占用 0 行）——这就是"存列可被信任"的起点。
$mismatchRatchet = 0
$nullUsableRatchet = 0
$badOccupancyRatchet = 0

# 自检用的合成身份（前缀自带归属，清理按前缀收敛）
$probeProduct = 'ZZAVGATE1'
$probeDepot = 'CP'
$probeLocation = '-'
$probeBatch = ''

$problems = [System.Collections.Generic.List[string]]::new()

$mismatchSql = @'
SET NOCOUNT ON;
WITH occ AS (
    SELECT LTRIM(RTRIM(PRO_NO)) P, LTRIM(RTRIM(DEPOT_ID)) D, LTRIM(RTRIM(ISNULL(LOCATION_NO, N'-'))) L,
           LTRIM(RTRIM(ISNULL(BATCH_NO, N''))) B, ISNULL(FREEZE_QTY, 0) Q
      FROM dbo.INV_FREEZE WHERE LTRIM(RTRIM(ISNULL(STATUS, N'C'))) = N'A'
    UNION ALL
    SELECT LTRIM(RTRIM(PRO_NO)), LTRIM(RTRIM(DEPOT_ID)), LTRIM(RTRIM(ISNULL(LOCATION_NO, N'-'))),
           LTRIM(RTRIM(ISNULL(BATCH_NO, N''))), ISNULL(RESERVE_QTY, 0)
      FROM dbo.INV_RESERVE WHERE LTRIM(RTRIM(ISNULL(STATUS, N'C'))) = N'A'
), agg AS (SELECT P, D, L, B, SUM(Q) OCC FROM occ GROUP BY P, D, L, B)
SELECT LTRIM(RTRIM(d.PRO_NO)) + N'|' + LTRIM(RTRIM(d.DEPOT_ID)) + N'|'
     + LTRIM(RTRIM(ISNULL(d.LOCATION_NO, N'-'))) + N'|' + LTRIM(RTRIM(ISNULL(d.BATCH_NO, N'')))
     + N'|数量=' + CONVERT(nvarchar(40), ISNULL(d.QTY, 0))
     + N'|可用量=' + CONVERT(nvarchar(40), ISNULL(d.USEABLE_QTY, 0))
     + N'|占用=' + CONVERT(nvarchar(40), ISNULL(a.OCC, 0))
     + N'|应为=' + CONVERT(nvarchar(40), ISNULL(d.QTY, 0) - ISNULL(a.OCC, 0))
  FROM dbo.INV_PRO_DEPOT d
  LEFT JOIN agg a ON a.P = LTRIM(RTRIM(d.PRO_NO)) AND a.D = LTRIM(RTRIM(d.DEPOT_ID))
                 AND a.L = LTRIM(RTRIM(ISNULL(d.LOCATION_NO, N'-')))
                 AND a.B = LTRIM(RTRIM(ISNULL(d.BATCH_NO, N'')))
 WHERE ABS(ISNULL(d.USEABLE_QTY, 0) - (ISNULL(d.QTY, 0) - ISNULL(a.OCC, 0))) > 0.0001
 ORDER BY 1;
'@

$nullUsableSql = "SET NOCOUNT ON; SELECT CONVERT(nvarchar(20), COUNT(*)) FROM dbo.INV_PRO_DEPOT WHERE USEABLE_QTY IS NULL;"
$badOccupancySql = @'
SET NOCOUNT ON;
SELECT CONVERT(nvarchar(20), COUNT(*)) FROM (
    SELECT STATUS, FREEZE_QTY AS Q FROM dbo.INV_FREEZE
    UNION ALL
    SELECT STATUS, RESERVE_QTY FROM dbo.INV_RESERVE
) o
 WHERE LTRIM(RTRIM(ISNULL(o.STATUS, N'C'))) NOT IN (N'A', N'C') OR ISNULL(o.Q, 0) < 0;
'@

function Get-Mismatches {
    param([string] $Probe = '')
    $rows = @(Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query $mismatchSql | ForEach-Object { "$_".Trim() })
    if ([string]::IsNullOrEmpty($Probe)) { return $rows }
    return @($rows | Where-Object { $_ -like "$Probe|*" })
}

function Initialize-Probe {
    Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query @"
SET NOCOUNT ON;
DELETE FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = N'$probeProduct';
DELETE FROM dbo.INV_FREEZE WHERE LTRIM(RTRIM(PRO_NO)) = N'$probeProduct';
DELETE FROM dbo.PRODUCT WHERE LTRIM(RTRIM(PRO_NO)) = N'$probeProduct';
INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, PRO_TYPE) VALUES (N'$probeProduct', N'可用量门禁自检件', '3');
-- 可用量**故意偏 10**：数量 100、可用量 100，却挂着 10 的有效冻结
INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, QTY, INIT_QTY, USEABLE_QTY)
    VALUES (N'$probeProduct', N'$probeDepot', N'$probeLocation', N'$probeBatch', 100, 100, 100);
INSERT INTO dbo.INV_FREEZE (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, SOURCE_TYPE, SOURCE_NO, FREEZE_QTY, STATUS, REASON)
    VALUES (N'$probeProduct', N'$probeDepot', N'$probeLocation', N'$probeBatch', N'', N'', 10, N'A', N'门禁自检');
"@ | Out-Null
}

function Clear-Probe {
    Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query @"
SET NOCOUNT ON;
DELETE FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = N'$probeProduct';
DELETE FROM dbo.INV_FREEZE WHERE LTRIM(RTRIM(PRO_NO)) = N'$probeProduct';
DELETE FROM dbo.PRODUCT WHERE LTRIM(RTRIM(PRO_NO)) = N'$probeProduct';
"@ | Out-Null
}

$selfTestNotes = [System.Collections.Generic.List[string]]::new()
if ($SelfTest) {
    try {
        Clear-Probe
        Initialize-Probe

        # 正向：可用量偏 10 ⇒ 检测器**必须**命中这一键
        $hit = @(Get-Mismatches -Probe $probeProduct)
        if ($hit.Count -ne 1) {
            $problems.Add("自检正向失败：人为把可用量偏 10 后，检测器命中 $($hit.Count) 行（应为 1）")
        } else {
            $selfTestNotes.Add("自检正向 PASS：可用量偏 10 被命中（$($hit[0])）")
        }

        # 反向：撤掉占用 ⇒ 同一键**不再**命中（证明命中的是恒等式本身，不是"随便报一行"）
        Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query `
            "SET NOCOUNT ON; DELETE FROM dbo.INV_FREEZE WHERE LTRIM(RTRIM(PRO_NO)) = N'$probeProduct';" | Out-Null
        $after = @(Get-Mismatches -Probe $probeProduct)
        if ($after.Count -ne 0) {
            $problems.Add("自检反向失败：撤掉占用后仍命中 $($after.Count) 行（应为 0）")
        } else {
            $selfTestNotes.Add('自检反向 PASS：撤掉占用后不再命中')
        }
    } finally {
        Clear-Probe
        $left = @(Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query `
            "SET NOCOUNT ON; SELECT CONVERT(nvarchar(20), COUNT(*)) FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(PRO_NO)) = N'$probeProduct';")
        if (@($left | Where-Object { "$_".Trim() -ne '0' }).Count -gt 0) {
            $problems.Add("自检清理失败：合成行没删干净（$($left -join ',')）")
        } else {
            $selfTestNotes.Add('自检清理 PASS：合成行已删干净')
        }
    }
}

# ① 逐键恒等
$mismatches = @(Get-Mismatches)
if ($mismatches.Count -gt $mismatchRatchet) {
    $problems.Add("可用量恒等式有 $($mismatches.Count) 个差异键（棘轮上限 $mismatchRatchet；差异键只减不增）")
    foreach ($row in ($mismatches | Select-Object -First 10)) { Write-Host "  [差异] $row" }
} else {
    Write-Host "  [PASS] ① 逐键恒等：可用量 = 数量 − Σ有效冻结 − Σ有效预留（差异键 $($mismatches.Count)/棘轮 $mismatchRatchet）"
    if ($mismatches.Count -lt $mismatchRatchet) {
        Write-Host "  [INFO] 棘轮可减：本期差异键 $($mismatches.Count) 个（把 `$mismatchRatchet 调到这个数）"
    }
}

# ② 可用量列不得为 NULL
$nullUsable = [int](@(Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query $nullUsableSql)[0].Trim())
if ($nullUsable -gt $nullUsableRatchet) {
    $problems.Add("可用量为 NULL 的行有 $nullUsable 行（棘轮上限 $nullUsableRatchet）")
} else {
    Write-Host "  [PASS] ② 可用量列无 NULL（$nullUsable/棘轮 $nullUsableRatchet）"
}

# ③ 占用行卫生
$badOccupancy = [int](@(Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query $badOccupancySql)[0].Trim())
if ($badOccupancy -gt $badOccupancyRatchet) {
    $problems.Add("占用行取值跑偏 $badOccupancy 行（STATUS 只允许 A/C、数量不得为负；棘轮上限 $badOccupancyRatchet）")
} else {
    Write-Host "  [PASS] ③ 占用行卫生：STATUS 取值合法、数量非负（$badOccupancy/棘轮 $badOccupancyRatchet）"
}

foreach ($note in $selfTestNotes) { Write-Host "  [PASS] $note" }

if ($problems.Count -gt 0) {
    Write-Output ''
    foreach ($problem in $problems) { Write-Output "  [FAIL] $problem" }
    Write-Output ''
    Write-Output '-- FAIL'
    exit 1
}

Write-Output ''
Write-Output 'PASS 可用量一致性：逐键恒等差异 = 0、可用量列无 NULL、占用行卫生合法（三项棘轮只减不增）'
exit 0
