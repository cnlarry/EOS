#Requires -Version 7.0
<#
.SYNOPSIS
统一表单配置快照导出（D6 固化）：用固定 SQL 口径重新生成
logs/module-list.tsv / fields-config.tsv / modules-config.tsv / columns-config.tsv，
消除手工 sqlcmd/.NET 导出口径漂移（2026-08-10 曾因口径从 155 变 293 行导致报告膨胀）。
.DESCRIPTION
候选集合：EOS.API/appsettings.json 的 formSettings.EnabledModuleIds（统一表单白名单，
与 form-definition 放行一致）。四个快照：
- module-list.tsv：白名单模块（M_IDX|M_ALIAS|M_DESC|MASTER_TABLE|DETAIL_TABLE）；
- fields-config.tsv：白名单模块主/明细表 FIELDS（T_ID|F_ID，消费方只读前 2 列）；
- modules-config.tsv：白名单模块配置（M_IDX|M_DESC|MASTER_TABLE）；
  注：字段级 FIELDS 排布列（FORM_ORDER/TAB_NO/SPAN/NEW_LINE/CELL_*）与 MODULES 的页签/列数列已退役
  （排布归 MODULE_FORM_LAYOUT），内置动作受控注册码那一列（FORM_BUTTONS）已随迁移 320 删除，
  路由三列（NEW_URL / MODI_URL / HELP_URL）已随迁移 321 删除（只留 M_URL 承载页），
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

Write-Host "导出统一表单配置快照（白名单 $($enabled.Count) 模块）" -ForegroundColor Cyan

# module-list.tsv：白名单模块（M_IDX|M_ALIAS|M_DESC|MASTER_TABLE|DETAIL_TABLE）。
# 旧页面路径列（NEW_URL / MODI_URL / HELP_URL）已随迁移 321 物理删除，不再导出。
$moduleRows = Invoke-EosSqlTable -Query @"
SET NOCOUNT ON;
SELECT LTRIM(RTRIM(CAST(M_IDX AS varchar(20)))), LTRIM(RTRIM(ISNULL(M_ALIAS,''))), LTRIM(RTRIM(ISNULL(M_DESC,''))),
       LTRIM(RTRIM(ISNULL(MASTER_TABLE,''))), LTRIM(RTRIM(ISNULL(DETAIL_TABLE,'')))
FROM dbo.MODULES WHERE M_IDX IN $idList ORDER BY M_IDX;
"@
$moduleLines = foreach ($row in $moduleRows.Rows) {
    ($row.ItemArray | ForEach-Object { if ($null -eq $_) { '' } else { [string]$_ } }) -join '|'
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

# modules-config.tsv：白名单模块配置（M_IDX|M_DESC|MASTER_TABLE）
$modConfigRows = Invoke-EosSqlTable -Query @"
SET NOCOUNT ON;
SELECT LTRIM(RTRIM(CAST(M_IDX AS varchar(20)))), LTRIM(RTRIM(ISNULL(M_DESC,''))), LTRIM(RTRIM(ISNULL(MASTER_TABLE,'')))
FROM dbo.MODULES WHERE M_IDX IN $idList ORDER BY M_IDX;
"@
$modConfigLines = foreach ($row in $modConfigRows.Rows) {
    ($row.ItemArray | ForEach-Object { if ($null -eq $_) { '' } else { [string]$_ } }) -join '|'
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
