#Requires -Version 7.0
<#
.SYNOPSIS
统一审计留存与归档（ADR-005 §8）：DETAIL_JSON 30 天截断、AUDIT_FIELD_CHANGE 90 天归档、
AUDIT_EVENT 180 天归档（导出 JSONL 后清理）。
.DESCRIPTION
策略（默认值可覆盖）：DETAIL_JSON 30 天、AUDIT_FIELD_CHANGE 90 天、AUDIT_EVENT 180 天。
归档文件写入 logs/audit-archive/；汇总报告同目录 retention-<stamp>.json。
可直接对 EOS.ERP 执行（经 AcceptanceCommon 的 sqlcmd 连接）。
.EXAMPLE
.\scripts\audit-retention.ps1
.\scripts\audit-retention.ps1 -EventDays 365 -FieldDays 180
#>
param(
    [int]$DetailDays = 30,
    [int]$FieldDays = 90,
    [int]$EventDays = 180,
    [string]$ArchiveDir = ''
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $root 'EOS.API.Tests\AcceptanceCommon.ps1')
if (-not $ArchiveDir) { $ArchiveDir = Join-Path $root 'logs\audit-archive' }
New-Item -ItemType Directory -Path $ArchiveDir -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

Write-Host "== 1. DETAIL_JSON 截断（>$DetailDays 天）==" -ForegroundColor Cyan
$truncated = Invoke-EosSqlNonQuery -Query @"
UPDATE dbo.AUDIT_EVENT SET DETAIL_JSON = NULL
WHERE CREATED_DATE < DATEADD(day, -$DetailDays, SYSDATETIME()) AND DETAIL_JSON IS NOT NULL;
"@
Write-Host "  截断 $truncated 行"

Write-Host "== 2. AUDIT_FIELD_CHANGE 归档 + 清理（>$FieldDays 天）==" -ForegroundColor Cyan
$fieldJson = Invoke-EosSqlText -Query @"
SELECT EVENT_ID, FIELD_NAME, OLD_VALUE, NEW_VALUE,
       CASE WHEN VALUE_HASH IS NULL THEN NULL ELSE CONVERT(nvarchar(64), VALUE_HASH, 2) END AS VALUE_HASH_HEX
FROM dbo.AUDIT_FIELD_CHANGE
WHERE EVENT_ID IN (SELECT EVENT_ID FROM dbo.AUDIT_EVENT WHERE CREATED_DATE < DATEADD(day, -$FieldDays, SYSDATETIME()))
ORDER BY EVENT_ID FOR JSON PATH;
"@
if ($fieldJson) {
    $fieldFile = Join-Path $ArchiveDir "audit-field-change-$stamp.jsonl"
    [System.IO.File]::WriteAllText($fieldFile, ($fieldJson -replace '}{', "}`n{") + "`n", [System.Text.UTF8Encoding]::new($false))
    Write-Host "  归档 $fieldFile"
}
$fieldDeleted = Invoke-EosSqlNonQuery -Query @"
DELETE FROM dbo.AUDIT_FIELD_CHANGE
WHERE EVENT_ID IN (SELECT EVENT_ID FROM dbo.AUDIT_EVENT WHERE CREATED_DATE < DATEADD(day, -$FieldDays, SYSDATETIME()));
"@
Write-Host "  清理 $fieldDeleted 行"

Write-Host "== 3. AUDIT_EVENT 归档 + 清理（>$EventDays 天）==" -ForegroundColor Cyan
$eventJson = Invoke-EosSqlText -Query @"
SELECT EVENT_ID, CONVERT(nvarchar(30), OCCURRED_AT, 126) AS OCCURRED_AT, CORRELATION_ID,
       ACTOR_USER_ID, ACTOR_TYPE, CLIENT_TYPE, M_IDX, RESOURCE_TYPE, RESOURCE_KEY,
       ACTION, RESULT, DEFINITION_VERSION, SUMMARY
FROM dbo.AUDIT_EVENT
WHERE CREATED_DATE < DATEADD(day, -$EventDays, SYSDATETIME())
ORDER BY EVENT_ID FOR JSON PATH;
"@
if ($eventJson) {
    $eventFile = Join-Path $ArchiveDir "audit-event-$stamp.jsonl"
    [System.IO.File]::WriteAllText($eventFile, ($eventJson -replace '}{', "}`n{") + "`n", [System.Text.UTF8Encoding]::new($false))
    Write-Host "  归档 $eventFile"
}
$eventDeleted = Invoke-EosSqlNonQuery -Query @"
DELETE FROM dbo.AUDIT_EVENT WHERE CREATED_DATE < DATEADD(day, -$EventDays, SYSDATETIME());
"@
Write-Host "  清理 $eventDeleted 行"

$summary = [pscustomobject]@{
    GeneratedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    DetailDays = $DetailDays; FieldDays = $FieldDays; EventDays = $EventDays
    DetailTruncated = $truncated; FieldChangesArchived = $fieldDeleted; EventsArchived = $eventDeleted
    ArchiveDir = $ArchiveDir
}
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ArchiveDir "retention-$stamp.json") -Encoding utf8
Write-Host "报告：$(Join-Path $ArchiveDir "retention-$stamp.json")" -ForegroundColor Cyan
