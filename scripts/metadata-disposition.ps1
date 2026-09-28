#Requires -Version 7.0
<#
.SYNOPSIS
阶段 1 元数据异常清单处置：生成逐条处置记录（disposition.csv）并对机械性
TYPE_MISMATCH 直接修复（F_TYPE 对齐物理列类型族）。
.DESCRIPTION
前置：先运行 .\EOS.API.Tests\AcceptanceMetadata.ps1 生成最新 metadata-exceptions.csv。
处置规则：
- TYPE_MISMATCH（205）——机械性可修复：F_TYPE 对齐物理 DATA_TYPE（精度以服务端为准，
  AGENTS.md），-Apply 时幂等 UPDATE（WHERE 含旧 F_TYPE 防覆盖人工编辑）；
- NOTNULL_NO_VERIFY（2026-08-15 预分类后 380）——不机械修复：AcceptanceMetadata.ps1
  已自动预分类（PK 375 / AUTO_SERIAL 5 / 选择器/只读/默认值/主表联动兜底），
  当前需人工确认 0 条；本脚本仅处置 confirmedVerify 清单与 EXCLUDE 兜底；
- HIDDEN_TABLE_HIGH / AUTOINC_MARKED_NOT —— 人工复核/技术债登记。
输出：logs/goal/phase1/disposition.csv + fix-type-mismatch.csv。
.EXAMPLE
.\scripts\metadata-disposition.ps1              # 仅生成处置记录（TYPE_MISMATCH=PENDING）
.\scripts\metadata-disposition.ps1 -Apply       # 应用 TYPE_MISMATCH 修复
#>
param(
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $root 'EOS.API.Tests\AcceptanceCommon.ps1')

$phase1 = Join-Path $root 'logs\goal\phase1'
$exceptionsPath = Join-Path $phase1 'metadata-exceptions.csv'
if (-not (Test-Path $exceptionsPath)) { throw "缺少 $exceptionsPath，请先运行 AcceptanceMetadata.ps1" }
$exceptions = @(Import-Csv $exceptionsPath)
if ($exceptions.Count -eq 0) { throw '异常清单为空' }

$disposition = [System.Collections.Generic.List[object]]::new()
$fixRows = [System.Collections.Generic.List[object]]::new()
$seenFix = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

# 用户确认（2026-08-09）：物理 NOT NULL 且无默认值的业务录入字段补 IS_VERIFY=1
$confirmedVerify = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($key in @(
    'BILLKIND|BILL_NAME',
    'MOU_ACCEPTDELE_D|ACCEPT_TYPE',
    'MOU_ACCEPTDELE_D|ACCEPT_NO',
    'INV_LOAN_M|LOAN_DATE',
    'HR_CONTRACT_D|EMP_ID',
    'HR_SAFE_D|EMP_ID',
    'HR_SAFE_D|SAFE_NUMBER',
    'HR_CERTIFY_D|EMP_ID',
    'HR_CERTIFY_D|CERTIFY_NUMBER',
    'HR_ENACTMENT_D|EMP_ID'
)) { [void]$confirmedVerify.Add($key) }

function Get-PhysicalType {
    param([string]$Table, [string]$Column)
    $rows = Invoke-EosSqlTable -Query "SELECT TYPE_NAME(c.user_type_id) AS DATA_TYPE FROM sys.columns c JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V') JOIN sys.schemas s ON o.schema_id=s.schema_id WHERE s.name='dbo' AND o.name='$Table' AND c.name='$Column';"
    if ($rows.Rows.Count -eq 0) { return $null }
    return [string]$rows.Rows[0].DATA_TYPE
}

function Test-IsPrimaryKey {
    param([string]$Table, [string]$Column)
    $rows = Invoke-EosSqlTable -Query @"
SELECT COUNT(*) C FROM sys.indexes i
JOIN sys.index_columns ic ON i.object_id=ic.object_id AND i.index_id=ic.index_id
JOIN sys.columns c ON ic.object_id=c.object_id AND ic.column_id=c.column_id
JOIN sys.tables t ON i.object_id=t.object_id
JOIN sys.schemas s ON t.schema_id=s.schema_id
WHERE s.name='dbo' AND t.name='$Table' AND c.name='$Column' AND i.is_primary_key=1;
"@
    return ([int]$rows.Rows[0].C) -gt 0
}

function Test-HasDefault {
    param([string]$Table, [string]$Column)
    $rows = Invoke-EosSqlTable -Query "SELECT dc.definition AS COLUMN_DEFAULT FROM sys.columns c JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V') JOIN sys.schemas s ON o.schema_id=s.schema_id LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id WHERE s.name='dbo' AND o.name='$Table' AND c.name='$Column';"
    if ($rows.Rows.Count -eq 0) { return $false }
    $value = $rows.Rows[0].COLUMN_DEFAULT
    return ($null -ne $value) -and ($value -isnot [System.DBNull]) -and ([string]$value).Trim() -ne ''
}

foreach ($ex in $exceptions) {
    $row = [pscustomobject]@{
        ModuleId   = $ex.ModuleId
        Table      = $ex.Table
        Column     = $ex.Column
        Category   = $ex.Category
        Detail     = $ex.Detail
        Status     = 'REVIEW'
        Action     = '人工复核'
        Target     = ''
        Note       = ''
    }
    switch ($ex.Category) {
        'TYPE_MISMATCH' {
            $physical = Get-PhysicalType $ex.Table $ex.Column
            $row.Target = $physical
            $row.Action = 'UPDATE_F_TYPE'
            $key = "$($ex.Table)|$($ex.Column)"
            if ($Apply -and $physical -and $seenFix.Contains($key)) {
                $row.Status = 'APPLIED'
            } elseif ($Apply -and $physical -and $seenFix.Add($key)) {
                $old = ([regex]::Match($ex.Detail, 'F_TYPE=([^\s]+)')).Groups[1].Value
                $null = Invoke-EosSqlNonQuery -Query @"
UPDATE dbo.FIELDS SET F_TYPE='$physical'
WHERE LTRIM(RTRIM(T_ID))='$($ex.Table)' AND LTRIM(RTRIM(F_ID))='$($ex.Column)' AND F_TYPE='$old';
"@
                $row.Status = 'APPLIED'
                $fixRows.Add([pscustomobject]@{
                    Table = $ex.Table; Column = $ex.Column; ModuleId = $ex.ModuleId
                    OldType = $old; NewType = $physical
                })
            } elseif ($physical) {
                $row.Status = 'PENDING'
            } else {
                $row.Status = 'REVIEW'
                $row.Action = '人工复核'
                $row.Note = '物理列不存在或已变更，跳过'
            }
        }
        'NOTNULL_NO_VERIFY' {
            if ($confirmedVerify.Contains("$($ex.Table)|$($ex.Column)")) {
                $row.Action = 'UPDATE_IS_VERIFY'
                $row.Target = 'IS_VERIFY=1'
                $row.Note = '用户确认（2026-08-09）：物理 NOT NULL 业务录入字段，补表单必填'
                if ($Apply) {
                    $null = Invoke-EosSqlNonQuery -Query @"
UPDATE dbo.FIELDS SET IS_VERIFY=1
WHERE LTRIM(RTRIM(T_ID))='$($ex.Table)' AND LTRIM(RTRIM(F_ID))='$($ex.Column)' AND ISNULL(IS_VERIFY,0)=0;
"@
                    $row.Status = 'APPLIED_IS_VERIFY'
                } else {
                    $row.Status = 'PENDING_IS_VERIFY'
                }
            } elseif ($ex.Column -eq 'SERIAL_NO') {
                $row.Status = 'EXCLUDE'
                $row.Action = '不迁移'
                $row.Note = '明细序号由服务端自动编号（AssignSerialNumbers），不适用表单必填'
            } elseif (Test-IsPrimaryKey $ex.Table $ex.Column) {
                $row.Status = 'EXCLUDE'
                $row.Action = '不迁移'
                $row.Note = '物理主键，必填性由主键约束与单号/组合键规则保证（自动单号/服务端填充）'
            } elseif (Test-HasDefault $ex.Table $ex.Column) {
                $row.Status = 'EXCLUDE'
                $row.Action = '不迁移'
                $row.Note = '物理列有 DEFAULT，非表单必填'
            } else {
                $row.Note = "预分类：$($ex.Disposition)（非主键、无默认、非自动编号：人工确认是否服务端填充后补 IS_VERIFY=1）"
            }
        }
        'HIDDEN_TABLE_HIGH' {
            $row.Status = 'CONFIRMED_HIDDEN'
            $row.Action = '不改库'
            $row.Note = '用户确认（2026-08-09）：工资明细动态显隐为有意隐藏（HR_WAGE_D/HRM_WAGE_D）'
        }
        'AUTOINC_MARKED_NOT' {
            $row.Status = 'CONFIRMED_TECHDEBT'
            $row.Action = '不改库'
            $row.Note = '用户确认（2026-08-09）：保留 IS_AUTOINC 并登记服务端生成语义技术债（M64 SYSDG.G_IDX 先例）'
        }
        default {
            $row.Note = '未分类项，人工复核'
        }
    }
    $disposition.Add($row)
}

New-Item -ItemType Directory -Path $phase1 -Force | Out-Null
$disposition | Export-Csv -LiteralPath (Join-Path $phase1 'disposition.csv') -NoTypeInformation -Encoding UTF8
if ($fixRows.Count -gt 0) {
    $fixRows | Export-Csv -LiteralPath (Join-Path $phase1 'fix-type-mismatch.csv') -NoTypeInformation -Encoding UTF8
}

Write-Host "处置记录已生成：$($disposition.Count) 条" -ForegroundColor Cyan
$disposition | Group-Object Category | ForEach-Object {
    $byStatus = ($_.Group | Group-Object Status | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ' '
    Write-Host ("  {0,-20} {1,4}  [{2}]" -f $_.Name, $_.Count, $byStatus) -ForegroundColor Cyan
}
if ($Apply) {
    Write-Host "TYPE_MISMATCH 修复已应用：$($fixRows.Count) 条（F_TYPE 对齐物理类型）" -ForegroundColor Green
} else {
    Write-Host '未应用修复（-Apply 可执行）。' -ForegroundColor Yellow
}
