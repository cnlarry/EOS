<#
.SYNOPSIS
    回滚报表归属列（REPORT.M_IDX）的落库：删列 + 卸约束 + 撤销迁移台账行。

.DESCRIPTION
    归属落库是纯增量（加列 + 回填），但一旦后续阶段按新列切换读路径、搬迁筛选条件，
    删列就会连带打断那些改动。因此本脚本**只允许在该迁移仍是最新一条时执行**：
    台账里存在编号更大的迁移即拒绝——那种情况下正确的做法是向前修，不是向后删。

    删除前先把当前归属映射整表导出留底（`logs/`），删列即删数据，无法事后重建。

.PARAMETER DryRun
    只做前置检查与留底，不执行删除。

.PARAMETER Force
    真正执行删除（未指定时等同 -DryRun，避免误触）。

.EXAMPLE
    pwsh scripts/rollback-report-attribution.ps1 -DryRun
    pwsh scripts/rollback-report-attribution.ps1 -Force
#>
[CmdletBinding()]
param(
    [switch] $DryRun,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
. (Join-Path $PSScriptRoot 'dev\eos-sql.ps1')

$Target = '267'
$ScriptName = 'EOS.API.Data.Migrations.267_report_attribution.sql'
$Backup = Join-Path (Split-Path -Parent $PSScriptRoot) 'logs\report-attribution-rolledback.csv'
$Execute = $Force -and -not $DryRun

function Get-Scalar {
    param([string] $Query)
    $rows = @(Invoke-EosSqlQuery -Query "SET NOCOUNT ON; $Query")
    if ($rows.Count -eq 0) { return '' }
    return $rows[0].Trim()
}

$db = Get-Scalar "SELECT DB_NAME();"
if ($db -ne 'EOS.ERP') {
    Write-Output "FAIL 只允许在 EOS.ERP 内执行，当前库为 $db。"
    exit 2
}

$hasColumn = Get-Scalar "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('dbo.REPORT') AND name='M_IDX';"
if ($hasColumn -eq '0') {
    Write-Output 'PASS 列 REPORT.M_IDX 不存在，无需回滚。'
    exit 0
}

# 只允许在"该迁移仍是最新一条"时回滚：台账里存在编号更大的脚本即拒绝。
# 台账里的脚本名形如 EOS.API.Data.Migrations.<编号>_<名称>.sql，取第一个「三位数字 + 下划线」的编号。
$laterCount = Get-Scalar "SELECT COUNT(*) FROM dbo.ERP_SCHEMA_JOURNAL WHERE TRY_CAST(SUBSTRING(ScriptName, PATINDEX('%[0-9][0-9][0-9]_%', ScriptName), 3) AS int) > $Target;"

Write-Output "== 报表归属列回滚 ==  库=$db  列存在=1  更大编号的迁移数=$laterCount"
if ($laterCount -ne '0') {
    Write-Output "FAIL 已存在编号大于 $Target 的迁移（$laterCount 条）：后续阶段可能已按新列切换读路径或搬迁条件，此时删列会打断它们。请向前修，不要向后删。"
    exit 1
}

# 留底：删列即删数据
$directory = Split-Path -Parent $Backup
if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
$rows = @(Invoke-EosSqlQuery -Query @"
SET NOCOUNT ON;
SELECT CONCAT(LTRIM(RTRIM(r.REPORT_ID)), '|', CAST(r.R_M_IDX AS varchar(20)), '|', CAST(ISNULL(r.Q_M_IDX, 0) AS varchar(20)), '|', CAST(r.M_IDX AS varchar(20)))
FROM dbo.REPORT r WITH (NOLOCK)
ORDER BY r.REPORT_ID;
"@ | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
@('reportId|R_M_IDX|Q_M_IDX|M_IDX') + $rows | Set-Content -LiteralPath $Backup -Encoding utf8
Write-Output "  留底：$($rows.Count) 行 → $Backup"

if (-not $Execute) {
    Write-Output '  （未指定 -Force，仅做检查与留底；加 -Force 才真正删除）'
    exit 0
}

$sql = @"
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_REPORT_MODULE')
    ALTER TABLE dbo.REPORT DROP CONSTRAINT FK_REPORT_MODULE;
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.REPORT') AND name = N'IX_REPORT_M_IDX')
    DROP INDEX IX_REPORT_M_IDX ON dbo.REPORT;
IF COL_LENGTH(N'dbo.REPORT', N'M_IDX') IS NOT NULL
    ALTER TABLE dbo.REPORT DROP COLUMN M_IDX;
DELETE FROM dbo.ERP_SCHEMA_JOURNAL WHERE ScriptName = N'$ScriptName';
IF COL_LENGTH(N'dbo.REPORT', N'M_IDX') IS NOT NULL
    THROW 55200, N'列 REPORT.M_IDX 仍然存在，回滚未生效。', 1;
IF EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE ScriptName = N'$ScriptName')
    THROW 55201, N'迁移台账行未被撤销。', 1;
COMMIT TRANSACTION;
"@
$temp = Join-Path ([System.IO.Path]::GetTempPath()) 'eos-rollback-267.sql'
[System.IO.File]::WriteAllText($temp, $sql, (New-Object System.Text.UTF8Encoding($false)))
try {
    $run = Invoke-EosSqlFile -Path $temp
    $run.Output | Where-Object { $_ -match '\S' } | ForEach-Object { Write-Output "  $_" }
    if ($run.ExitCode -ne 0) {
        Write-Output "FAIL 回滚执行失败 rc=$($run.ExitCode)"
        exit 1
    }
}
finally {
    Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
}

$leftover = Get-Scalar "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('dbo.REPORT') AND name='M_IDX';"
Write-Output ("PASS 列已删除（残留 $leftover；台账行已撤销；留底 $Backup）")
exit 0
