<#
.SYNOPSIS
    把 dbo.SYSDF（旧操作日志表）整表导出为 CSV，供迁移 325 删表后考古备查。

.DESCRIPTION
    SYSDF 是旧系统的操作流水（2007-11-14 ~ 2026-09-05，11134 行），AUDIT_EVENT v2 上线后
    WorkbenchAuditWriter 已明文停写、AuditController 也不再查它。用户 2026-10-06 拍板：
    **导出 CSV 备查后整表退役**（见 Migrations/325_retire_dead_modules_and_tables.sql）。

    导出必须发生在 325 落地**之前**（脚本本身只读，不会改任何数据）。

    **归档落在 `logs/archive/retire-324/`，不进开源仓库**（`logs/` 在 .gitignore 里）：
    这批数据是给本机/内网考古用的副本，用户明确"留在本地，不进仓库"。脚本本身（无数据）
    留在 `scripts/`，所以换台机器照样能重跑一遍导出。

    CSV 用 RFC 4180 口径（含逗号/引号/换行的字段加引号、引号翻倍），UTF-8 带 BOM（Excel 直接打开不乱码）。

.EXAMPLE
    pwsh scripts/export-sysdf-archive.ps1
#>
[CmdletBinding()]
param(
    [string] $ConnectionString,
    [string] $OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'logs\archive\retire-324')
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

. (Join-Path $PSScriptRoot 'dev\eos-sql.ps1')
$target = Get-EosSqlTarget -ConnectionString $ConnectionString
$cs = $target.AdoConnectionString

# 列顺序显式写死（不用 SELECT *）：归档文件的列序必须可复现
$columns = @('LOG_IDX', 'M_IDX', 'RECORD_IDX', 'CONTENT', 'TYPE', 'EXEC_BY', 'EXEC_DATE', 'CI', 'OPERFLAG')

if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
$OutFile = Join-Path $OutDir 'SYSDF-2007-11-14_to_2026-09-05.csv'

function ConvertTo-CsvField([object] $value) {
    if ($null -eq $value -or $value -is [DBNull]) { return '' }
    # 忠实转写：工期时间戳去掉毫秒以外都一样，bit 写成 1/0（与 SSMS 显示一致，不用 True/False）。
    # 字符串一律不 Trim：nchar 定长列的尾随空格是**库里真实存的字节**，归档要照原样留。
    $text = if ($value -is [bool]) { if ($value) { '1' } else { '0' } }
            elseif ($value -is [datetime]) { ([datetime]$value).ToString('yyyy-MM-dd HH:mm:ss') }
            else { [string]$value }
    if ($text -match '[",\r\n]') { return '"' + $text.Replace('"', '""') + '"' }
    return $text
}

$connection = New-Object System.Data.SqlClient.SqlConnection $cs
$connection.Open()
try {
    $command = $connection.CreateCommand()
    $command.CommandTimeout = 300
    $command.CommandText = "SET NOCOUNT ON; SELECT $($columns -join ',') FROM dbo.SYSDF ORDER BY LOG_IDX;"

    $reader = $command.ExecuteReader()
    $writer = New-Object System.IO.StreamWriter($OutFile, $false, (New-Object System.Text.UTF8Encoding $true))
    try {
        $writer.WriteLine(($columns -join ','))
        $count = 0
        while ($reader.Read()) {
            $fields = for ($i = 0; $i -lt $columns.Count; $i++) { ConvertTo-CsvField $reader.GetValue($i) }
            $writer.WriteLine(($fields -join ','))
            $count++
        }
    }
    finally {
        $writer.Dispose()
        $reader.Dispose()
    }
}
finally {
    $connection.Dispose()
}

$file = Get-Item $OutFile
Write-Output "导出完成：$($file.FullName)"
Write-Output "  行数（不含表头）：$count"
Write-Output "  字节：$($file.Length)"
Write-Output "  SHA-256：$((Get-FileHash $OutFile -Algorithm SHA256).Hash)"
Write-Output "  （归档在 logs/ 下，不进开源仓库；导出后把新的行数/时间范围/SHA-256 抄回同目录 README 与迁移 325 的守卫）"
