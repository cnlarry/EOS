# EOS.IM local dev database initializer (dev-only helper)
#
# Reads the EOS.ERP connection string from the Codex config
# (~/.codex/config.toml, [mcp_servers.mssql-erp.env]), switches the initial
# catalog to EOS.IM, and executes the migration script under
# EOS.API/Data/Migrations. Use only in dev; the authoritative schema lives in
# the versioned SQL files, not in this script.

param(
    [string]$MigrationFile = '001_im_initial_schema.sql'
)

$ErrorActionPreference = 'Stop'

$repoRoot  = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$sqlPath   = Join-Path $repoRoot ("EOS.API\Data\Migrations\" + $MigrationFile)
$configPath = Join-Path $env:USERPROFILE '.codex\config.toml'

if (-not (Test-Path $sqlPath))    { throw "Migration script not found: $sqlPath" }
if (-not (Test-Path $configPath)) { throw "Codex config not found: $configPath" }

$cfg = Get-Content $configPath -Raw
$match = [regex]::Match($cfg, '\[mcp_servers\.mssql-erp\.env\][\s\S]*?MSSQL_ERP_CONN\s*=\s*"([^"]+)"')
if (-not $match.Success) { throw 'mssql-erp MSSQL_ERP_CONN not found in config.toml' }

$connStr = $match.Groups[1].Value -replace 'Database=EOS\.ERP', 'Database=EOS.IM'

$conn = New-Object System.Data.SqlClient.SqlConnection $connStr
$conn.Open()
try {
    $cmd = $conn.CreateCommand()
    $cmd.CommandTimeout = 120

    $cmd.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name LIKE 'im\_%' ESCAPE '\'"
    $existing = [int]$cmd.ExecuteScalar()
    Write-Host "Existing im_ tables: $existing"

    if ($existing -eq 0) {
        $bytes = [System.IO.File]::ReadAllBytes($sqlPath)
        $sql = [System.Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF)
        $batches = $sql -split '(?m)^\s*GO\s*$'
        $n = 0
        foreach ($b in $batches) {
            if ($b.Trim().Length -gt 0) {
                $cmd.CommandText = $b
                [void]$cmd.ExecuteNonQuery()
                $n++
            }
        }
        Write-Host "Executed batches: $n"
    } else {
        Write-Host 'Schema already exists, skipping DDL'
    }

    $cmd.CommandText = @"
SELECT t.name AS TableName, COUNT(c.column_id) AS ColumnCount
FROM sys.tables t JOIN sys.columns c ON c.object_id = t.object_id
WHERE t.name LIKE 'im\_%' ESCAPE '\'
GROUP BY t.name ORDER BY t.name
"@
    $reader = $cmd.ExecuteReader()
    while ($reader.Read()) { Write-Host ("  " + $reader['TableName'] + " (" + $reader['ColumnCount'] + " cols)") }
    $reader.Close()

    $cmd.CommandText = "SELECT COUNT(*) FROM sys.extended_properties WHERE name = N'MS_Description'"
    Write-Host ("MS_Description count: " + $cmd.ExecuteScalar())

    # Chinese sanity check via code points (hui4 = U+4F1A = 20250, hua4 =
    # U+8BDD = 35805) so the script stays ASCII-only and avoids
    # console-encoding pitfalls in WinPS 5.1.
    $cmd.CommandText = "SELECT COUNT(*) FROM sys.extended_properties WHERE name = N'MS_Description' AND CAST(value AS NVARCHAR(MAX)) LIKE N'%' + NCHAR(20250) + NCHAR(35805) + N'%'"
    Write-Host ("Remarks containing Chinese keyword: " + $cmd.ExecuteScalar())

    $cmd.CommandText = "SELECT COUNT(*) FROM sys.extended_properties WHERE name = N'MS_Description' AND class = 0"
    Write-Host ("  DB-level remarks: " + $cmd.ExecuteScalar())
    $cmd.CommandText = "SELECT COUNT(*) FROM sys.extended_properties WHERE name = N'MS_Description' AND class = 1 AND minor_id = 0"
    Write-Host ("  Table-level remarks: " + $cmd.ExecuteScalar())
    $cmd.CommandText = "SELECT COUNT(*) FROM sys.extended_properties WHERE name = N'MS_Description' AND class = 1 AND minor_id > 0"
    Write-Host ("  Column-level remarks: " + $cmd.ExecuteScalar())
    $cmd.CommandText = "SELECT COUNT(*) FROM sys.extended_properties WHERE name = N'MS_Description' AND class = 7"
    Write-Host ("  Index-level remarks: " + $cmd.ExecuteScalar())
}
finally {
    $conn.Close()
}
