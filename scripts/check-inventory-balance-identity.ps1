<#
.SYNOPSIS
    库存账务自证门禁：余额逐键对流水累计，报表口径必须恒平，全库口径以棘轮冻结已知不平键。

.DESCRIPTION
    库存账"对不对"要能被系统回答，而不是靠人肉比对。本门禁是最小自证闭环：
    以 **(料号, 库别)** 为键，余额（`INV_PRO_DEPOT` 的 `SUM(QTY)`）对流水累计
    （`INV_DEPOT_LOG`，`IN_OUT='I'` 为正、`'O'` 为负）逐键比对。

    **执行顺序固定，不得调整**：

      ① `INV_DEPOT_LOG` 的 `QTY IS NULL` 行数。对账算式
         `SUM(CASE WHEN IN_OUT='I' THEN QTY ELSE -QTY END)` **不是 NULL 安全的**——
         `QTY IS NULL` 会让该行取值为 NULL，而 `SUM` **静默忽略**它（不报错、不留痕）。
         不先断这一类，"流水缺数量"与"账实不符"会被压成同一个数字，报错指向错误的根因。
         · 报表档内必须为 **0**（硬断言）；
         · 全库以棘轮基线冻结（只减不增）。
      ② `IN_OUT` 只能取 `I` / `O`。其它取值（含 NULL）即 FAIL——对账算式会把非 `I` 的一律当出库。
      ③ 逐键对账，分两档：
         · **报表档**（料件类别 `3` 原料 / `2` 半成品）：差异必须恒为 **0**，出现任何差异即 FAIL。
           这一档覆盖库存报表实际取数的料件，是跑得通、有意义的回归防线。
           报表本体只取 `'3'`，门禁取 `'2'`+`'3'` 的并集（更严，且两者当前差异均为 0）。
         · **全库档**：已知不平键以显式清单冻结（棘轮，只减不增）。出现清单外的不平键即 FAIL；
           清单内的键本期已平则提示"棘轮可减"。

    **覆盖范围必须被说清**：报表档只是全库的一小部分，而差异全部落在成品与主档缺失料号上——
    恰好是报表档**覆盖不到**的那一块。覆盖不到可以，装作覆盖到了不行：故全库档必须一直存在，
    且 FAIL 文案里必须写明它含未定性差异。

    **同源口径**：第 ③ 步的余额与流水算式与库存日报（`EOS.API/Data/ReportAggregateRegistry.cs`
    的 `InventoryDaily`）逐字同源，避免"报表算一套、门禁算另一套"。差异阈值 `0.0001`
    （数量精度远高于此，用于吸收浮点求和的末位噪声）。

.PARAMETER Root
    仓库根；默认由脚本位置推导。

.PARAMETER ConnectionString
    可选连接串；默认取 `MSSQL_ERP_CONN`（与仓库其它脚本一致）。

.PARAMETER SelfTest
    正反自检（**不连库**）：用合成数据分别命中三类断言（① NULL 数量 ② 方向越界 ③ 逐键不平），
    并断言干净数据放行、报表档不被全库档棘轮豁免、棘轮可减会被提示。

.EXAMPLE
    pwsh scripts/check-inventory-balance-identity.ps1            # exit 0 = 账可自证
    pwsh scripts/check-inventory-balance-identity.ps1 -SelfTest  # 正反自检
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

# 报表档料件类别：2 半成品 / 3 原料（库存报表取数口径见 InventorySources.DistinctProductDepotPairs）
$reportTierProTypes = @('2', '3')
$balanceTolerance = 0.0001

# 棘轮基线：`INV_DEPOT_LOG` 中 `QTY IS NULL` 的行数上限。只减不增——
# 新增的 NULL 数量流水必须先定性（它们不参与对账，是"看不见"的一类缺陷）。
$nullQuantityRatchet = 0

# 棘轮：全库档已知不平键（当前 54 个）。**含未定性差异**，只减不增——
# 键经过定性并修正后应从本清单移除；出现清单外的不平键即 FAIL。
$frozenUnbalancedKeys = @'
0324020.MXP|BF
AHW5BMAT001|CP
AHW5BPRO001|CP
APF3BPRO001|CP
APF3BPRO001|PJ
ARH6BMAT001|CP
ARH6BPRO001|CP
AZK2BPRO001|CP
BL-3030-30|PJ
C2051PCB02-3P|BF
DM-010|PJ
DPS-0502000C|CP
G1003-150P03A|BF
H15-05SZ-200|BF
H15-10SN02-040|BF
H15-10SN07-050|BF
H15-10SP01-050|BF
H15-10SP-150-33|BF
H15-10SP21-050H|BF
H15-10SZ10-030|BF
H15-10SZ-ASSY-02|BF
H15-10SZ-ASSY-03|BF
H15-10SZ-ASSY-04|BF
H20-16AP01-150-ASSY|BF
H20-16AP-150C|BF
H20-16EN-020|BF
H20-16EP02-015|BF
H20-16GP-150C|BF
H20-16GZ03-070C|BF
H20-16HP-200|BF
H20-16HP-200H|BF
H25-1022P1B|BF
H25-1623SA-Q5|BF
HF-505|PJ
HJP-FR1-01|BF
HS615S|BF
HS615S|PJ
KW10-Z0P150附3号动臂|CP
PB-22E60N|PJ
PBM12-11M-CBA|BF
PBM-16Z15-FS-EU3-S7-S|PJ
RCA-1113-0-ACA11-05|CP
RCA-1113-11B-23|CP
RV09712NM-FB15A7|BF
TS-2-2416-CT|BF
TVDM04-050BB-R-10|BF
V15T16-E1P200|BF
V15T16-S1P400|CP
V15T22-C1P300A05-03|BF
V15T22-C1Z200-01|BF
XDE1YPRO001|BF
XDE1YPRO001|CP
XDE1YPRO001|PJ
YSS-16-2216-CRW|BF
'@ -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '\S' }

function Test-LedgerNullQuantity {
    <#
      第 ① 步：流水缺数量。报表档硬 0，全库棘轮。
    #>
    param([int] $NullRows, [int] $NullRowsInReportTier, [int] $Ratchet)

    $problems = New-Object System.Collections.Generic.List[string]
    # 哨兵 -1 = 探针没取到值。下面两条判据都是"**大于**基线才 FAIL"，哨兵会静默满足它们，
    # 于是"查询没返回这一项"被读成"通过"——这类假绿必须由断言自己抓出来。
    if ($NullRows -lt 0 -or $NullRowsInReportTier -lt 0) {
        $problems.Add("流水缺数量探针未取到值（全库=$NullRows，报表档=$NullRowsInReportTier）：该项断言没有真正执行。")
    }
    if ($NullRowsInReportTier -gt 0) {
        $problems.Add("报表档（料件类别 $($reportTierProTypes -join '/')）内有 $NullRowsInReportTier 行流水缺少数量：该档必须为 0（对账算式会静默忽略这些行）。")
    }
    if ($NullRows -gt $Ratchet) {
        $problems.Add("流水缺少数量的行数为 $NullRows，超过棘轮基线 $Ratchet：新增的 NULL 数量流水必须先定性（它们既不参与对账，也不留痕）。")
    }
    return $problems
}

function Test-LedgerDirection {
    <#
      第 ② 步：流水方向取值。只允许 I / O——对账算式把非 I 的一律当出库，
      其它取值（含 NULL）会静默改变符号。
    #>
    param([string[]] $Directions)

    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($direction in ($Directions | Sort-Object -Unique)) {
        if (@('I', 'O') -notcontains $direction) {
            $problems.Add("流水方向出现非法取值 '$direction'：只允许 I（入库）/ O（出库）。")
        }
    }
    return $problems
}

function Test-BalanceIdentity {
    <#
      第 ③ 步：逐键对账（纯函数，便于自检）。
      入参 Rows 每项为 @{ Key = '料号|库别'; Diff = <double>; ReportTier = <bool> }。
      返回 @{ Problems; ReportViolations; NewKeys; FrozenHit; Reducible }。
      报表档的差异单独成一类：它**不因"键已在棘轮清单里"而放行**——报表档恒 0 是硬断言。
    #>
    param([object[]] $Rows, [string[]] $Frozen)

    $frozenSet = @{}
    foreach ($key in $Frozen) { $frozenSet[$key] = $true }

    $problems = New-Object System.Collections.Generic.List[string]
    $reportViolations = New-Object System.Collections.Generic.List[string]
    $newKeys = New-Object System.Collections.Generic.List[string]
    $unbalancedNow = @{}
    $frozenHit = 0

    foreach ($row in $Rows) {
        if ([math]::Abs([double] $row.Diff) -le $balanceTolerance) { continue }
        $unbalancedNow[$row.Key] = $true
        if ($row.ReportTier) {
            $reportViolations.Add("$($row.Key) 差异 $($row.Diff)")
            continue
        }
        if ($frozenSet.ContainsKey($row.Key)) { $frozenHit++ } else { $newKeys.Add("$($row.Key) 差异 $($row.Diff)") }
    }

    $reducible = New-Object System.Collections.Generic.List[string]
    foreach ($key in $Frozen) { if (-not $unbalancedNow.ContainsKey($key)) { $reducible.Add($key) } }

    if ($reportViolations.Count -gt 0) {
        $problems.Add("报表档（料件类别 $($reportTierProTypes -join '/')）出现差异 $($reportViolations.Count) 个键——该档必须恒为 0：")
        foreach ($line in ($reportViolations | Sort-Object)) { $problems.Add("    $line") }
    }
    if ($newKeys.Count -gt 0) {
        $problems.Add("全库档出现棘轮清单外的不平键 $($newKeys.Count) 个（只减不增；本档为棘轮，含已知未定性差异，覆盖范围含成品与主档缺失料号，不代表账已全对）：")
        foreach ($line in ($newKeys | Sort-Object)) { $problems.Add("    $line") }
    }

    return @{
        Problems         = $problems
        ReportViolations = @($reportViolations)
        NewKeys          = @($newKeys)
        FrozenHit        = $frozenHit
        Reducible        = @($reducible)
    }
}

function Get-BalanceEvidence {
    <#
      连库取证据。三段 SQL 与断言顺序一一对应，返回 @{ NullRows; NullRowsInReportTier; Directions; Rows }。
    #>
    param([string] $ConnectionString)

    $proTypeList = ($reportTierProTypes | ForEach-Object { "'$_'" }) -join ','

    $nullSql = @"
SET NOCOUNT ON;
SELECT CONCAT('null=', COUNT(*)) FROM dbo.INV_DEPOT_LOG WHERE QTY IS NULL;
SELECT CONCAT('nullReportTier=', COUNT(*)) FROM dbo.INV_DEPOT_LOG l
  JOIN dbo.PRODUCT pr ON pr.PRO_NO = l.PRO_NO
 WHERE l.QTY IS NULL AND pr.PRO_TYPE IN ($proTypeList);
"@
    $nullRows = -1
    $nullReportTier = -1
    foreach ($line in @(Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query $nullSql)) {
        $text = "$line".Trim()
        if ($text -like 'nullReportTier=*') { $nullReportTier = [int] $text.Substring('nullReportTier='.Length) }
        elseif ($text -like 'null=*') { $nullRows = [int] $text.Substring('null='.Length) }
    }

    $directionSql = "SET NOCOUNT ON; SELECT ISNULL(IN_OUT, '<null>') FROM dbo.INV_DEPOT_LOG GROUP BY IN_OUT;"
    $directions = @(Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query $directionSql | ForEach-Object { "$_".Trim() })

    # 余额与流水两个集合都按 (料号, 库别) 聚合后全外连接：只在流水里出现的键（无余额行）与
    # 只在余额里出现的键（无流水）同样是"不平"，必须一起呈现，不能内连接把它们静默丢掉。
    $balanceSql = @"
SET NOCOUNT ON;
WITH bal AS (
    SELECT DEPOT_ID, PRO_NO, SUM(QTY) AS BAL_QTY
      FROM dbo.INV_PRO_DEPOT GROUP BY DEPOT_ID, PRO_NO
), led AS (
    SELECT DEPOT_ID, PRO_NO,
           SUM(CASE WHEN IN_OUT = 'I' THEN QTY ELSE -QTY END) AS LED_QTY
      FROM dbo.INV_DEPOT_LOG GROUP BY DEPOT_ID, PRO_NO
), k AS (
    SELECT COALESCE(b.PRO_NO, l.PRO_NO) AS PRO_NO,
           COALESCE(b.DEPOT_ID, l.DEPOT_ID) AS DEPOT_ID,
           ISNULL(b.BAL_QTY, 0) - ISNULL(l.LED_QTY, 0) AS DIFF
      FROM bal b FULL OUTER JOIN led l
        ON l.PRO_NO = b.PRO_NO AND l.DEPOT_ID = b.DEPOT_ID
)
SELECT CONCAT(RTRIM(k.PRO_NO), '|', RTRIM(k.DEPOT_ID), '|',
              CONVERT(NVARCHAR(40), CAST(k.DIFF AS DECIMAL(28,4))), '|',
              CASE WHEN pr.PRO_TYPE IN ($proTypeList) THEN 1 ELSE 0 END)
  FROM k LEFT JOIN dbo.PRODUCT pr ON pr.PRO_NO = k.PRO_NO;
"@

    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($line in @(Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query $balanceSql)) {
        $parts = "$line".Trim() -split '\|'
        if ($parts.Count -ne 4) { continue }
        $rows.Add([pscustomobject] @{
            Key        = "$($parts[0])|$($parts[1])"
            Diff       = [double]::Parse($parts[2], [Globalization.CultureInfo]::InvariantCulture)
            ReportTier = ($parts[3] -eq '1')
        })
    }

    # 逐项赋值而非一次性写哈希字面量：字面量里放 List<object> 会被 PowerShell 在绑定参数时
    # 报 "Argument types do not match"（与数据类型无关，是绑定路径的坑），逐项赋值绕开它。
    $result = @{}
    $result['NullRows'] = [int] $nullRows
    $result['NullRowsInReportTier'] = [int] $nullReportTier
    $result['Directions'] = [string[]] $directions
    $result['Rows'] = [object[]] $rows
    return $result
}

if ($SelfTest) {
    # ① NULL 数量：报表档内出现一行即 FAIL；全库超过棘轮基线即 FAIL；基线之内放行
    $nullDirty = @(Test-LedgerNullQuantity -NullRows 133 -NullRowsInReportTier 0 -Ratchet 132)
    if ($nullDirty.Count -ne 1) { throw "自检失败：超过棘轮基线的 NULL 数量应恰好命中 1 条，实际 $($nullDirty.Count) 条。" }
    $nullReportTier = @(Test-LedgerNullQuantity -NullRows 1 -NullRowsInReportTier 1 -Ratchet 132)
    if ($nullReportTier.Count -ne 1) { throw '自检失败：报表档内的 NULL 数量流水未被判 FAIL。' }
    $nullClean = @(Test-LedgerNullQuantity -NullRows 132 -NullRowsInReportTier 0 -Ratchet 132)
    if ($nullClean.Count -ne 0) { throw "自检失败：基线之内的 NULL 数量被误报 ⇒ $($nullClean[0])" }
    $nullMissing = @(Test-LedgerNullQuantity -NullRows 132 -NullRowsInReportTier -1 -Ratchet 132)
    if ($nullMissing.Count -ne 1) { throw '自检失败：探针未取到值（哨兵 -1）未被判 FAIL——那正是"没跑到却被读成通过"的形态。' }
    Write-Host '  [PASS] ① 流水缺数量：报表档硬 0、全库棘轮（超出即 FAIL）、探针未取到值即 FAIL'

    # ② 方向越界
    $directionDirty = @(Test-LedgerDirection -Directions @('I', 'O', 'X'))
    if ($directionDirty.Count -ne 1) { throw "自检失败：非法流水方向应恰好命中 1 条，实际 $($directionDirty.Count) 条。" }
    $directionNull = @(Test-LedgerDirection -Directions @('I', 'O', '<null>'))
    if ($directionNull.Count -ne 1) { throw '自检失败：NULL 流水方向未被判 FAIL。' }
    $directionClean = @(Test-LedgerDirection -Directions @('I', 'O'))
    if ($directionClean.Count -ne 0) { throw "自检失败：合法方向被误报 ⇒ $($directionClean[0])" }
    Write-Host '  [PASS] ② 流水方向：只允许 I / O，其它取值（含 NULL）FAIL'

    # ③ 逐键对账：报表档恒 0（不因键在棘轮清单里而放行）／清单外的不平键 FAIL／清单内的键放行
    $frozen = @('AAA|CP', 'BBB|CP')
    $rows = @(
        [pscustomobject] @{ Key = 'AAA|CP'; Diff = -3.0; ReportTier = $false }
        [pscustomobject] @{ Key = 'BBB|CP'; Diff = 0.0; ReportTier = $false }
        [pscustomobject] @{ Key = 'CCC|CP'; Diff = 0.0; ReportTier = $false }
    )
    $clean = Test-BalanceIdentity -Rows $rows -Frozen $frozen
    if ($clean.Problems.Count -ne 0) { throw "自检失败：棘轮清单内的不平键 + 已平键被误报 ⇒ $($clean.Problems[0])" }
    if ($clean.FrozenHit -ne 1) { throw "自检失败：棘轮命中数应为 1，实际 $($clean.FrozenHit)。" }
    if ($clean.Reducible.Count -ne 1 -or $clean.Reducible[0] -ne 'BBB|CP') { throw '自检失败：本期已平的冻结键未被识别为"棘轮可减"。' }

    $newKeyRows = @(
        [pscustomobject] @{ Key = 'AAA|CP'; Diff = -3.0; ReportTier = $false }
        [pscustomobject] @{ Key = 'DDD|CP'; Diff = 7.5; ReportTier = $false }
    )
    $dirty = Test-BalanceIdentity -Rows $newKeyRows -Frozen $frozen
    # 问题清单 = 结论行 + 每个不平键一行明细（与其它门禁同款：明细行也计一条）
    if ($dirty.Problems.Count -ne 2) { throw "自检失败：清单外的不平键应命中 2 条（结论 + 明细），实际 $($dirty.Problems.Count) 条。" }
    if ($dirty.Problems[0] -notmatch '棘轮' -or $dirty.Problems[0] -notmatch '含已知未定性差异') {
        throw "自检失败：全库档的 FAIL 文案未声明它含未定性差异 ⇒ $($dirty.Problems[0])"
    }
    if ($dirty.Problems[1] -notmatch 'DDD\|CP') {
        throw "自检失败：明细未点出清单外的那一个键 ⇒ $($dirty.Problems[1])"
    }

    # 报表档的键即便也在棘轮清单里，也必须被单独判 FAIL（棘轮不得豁免报表档）
    $reportRows = @([pscustomobject] @{ Key = 'AAA|CP'; Diff = -0.5; ReportTier = $true })
    $reportDirty = Test-BalanceIdentity -Rows $reportRows -Frozen $frozen
    if ($reportDirty.Problems.Count -ne 2 -or $reportDirty.ReportViolations.Count -ne 1) {
        throw '自检失败：报表档的差异被棘轮清单豁免了（报表档必须恒为 0）。'
    }
    if ($reportDirty.NewKeys.Count -ne 0) {
        throw '自检失败：报表档的差异被重复计入"清单外的不平键"，两类缺陷应各报各的。'
    }
    Write-Host '  [PASS] ③ 逐键对账：报表档恒 0（棘轮不豁免）、清单外不平键 FAIL、清单内放行并可提示棘轮可减'

    # 阈值：末位浮点噪声不计为不平
    $epsilonRows = @([pscustomobject] @{ Key = 'CCC|CP'; Diff = 0.00001; ReportTier = $true })
    if ((Test-BalanceIdentity -Rows $epsilonRows -Frozen @()).Problems.Count -ne 0) {
        throw '自检失败：浮点末位噪声被误判为不平。'
    }

    Write-Host '-- SELFTEST OK'
    exit 0
}

$problems = New-Object System.Collections.Generic.List[string]

try {
    $evidence = Get-BalanceEvidence -ConnectionString $ConnectionString
}
catch {
    Write-Output "FAIL 无法取到对账证据（连库失败）：$($_.Exception.Message)"
    exit 1
}

# ① 先断 NULL：不先断，"流水缺数量"与"账实不符"会被压成同一个数字
foreach ($problem in (Test-LedgerNullQuantity -NullRows $evidence.NullRows -NullRowsInReportTier $evidence.NullRowsInReportTier -Ratchet $nullQuantityRatchet)) {
    [void]$problems.Add($problem)
}
Write-Host "  ① 流水缺数量：全库 $($evidence.NullRows) 行（棘轮基线 $nullQuantityRatchet），报表档内 $($evidence.NullRowsInReportTier) 行（必须 0）"

# ② 再断方向
foreach ($problem in (Test-LedgerDirection -Directions $evidence.Directions)) { [void]$problems.Add($problem) }
Write-Host "  ② 流水方向取值：$(($evidence.Directions | Sort-Object) -join ' / ')"

# ③ 最后逐键对账
$identity = Test-BalanceIdentity -Rows $evidence.Rows -Frozen $frozenUnbalancedKeys
foreach ($problem in $identity.Problems) { [void]$problems.Add($problem) }

$reportTierKeyCount = @($evidence.Rows | Where-Object { $_.ReportTier }).Count
Write-Host "  ③ 逐键对账：共 $($evidence.Rows.Count) 个键（报表档 $reportTierKeyCount 个），报表档差异 $($identity.ReportViolations.Count) 个，棘轮内不平键 $($identity.FrozenHit) 个，清单外不平键 $($identity.NewKeys.Count) 个"
if ($identity.Reducible.Count -gt 0) {
    Write-Host "  [INFO] 棘轮可减：$($identity.Reducible.Count) 个冻结键本期已平（定性并修正后应从冻结清单移除）：$($identity.Reducible -join '、')"
}

if ($problems.Count -gt 0) {
    Write-Output ''
    foreach ($problem in $problems) { Write-Output "  [FAIL] $problem" }
    Write-Output ''
    Write-Output '-- FAIL'
    exit 1
}

Write-Output ''
Write-Output "PASS 库存账务自证：报表档（料件类别 $($reportTierProTypes -join '/')，$reportTierKeyCount 个键）差异 = 0；全库档 $($evidence.Rows.Count) 个键中棘轮内不平 $($identity.FrozenHit) 个（含未定性差异，只减不增）"
exit 0
