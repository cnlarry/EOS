<#
.SYNOPSIS
    删除服务型效果下遗留的全空占位公式行，删除前把每一行**逐列**备份成可回退的数据文件。

.DESCRIPTION
    占位行＝算子、目标表、目标列三者皆空的公式行：翻译期随行带出的空行，语义在动作参数里。
    运行期一律跳过（加载即丢），但**它会进定义快照**，因此留着它会让"配置有没有改过"看起来像改过。

    本脚本把"清理"和"留退路"做成一步：先按全部列的原值把将要删除的行导出成 JSON 备份，
    再在同一事务里删除；删除行数与备份行数不一致就整笔回滚——不允许"删了但没备份"。
    清理与回退都幂等：清理按谓词删（再跑一次删 0 行），回退按 OP_ID 判存在（再跑一次插 0 行）。

    只清库内数据，**不碰快照**：快照版本是否前进由发布侧决定（内容未变则复用当前版本）。

.PARAMETER ModuleIds
    只清理这些模块；缺省 = 全部模块。

.PARAMETER BackupFile
    备份文件路径；缺省 logs/placeholder-op-rows-<yyyyMMdd-HHmmss>.json。

.PARAMETER UpdatedBy
    脏标记里的修改人（清理后模块需要按流程重发布）。

.PARAMETER SkipDirtyMark
    不标脏。缺省标脏：删过行的模块应显式提示"待重发布"，否则在旧判据的运行实例上会表现为无声落后。

.PARAMETER ConnectionString
    连接串；缺省用 eos-sql.ps1 的解析逻辑（环境变量 MSSQL_ERP_CONN → 集成认证）。

.EXAMPLE
    pwsh scripts/clean-placeholder-formula-rows.ps1 -ModuleIds 1406 -WhatIf
    pwsh scripts/clean-placeholder-formula-rows.ps1 -ModuleIds 1406
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [int[]] $ModuleIds = @(),
    [string] $BackupFile,
    [string] $UpdatedBy = 'PLACEHOLDER-OP-CLEANUP',
    [switch] $SkipDirtyMark,
    [string] $ConnectionString
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

. (Join-Path $PSScriptRoot 'dev/eos-sql.ps1')
$target = Get-EosSqlTarget -ConnectionString $ConnectionString

if (-not $BackupFile) {
    $BackupFile = Join-Path $PSScriptRoot ('..\logs\placeholder-op-rows-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
}
$BackupFile = [IO.Path]::GetFullPath($BackupFile)

function Invoke-SqlRows {
    param([Parameter(Mandatory = $true)][string] $Sql)

    # 经托管客户端取整值：备份是整行 JSON、结构列是长文本，sqlcmd 的默认列宽会把长值折行/截断
    # （-W 只去行尾空格，不解决列宽），因此这里不用 sqlcmd 通道。
    return @(Invoke-EosManagedQuery -ConnectionString $target.AdoConnectionString -Sql $Sql)
}

$moduleFilter = if ($ModuleIds.Count -gt 0) {
    ' AND a.M_IDX IN (' + (($ModuleIds | Sort-Object -Unique) -join ',') + ')'
} else { '' }

# 占位行判据与运行期同一口径：算子、目标表、目标列三者皆空
$placeholderFilter = "LTRIM(RTRIM(ISNULL(o.OP_CODE,''))) + LTRIM(RTRIM(ISNULL(o.TARGET_TABLE,''))) + LTRIM(RTRIM(ISNULL(o.TARGET_FIELD,''))) = ''"

$captureSql = @"
DECLARE @rows nvarchar(max) = (
    SELECT o.OP_ID AS opId, o.ACTION_ID AS actionId, o.OP_SEQ AS opSeq,
           o.TARGET_TABLE AS targetTable, o.TARGET_FIELD AS targetField, o.OP_CODE AS opCode,
           o.SOURCE_SCOPE AS sourceScope, o.SOURCE_TABLE AS sourceTable, o.SOURCE_FIELD AS sourceField,
           o.SOURCE_AGG AS sourceAgg, o.SOURCE_CONSTANT AS sourceConstant,
           o.SOURCE_TERMS_STRUCT AS sourceTerms, o.MATCH_STRUCT AS matchStruct,
           o.CONDITION_STRUCT AS conditionStruct, o.REMARK AS remark,
           a.M_IDX AS moduleId, a.EVENT_CODE AS eventCode, a.SEQ AS actionSeq, a.EFFECT_KEY AS effectKey
    FROM dbo.MODULE_BUSINESS_ACTION_OP o
    JOIN dbo.MODULE_BUSINESS_ACTION a ON a.ACTION_ID = o.ACTION_ID
    WHERE $placeholderFilter$moduleFilter
    ORDER BY a.M_IDX, a.EVENT_CODE, a.SEQ, o.OP_SEQ
    FOR JSON PATH
);
SELECT CONVERT(varchar(max), CONVERT(varbinary(max), ISNULL(@rows, N'[]')), 2);
"@

$hex = @(Invoke-SqlRows -Sql $captureSql) | Select-Object -First 1
if (-not $hex) { throw '取备份失败：查询没有返回结果。' }
$json = [Text.Encoding]::Unicode.GetString([Convert]::FromHexString($hex.Trim()))
$rows = @($json | ConvertFrom-Json)
$moduleScope = @($rows | ForEach-Object { [int] $_.moduleId } | Sort-Object -Unique)

$payload = [pscustomobject]@{
    capturedAt = (Get-Date).ToString('s')
    predicate  = 'OP_CODE/TARGET_TABLE/TARGET_FIELD 三者皆空（与运行期 IsPlaceholderOp 同口径）'
    moduleIds  = @($ModuleIds)
    rowCount   = $rows.Count
    rows       = $rows
}
# 直接写文件而不经 Set-Content：-WhatIf 会被 PowerShell 传给内部 cmdlet，连备份一起跳过，
# 于是"预演"会打印出备份路径却没写文件。备份必须无条件落盘——它是唯一的退路。
$backupDirectory = Split-Path -Parent $BackupFile
if ($backupDirectory -and -not (Test-Path -LiteralPath $backupDirectory)) {
    New-Item -ItemType Directory -Force -Path $backupDirectory | Out-Null
}
[IO.File]::WriteAllText($BackupFile, ($payload | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))

Write-Host "备份：$($rows.Count) 行 -> $BackupFile"
Write-Host ("涉及模块：{0}" -f $(if ($moduleScope.Count) { $moduleScope -join ',' } else { '（无）' }))
foreach ($row in $rows) {
    Write-Host ("  模块 {0} / {1} SEQ={2} / {3} / OP_ID={4} OP_SEQ={5}" -f $row.moduleId, $row.eventCode, $row.actionSeq, $row.effectKey, $row.opId, $row.opSeq)
}

if ($rows.Count -eq 0) {
    Write-Host '没有需要清理的占位行（幂等：重复执行不会重复删除）。'
    return
}

if (-not $PSCmdlet.ShouldProcess("$($rows.Count) 行占位公式行（模块 $($moduleScope -join ',')）", '删除')) {
    Write-Host '已按 -WhatIf 跳过删除；备份文件已生成，可据此回退。'
    return
}

$deleteSql = @"
SET XACT_ABORT ON;
BEGIN TRAN;
DELETE o FROM dbo.MODULE_BUSINESS_ACTION_OP o
JOIN dbo.MODULE_BUSINESS_ACTION a ON a.ACTION_ID = o.ACTION_ID
WHERE $placeholderFilter$moduleFilter;
DECLARE @deleted int = @@ROWCOUNT;
IF @deleted <> $($rows.Count)
BEGIN
    ROLLBACK TRAN;
    THROW 50000, N'删除行数与备份行数不一致，整笔回滚。', 1;
END
SELECT CONCAT('deleted=', @deleted);
COMMIT;
"@

# 取数只回第一结果集，故删除与标脏分两次调用，各自的读数都看得见
Write-Host ((@(Invoke-SqlRows -Sql $deleteSql)) -join ' ')

if (-not $SkipDirtyMark) {
    $dirtySql = @"
MERGE dbo.WORKBENCH_MODULE_DIRTY AS t
USING (SELECT M_IDX AS M_IDX FROM dbo.MODULES WHERE M_IDX IN ($($moduleScope -join ','))) AS s
    ON t.M_IDX = s.M_IDX
WHEN MATCHED THEN UPDATE SET DIRTY_TAG=1, LAST_MODIFIED_BY=N'$UpdatedBy', LAST_MODIFIED_AT=SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (s.M_IDX, 1, N'$UpdatedBy', SYSDATETIME());
SELECT CONCAT('dirty_marked=', @@ROWCOUNT);
"@
    Write-Host ((@(Invoke-SqlRows -Sql $dirtySql)) -join ' ')
}

$after = @(Invoke-SqlRows -Sql "SELECT CONCAT('remaining_placeholder_rows=', COUNT(*)) FROM dbo.MODULE_BUSINESS_ACTION_OP o JOIN dbo.MODULE_BUSINESS_ACTION a ON a.ACTION_ID=o.ACTION_ID WHERE $placeholderFilter$moduleFilter;")
Write-Host ($after -join ' ')
Write-Host "回退：pwsh scripts/restore-placeholder-formula-rows.ps1 -BackupFile `"$BackupFile`""
