#Requires -Version 7.0
<#
.SYNOPSIS
选择器缺口回填（第一层业务复核推进）：对 AcceptanceSemantics.ps1 输出的 MISSING 选择器候选，
按同名列众数样例复制 CHOOSE_* 配置，生成幂等 SQL（update.sql EOS-13 节）。
.DESCRIPTION
前置：先运行 EOS.API.Tests\AcceptanceSemantics.ps1 生成 semantics-candidates.csv。
规则：
- 候选 = Disposition=MISSING 且 Kind=CHOOSER（旧页有 DxChooser、现代 FIELDS 可见可编辑但 CHOOSE_ACTIVE 全 0）；
- 样例 = 同名列 CHOOSE_ACTIVE1=1 且 CHOOSE_T_ID1 非空的行，按目标表计数取众数；
- 无同名列先例的字段跳过（EMP_NAME/FEE_TYPE/ASSESS_TYPE/CURR_ID_NOW 等留人工清单）；
- 幂等：仅当目标 FIELDS 行 CHOOSE_ACTIVE1..4 全 0 时更新。
输出：logs/goal/phase1/backfill-chooser.sql（预览）；-Apply 直接执行。
.EXAMPLE
.\scripts\backfill-chooser.ps1                # 生成 SQL（不执行）
.\scripts\backfill-chooser.ps1 -Apply         # 生成并执行
#>
param(
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $root 'EOS.API.Tests\AcceptanceCommon.ps1')
$phase1 = Join-Path $root 'logs\goal\phase1'
$csv = Join-Path $phase1 'semantics-candidates.csv'
if (-not (Test-Path $csv)) { throw "缺少 $csv，请先运行 AcceptanceSemantics.ps1" }

$candidates = @(Import-Csv $csv | Where-Object { $_.Disposition -eq 'MISSING' -and $_.Kind -eq 'CHOOSER' })
if ($candidates.Count -eq 0) { Write-Host '无 MISSING 选择器候选'; return }

$sql = [System.Text.StringBuilder]::new()
[void]$sql.AppendLine("/* EOS-13：选择器缺口回填（旧页 DxChooser → FIELDS.CHOOSE_*，2026-08-16） */")
[void]$sql.AppendLine("BEGIN TRANSACTION;")

$applied = 0
$skipped = [System.Collections.Generic.List[object]]::new()
foreach ($cand in $candidates) {
    $table = $cand.Table
    $field = $cand.Field
    # 同名列众数样例（排除自身表）
    $sample = Invoke-EosSqlTable -Query @"
SELECT TOP 1 LTRIM(RTRIM(CHOOSE_T_ID1)) AS T, ISNULL(CHOOSE_M_IDX1,0) AS MIDX,
       LTRIM(RTRIM(ISNULL(CHOOSE_T_DESC1,''))) AS TDESC, LTRIM(RTRIM(ISNULL(CHOOSE_RETURNVAL1,''))) AS RET,
       LTRIM(RTRIM(ISNULL(CHOOSE_FILTER1,''))) AS FLT
FROM dbo.FIELDS
WHERE LTRIM(RTRIM(F_ID))='$field' AND ISNULL(CHOOSE_ACTIVE1,0)=1
  AND LTRIM(RTRIM(ISNULL(CHOOSE_T_ID1,'')))<>'' AND LTRIM(RTRIM(T_ID))<>'$table'
GROUP BY CHOOSE_T_ID1, CHOOSE_M_IDX1, CHOOSE_T_DESC1, CHOOSE_RETURNVAL1, CHOOSE_FILTER1
ORDER BY COUNT_BIG(1) DESC, LEN(ISNULL(CHOOSE_RETURNVAL1,'')) DESC;
"@
    if ($sample.Rows.Count -eq 0) {
        $skipped.Add([pscustomobject]@{ ModuleId = $cand.ModuleId; Table = $table; Field = $field; Reason = '无同名列先例' })
        continue
    }
    $t = [string]$sample.Rows[0][0]
    $midx = [int]$sample.Rows[0][1]
    $tdesc = [string]$sample.Rows[0][2]
    $ret = [string]$sample.Rows[0][3]
    $flt = [string]$sample.Rows[0][4]
    $esc = { param($v) $v.Replace("'", "''") }
    [void]$sql.AppendLine(@"
IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE LTRIM(RTRIM(T_ID))='$table' AND LTRIM(RTRIM(F_ID))='$field'
           AND ISNULL(CHOOSE_ACTIVE1,0)=0 AND ISNULL(CHOOSE_ACTIVE2,0)=0 AND ISNULL(CHOOSE_ACTIVE3,0)=0 AND ISNULL(CHOOSE_ACTIVE4,0)=0)
    UPDATE dbo.FIELDS SET CHOOSE_ACTIVE1=1, CHOOSE_T_ID1='$t', CHOOSE_M_IDX1=$midx,
        CHOOSE_T_DESC1=$(if ($tdesc) { "N'$(& $esc $tdesc)'" } else { 'NULL' }),
        CHOOSE_RETURNVAL1=$(if ($ret) { "N'$(& $esc $ret)'" } else { 'NULL' }),
        CHOOSE_FILTER1=$(if ($flt) { "N'$(& $esc $flt)'" } else { 'NULL' }),
        LAST_UPDATE_BY='EOS-MIG', LAST_UPDATE_DATE=GETDATE()
    WHERE LTRIM(RTRIM(T_ID))='$table' AND LTRIM(RTRIM(F_ID))='$field';
"@)
    $applied++
}
[void]$sql.AppendLine("COMMIT TRANSACTION;")
[void]$sql.AppendLine("PRINT N'[EOS-13] 选择器缺口回填：$applied 条。';")
[void]$sql.AppendLine("GO")

$sqlPath = Join-Path $phase1 'backfill-chooser.sql'
[System.IO.File]::WriteAllText($sqlPath, $sql.ToString(), [System.Text.UTF8Encoding]::new($true))
Write-Host "生成选择器回填 SQL：$applied 条（$sqlPath）" -ForegroundColor Cyan
if ($skipped.Count -gt 0) {
    Write-Host "跳过（无同名列先例，留人工）：$($skipped.Count) 条" -ForegroundColor Yellow
    $skipped | ForEach-Object { Write-Host "  $($_.Table).$($_.Field)（$($_.Reason)）" -ForegroundColor Yellow }
    $skipped | Export-Csv -LiteralPath (Join-Path $phase1 'chooser-skip-manual.csv') -NoTypeInformation -Encoding UTF8
}
if ($Apply) {
    $null = Invoke-EosSqlTable -Query $sql.ToString()
    Write-Host "已执行：$applied 条回填" -ForegroundColor Green
}
