<#
.SYNOPSIS
    连库门禁：主表金额汇总（主表列 = 明细同名列 SUM）的三项断言。

.DESCRIPTION
    汇总规则由运行期按元数据发现（EOS.API/Data/Workbench/MasterAmountRollup.cs）：
    主表列 C 与明细同名列 C 名称相同、数据类型相同、且两侧 FIELDS.NUMERIC_ROLE='AMOUNT'。
    发现规则是隐式的——改列名、加同名列、漏登记语义都会静默改变行为，故用本门禁把
    「发现结果」与「库内事实」对起来：

      1) 逐键对账：对每个被发现的 (主从表对, 列)，凡有明细行的主表记录，
         主表值必须等于明细同名列之和（容差 0.005，口径与运行期一致：只比有明细的单据）。
      2) 只读：被发现的列必须 IS_READONLY=1——它们由框架回写，可写就是"假可写"。
      3) 独占写：效果动作的写入目标（MODULE_BUSINESS_ACTION_OP.TARGET_FIELD）不得命中
         被发现的列——命中也写不进去（框架回写会盖掉），但配置者会以为配生效了。

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

    $discover = @'
IF OBJECT_ID('tempdb..#D') IS NOT NULL DROP TABLE #D;
SELECT DISTINCT MASTER_TABLE AS M, DETAIL_TABLE AS D INTO #D FROM dbo.MODULES
 WHERE ISNULL(MASTER_TABLE,'')<>'' AND ISNULL(DETAIL_TABLE,'')<>'';
SELECT p.M AS MASTER_TABLE, p.D AS DETAIL_TABLE, f.F_ID AS COLUMN_NAME,
       CASE WHEN ISNULL(f.IS_READONLY,0)=1 THEN 1 ELSE 0 END AS IS_READONLY
FROM #D p
JOIN dbo.FIELDS f ON f.T_ID = p.M AND f.NUMERIC_ROLE = N'AMOUNT'
JOIN dbo.FIELDS d ON d.T_ID = p.D AND d.F_ID = f.F_ID AND d.NUMERIC_ROLE = N'AMOUNT'
WHERE COL_LENGTH('dbo.'+QUOTENAME(p.M), f.F_ID) IS NOT NULL
  AND COL_LENGTH('dbo.'+QUOTENAME(p.D), f.F_ID) IS NOT NULL
  AND (SELECT TYPE_NAME(c.user_type_id) FROM sys.columns c WHERE c.object_id=OBJECT_ID('dbo.'+QUOTENAME(p.M)) AND c.name=f.F_ID)
    = (SELECT TYPE_NAME(c.user_type_id) FROM sys.columns c WHERE c.object_id=OBJECT_ID('dbo.'+QUOTENAME(p.D)) AND c.name=f.F_ID)
ORDER BY p.M, f.F_ID;
'@
    $rules = @(Invoke-EosSqlQuery -Query $discover)
    if ($rules.Count -eq 0) {
        throw '发现规则为空：元数据（MODULES.MASTER_TABLE/DETAIL_TABLE、FIELDS.NUMERIC_ROLE）不完整，门禁无从判定。'
    }

    $problems = New-Object System.Collections.ArrayList

    # ---- 断言 1：逐键对账（逐条规则拼一条带 LIMIT 的差异查询） ----
    $totalChecked = 0
    $totalDiff = 0
    $skipped = 0
    foreach ($rule in $rules) {
        $sheet = ($rule -split '\s+') | Where-Object { $_ -ne '' }
        if ($sheet.Count -lt 4) { continue }
        $master = $sheet[0]; $detail = $sheet[1]; $column = $sheet[2]; $readOnly = $sheet[3]
        # 主从连键取自主表前两个主键列（与运行期一致）
        $keys = @(Invoke-EosSqlQuery -Query @"
SELECT TOP 2 c.name FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
WHERE i.object_id=OBJECT_ID('dbo.[$master]') AND i.is_primary_key=1 AND ic.key_ordinal<=2
ORDER BY ic.key_ordinal;
"@)
        if ($keys.Count -lt 2) { $skipped++; continue }
        $k1 = @(($keys[0] -split '\s+') | Where-Object { $_ -ne '' })[0]
        $k2 = @(($keys[1] -split '\s+') | Where-Object { $_ -ne '' })[0]
        # 明细表没有主表的前两个主键列 ⇒ 运行期也拼不出这条回写，属"不汇总"的合法形态，
        # 计入 skip 并在汇总行里点名，不判 FAIL（否则合法的历史表结构会永久红）。
        $hasKeys = Invoke-EosSqlQuery -Query @"
SELECT CASE WHEN COL_LENGTH('dbo.[$detail]','$k1') IS NOT NULL AND COL_LENGTH('dbo.[$detail]','$k2') IS NOT NULL THEN 1 ELSE 0 END;
"@
        if (($hasKeys -join '').Trim() -ne '1') { $skipped++; continue }
        if ($readOnly -ne '1') {
            [void]$problems.Add("只读缺失：$master.$column（由框架回写，IS_READONLY 必须为 1）")
        }
        $check = Invoke-EosSqlQuery -Query @"
SELECT COUNT(*) AS CHECKED,
       ISNULL(SUM(CASE WHEN ABS(ISNULL(m.[$column],0)-ISNULL(x.S,0))>0.005 THEN 1 ELSE 0 END),0) AS DIFF
FROM dbo.[$master] m
JOIN (SELECT [$k1],[$k2],SUM([$column]) AS S FROM dbo.[$detail] GROUP BY [$k1],[$k2]) x
  ON x.[$k1]=m.[$k1] AND x.[$k2]=m.[$k2];
"@
        $numbers = @((($check -join ' ') -split '\s+') | Where-Object { $_ -ne '' })
        if ($numbers.Count -ge 2) {
            $checked = [int]$numbers[0]; $diff = [int]$numbers[1]
            $totalChecked += $checked
            $totalDiff += $diff
            if ($diff -gt 0) {
                [void]$problems.Add("汇总不一致：$master.$column 有 $diff 张单据与明细合计不等（本轮比对 $checked 张）")
            }
        }
    }

    # ---- 断言 3：效果动作不得把被发现的列当写入目标 ----
    $rulePairs = @()
    foreach ($rule in $rules) {
        $sheet = ($rule -split '\s+') | Where-Object { $_ -ne '' }
        if ($sheet.Count -ge 3) { $rulePairs += "$($sheet[0]).$($sheet[2])" }
    }
    $opTargets = @(Invoke-EosSqlQuery -Query @"
SELECT CONCAT(A.M_IDX, '|', A.EFFECT_KEY, '|', ISNULL(OP.TARGET_TABLE,''), '.', ISNULL(OP.TARGET_FIELD,'')) AS T
FROM dbo.MODULE_BUSINESS_ACTION_OP OP
JOIN dbo.MODULE_BUSINESS_ACTION A ON A.ACTION_ID = OP.ACTION_ID;
"@)
    foreach ($target in $opTargets) {
        $parts = ($target -join ' ') -split '\|'
        if ($parts.Count -lt 3) { continue }
        $qualified = $parts[2].Trim()
        if ($rulePairs -contains $qualified) {
            [void]$problems.Add("效果动作写入汇总列：模块 $($parts[0]) 的 $($parts[1]) 写入 $qualified（框架回写会覆盖它，配置等于没配）")
        }
    }

    if ($problems.Count -gt 0) {
        foreach ($problem in $problems) { Write-Output "  [FAIL] $problem" }
        Write-Output "FAIL：主表金额汇总门禁未通过（$($problems.Count) 条；比对单据 $totalChecked 张、不一致 $totalDiff 张）"
        if ($Report) { exit 0 } else { exit 1 }
    }

    Write-Output "PASS：汇总规则 $($rules.Count) 条；比对单据 $totalChecked 张、0 不一致；$skipped 条规则因明细缺连键列不参与对账；被汇总列全部只读；无效果动作写入汇总列。"
    exit 0
}
catch {
    Write-Output "FAIL：$($_.Exception.Message)"
    exit 1
}
