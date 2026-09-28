<#
月结快照对账门禁（ADR-020 §6 P2-2 / §10 WS-12）

判据（视图 `dbo.V_INV_MONTH_SNAPSHOT_RECON` 的口径，见迁移 `237`）：
  最近一期**已批核**月结快照 + 该期期末之后的流水净额 == 实时余额？
  视图只输出差异行，所以"对完一致"= 0 行。

**"0 行"有两种含义，本门禁绝不混淆**：
  · 有已批核快照且 0 行  ⇒ PASS（比过，且逐键一致）；
  · **没有**已批核快照   ⇒ **N/A**（没有可比的对象）——这不是通过，也不计入覆盖。

用法：
  pwsh scripts/check-month-snapshot-recon.ps1              # 对真库跑
  pwsh scripts/check-month-snapshot-recon.ps1 -SelfTest    # 判定逻辑的正反自检（不连库）

判别性证据不在本脚本里，而在真库用例 `EOS.API.Tests/MonthSnapshotReconLiveTests.cs`：
人为改一行余额 ⇒ 视图**恰好**报出那一个键；期末当天的流水改成按时间比较 ⇒ 差异集多出一个键。
#>
[CmdletBinding()]
param(
    [switch]$SelfTest,
    [int]$SampleLimit = 20
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# ---------- 判定（纯函数，自检与真库共用同一处） ----------
function Test-SnapshotRecon {
    param(
        [Parameter(Mandatory)][bool]$HasConfirmedSnapshot,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$DiffRows
    )

    $problems = [System.Collections.Generic.List[string]]::new()
    if (-not $HasConfirmedSnapshot) {
        return @{
            Verdict  = 'N/A'
            Problems = $problems
        }
    }
    if ($DiffRows.Count -gt 0) {
        $problems.Add("快照对账不平：$($DiffRows.Count) 个键的『快照 + 其后流水』与实时余额不一致。")
        foreach ($row in $DiffRows) {
            $problems.Add("  键 $($row.DepotId)/$($row.ProNo)：快照 $($row.SnapQty) + 其后流水 $($row.AfterLedgerQty) " +
                "- 实时余额 $($row.LiveQty) = 差 $($row.DiffQty)")
        }
    }
    return @{
        Verdict  = if ($DiffRows.Count -gt 0) { 'FAIL' } else { 'PASS' }
        Problems = $problems
    }
}

if ($SelfTest) {
    Write-Host '== 自检：判定逻辑 =='
    $clean = @(Test-SnapshotRecon -HasConfirmedSnapshot $true -DiffRows @())
    if ($clean.Verdict -ne 'PASS' -or $clean.Problems.Count -ne 0) { throw '自检失败：一致的账应当是 PASS 且无问题。' }

    $dirty = @(Test-SnapshotRecon -HasConfirmedSnapshot $true -DiffRows @(
            [pscustomobject] @{ DepotId = 'CP'; ProNo = 'X1'; SnapQty = 10; AfterLedgerQty = 2; LiveQty = 5; DiffQty = 7 }
        ))
    if ($dirty.Verdict -ne 'FAIL') { throw '自检失败：不平的账必须是 FAIL。' }
    if ($dirty.Problems.Count -ne 2) { throw "自检失败：问题清单 = 结论行 + 每个键一行，实际 $($dirty.Problems.Count) 条。" }
    if ($dirty.Problems[1] -notmatch 'X1') { throw '自检失败：问题清单未点出不平的键。' }

    $none = @(Test-SnapshotRecon -HasConfirmedSnapshot $false -DiffRows @())
    if ($none.Verdict -ne 'N/A') { throw '自检失败：没有已批核快照时必须是 N/A——0 行不等于通过。' }

    Write-Host "  PASS：三项正反自检通过（一致⇒PASS / 不平⇒FAIL且点名 / 无快照⇒N/A）"
    exit 0
}

. (Join-Path $PSScriptRoot 'dev/eos-sql.ps1')

$probe = @'
SET NOCOUNT ON;
SELECT CONCAT('confirmed=', (SELECT COUNT(*) FROM dbo.INV_PRO_MONTH_M WHERE CONFIRM_TAG = 1),
              '|period=', ISNULL((SELECT TOP 1 RTRIM(MONTH_TYPE) + '/' + RTRIM(MONTH_NO) + '@' + CONVERT(varchar(10), MONTH_DATE, 120)
                                    FROM dbo.INV_PRO_MONTH_M WHERE CONFIRM_TAG = 1 ORDER BY MONTH_DATE DESC, MONTH_NO DESC), ''));
'@
$head = @(Invoke-EosSqlQuery -Query $probe)[0]
$parts = @{}
foreach ($pair in $head.Split('|')) {
    $kv = $pair.Split('=', 2)
    $parts[$kv[0]] = $kv[1]
}
$confirmed = [int]$parts['confirmed']
$period = $parts['period']

Write-Host "== 月结快照对账（视图 dbo.V_INV_MONTH_SNAPSHOT_RECON）=="
if ($confirmed -eq 0) {
    Write-Host 'N/A 没有已批核的月结单 ⇒ 无快照可比（这不是通过，是不适用）。'
    Write-Host '    覆盖范围：视图需要至少一期已批核月结快照；生成快照见模块 1304 的生成按钮。'
    exit 0
}

Write-Host "  对账期：$period"
$rows = @()
foreach ($line in @(Invoke-EosSqlQuery -Query @"
SET NOCOUNT ON;
SELECT TOP $SampleLimit CONCAT(RTRIM(DEPOT_ID), '|', RTRIM(PRO_NO), '|', CONVERT(varchar(40), CAST(SNAP_QTY AS decimal(28,4))),
              '|', CONVERT(varchar(40), CAST(AFTER_LEDGER_QTY AS decimal(28,4))),
              '|', CONVERT(varchar(40), CAST(LIVE_QTY AS decimal(28,4))),
              '|', CONVERT(varchar(40), CAST(DIFF_QTY AS decimal(28,4))))
  FROM dbo.V_INV_MONTH_SNAPSHOT_RECON ORDER BY ABS(DIFF_QTY) DESC;
"@)) {
    $f = $line.Split('|')
    $rows += [pscustomobject] @{ DepotId = $f[0]; ProNo = $f[1]; SnapQty = $f[2]; AfterLedgerQty = $f[3]; LiveQty = $f[4]; DiffQty = $f[5] }
}
$total = [int](@(Invoke-EosSqlQuery -Query 'SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.V_INV_MONTH_SNAPSHOT_RECON;')[0])
$result = Test-SnapshotRecon -HasConfirmedSnapshot $true -DiffRows $rows

if ($result.Verdict -eq 'PASS') {
    Write-Host 'PASS 快照 + 其后流水 与 实时余额逐键一致（差异 0 行）。'
    Write-Host '    覆盖范围：全库余额键（视图逐键比对，不是抽样）。'
    exit 0
}

foreach ($problem in $result.Problems) { Write-Host $problem }
if ($total -gt $rows.Count) { Write-Host "  （仅列出差异最大的 $($rows.Count) 个键，共 $total 个）" }
exit 1
