#Requires -Version 7.0
<#
.SYNOPSIS
统一表单配置快照导出（D6 固化）：用固定 SQL 口径重新生成
logs/module-list.tsv / fields-config.tsv / modules-config.tsv / columns-config.tsv，
消除手工 sqlcmd/.NET 导出口径漂移（2026-08-10 曾因口径从 155 变 293 行导致报告膨胀）。
.DESCRIPTION
候选集合：EOS.API/appsettings.json 的 formSettings.EnabledModuleIds（统一表单白名单，
与 form-definition 放行一致）。四个快照：
- module-list.tsv：白名单模块（M_IDX|M_ALIAS|M_DESC|MASTER_TABLE|DETAIL_TABLE|MODI_URL）；
- fields-config.tsv：白名单模块主/明细表 FIELDS（T_ID|F_ID，消费方只读前 2 列）；
- modules-config.tsv：白名单模块配置（M_IDX|M_DESC|MASTER_TABLE|MODI_URL|FORM_BUTTONS）；
  注：字段级 FIELDS 排布列（FORM_ORDER/TAB_NO/SPAN/NEW_LINE/CELL_*）与 MODULES 的页签/列数列已退役
  （排布归 MODULE_FORM_LAYOUT），
  本导出不再含这些列；`fields-config.tsv` 保留 FORM_OPTIONS（该列仍在使用，非排布配置）。
- columns-config.tsv：白名单模块主/明细表物理列（TABLE_NAME|COLUMN_NAME）。
覆盖导出（可重跑），供 scripts/layout-diff.mjs 等差异工具复用。
.EXAMPLE
.\scripts\export-config-snapshots.ps1
#>

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $root 'EOS.API.Tests\AcceptanceCommon.ps1')

$settings = Get-Content -Raw -LiteralPath (Join-Path $root 'EOS.API\appsettings.json') | ConvertFrom-Json
$enabled = @($settings.UnifiedFormEditor.EnabledModuleIds)
if ($enabled.Count -eq 0) { throw 'appsettings.json UnifiedFormEditor.EnabledModuleIds 为空，无法导出快照。' }
$idList = "(" + (($enabled | ForEach-Object { [int]$_ }) -join ',') + ")"
$logs = Join-Path $root 'logs'
New-Item -ItemType Directory -Path $logs -Force | Out-Null

function Export-Tsv {
    param([string]$Name, [string]$Query)
    $dt = Invoke-EosSqlTable -Query $Query
    $lines = foreach ($row in $dt.Rows) { ($row.ItemArray | ForEach-Object { if ($null -eq $_) { '' } else { [string]$_ } }) -join '|' }
    [System.IO.File]::WriteAllLines((Join-Path $logs $Name), $lines, [System.Text.UTF8Encoding]::new($false))
    Write-Host "  $Name：$($dt.Rows.Count) 行" -ForegroundColor Cyan
}

# 备份表缺失的旧页面路径覆盖（M86 现代化后 MODULES.MODI_URL 无旧路径，ADR-004 备份表部分缺失）
$legacyUrlOverrides = @{
    '180102' = '~/HR/EMPLOYEE.aspx'
    '180110' = '~/HR/EMPLOYEE.aspx'
    '180105' = '~/HR/EMPLOYEE_DIMISSION.aspx'
    '180111' = '~/HR/EMPLOYEE_DIMISSION.aspx'
}

Write-Host "导出统一表单配置快照（白名单 $($enabled.Count) 模块）" -ForegroundColor Cyan

# module-list.tsv：白名单模块（MODI_URL 取 ADR-004 备份表旧页面路径，供 layout-diff/extract-layouts/
# AcceptanceSemantics 提取旧页面；无旧路径回退现代值——现代化后 MODULES.MODI_URL 已无旧路径）
$moduleRows = Invoke-EosSqlTable -Query @"
SET NOCOUNT ON;
SELECT LTRIM(RTRIM(CAST(M_IDX AS varchar(20)))), LTRIM(RTRIM(ISNULL(M_ALIAS,''))), LTRIM(RTRIM(ISNULL(M_DESC,''))),
       LTRIM(RTRIM(ISNULL(MASTER_TABLE,''))), LTRIM(RTRIM(ISNULL(DETAIL_TABLE,''))),
       COALESCE((SELECT TOP 1 LTRIM(RTRIM(b.MODI_URL)) FROM dbo.MODULES_Backup_ADR004 b
                 WHERE b.M_IDX=MODULES.M_IDX AND LTRIM(RTRIM(ISNULL(b.MODI_URL,''))) LIKE '~%'),
                LTRIM(RTRIM(ISNULL(MODI_URL,''))))
FROM dbo.MODULES WHERE M_IDX IN $idList ORDER BY M_IDX;
"@
$moduleLines = foreach ($row in $moduleRows.Rows) {
    $id = [string]$row[0]
    $url = if ($legacyUrlOverrides.ContainsKey($id)) { $legacyUrlOverrides[$id] } else { [string]$row[5] }
    ($row.ItemArray[0..4] + @($url) | ForEach-Object { if ($null -eq $_) { '' } else { [string]$_ } }) -join '|'
}
[System.IO.File]::WriteAllLines((Join-Path $logs 'module-list.tsv'), $moduleLines, [System.Text.UTF8Encoding]::new($false))
Write-Host "  module-list.tsv：$($moduleLines.Count) 行" -ForegroundColor Cyan

# fields-config.tsv：白名单模块主/明细表 FIELDS（3 列，layout-diff 只读前 2 列）
$tables = @()
$tblDt = Invoke-EosSqlTable -Query "SELECT LTRIM(RTRIM(MASTER_TABLE)) FROM dbo.MODULES WHERE M_IDX IN $idList AND LTRIM(RTRIM(ISNULL(MASTER_TABLE,'')))<>'' UNION SELECT LTRIM(RTRIM(DETAIL_TABLE)) FROM dbo.MODULES WHERE M_IDX IN $idList AND LTRIM(RTRIM(ISNULL(DETAIL_TABLE,'')))<>'';"
foreach ($row in $tblDt.Rows) { if ($row[0]) { $tables += [string]$row[0] } }
if ($tables.Count -gt 0) {
    $tblList = "(" + (($tables | ForEach-Object { "'$_'" }) -join ',' ) + ")"
    Export-Tsv 'fields-config.tsv' @"
SET NOCOUNT ON;
SELECT LTRIM(RTRIM(T_ID)), LTRIM(RTRIM(F_ID)),
       LTRIM(RTRIM(ISNULL(FORM_OPTIONS,'')))
FROM dbo.FIELDS WHERE LTRIM(RTRIM(T_ID)) IN $tblList ORDER BY T_ID, F_ID;
"@
}

# modules-config.tsv：白名单模块布局配置（MODI_URL 同取旧路径）
$modConfigRows = Invoke-EosSqlTable -Query @"
SET NOCOUNT ON;
SELECT LTRIM(RTRIM(CAST(M_IDX AS varchar(20)))), LTRIM(RTRIM(ISNULL(M_DESC,''))), LTRIM(RTRIM(ISNULL(MASTER_TABLE,''))),
       COALESCE((SELECT TOP 1 LTRIM(RTRIM(b.MODI_URL)) FROM dbo.MODULES_Backup_ADR004 b
                 WHERE b.M_IDX=MODULES.M_IDX AND LTRIM(RTRIM(ISNULL(b.MODI_URL,''))) LIKE '~%'),
                LTRIM(RTRIM(ISNULL(MODI_URL,'')))),
       LTRIM(RTRIM(ISNULL(FORM_BUTTONS,'')))
FROM dbo.MODULES WHERE M_IDX IN $idList ORDER BY M_IDX;
"@
$modConfigLines = foreach ($row in $modConfigRows.Rows) {
    $id = [string]$row[0]
    $url = if ($legacyUrlOverrides.ContainsKey($id)) { $legacyUrlOverrides[$id] } else { [string]$row[3] }
    ($row.ItemArray[0..2] + @($url) + @($row.ItemArray[4]) | ForEach-Object { if ($null -eq $_) { '' } else { [string]$_ } }) -join '|'
}
[System.IO.File]::WriteAllLines((Join-Path $logs 'modules-config.tsv'), $modConfigLines, [System.Text.UTF8Encoding]::new($false))
Write-Host "  modules-config.tsv：$($modConfigLines.Count) 行" -ForegroundColor Cyan

# columns-config.tsv：白名单模块主/明细表物理列
if ($tables.Count -gt 0) {
    $tblList = "(" + (($tables | ForEach-Object { "'$_'" }) -join ',' ) + ")"
    Export-Tsv 'columns-config.tsv' @"
SET NOCOUNT ON;
SELECT o.name AS TABLE_NAME, c.name AS COLUMN_NAME
FROM sys.columns c
JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
JOIN sys.schemas s ON o.schema_id=s.schema_id
WHERE s.name='dbo' AND o.name IN $tblList ORDER BY o.name, c.column_id;
"@
}

Write-Host "快照导出完成：$logs" -ForegroundColor Green
