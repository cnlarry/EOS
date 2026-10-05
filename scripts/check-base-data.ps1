#Requires -Version 7.0
<#
.SYNOPSIS
基础数据一致性门禁：模块清单、建库种子与库内实际三者必须一致。

.DESCRIPTION
基础数据（主档）有三个落点，任意两处漂移都会让"clone 后即可用"不成立：
  ① 清单 EOS.API.Tests/base-data-modules.json（模块号 / 主表 / 是否已种子）；
  ② 建库种子 db/bootstrap/25_base_data.sql（随 clone 生成规范值）；
  ③ 库内实际（模块存在、主表匹配、seeded 表确有行）。
本脚本逐条对齐，任一不符即 exit 1。需连库。

.EXAMPLE
pwsh scripts/check-base-data.ps1
pwsh scripts/check-base-data.ps1 -SelfTest   # 不连库，用合成清单做正反自检
#>
param([switch]$SelfTest, [switch]$SkipDb)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $root 'scripts/dev/eos-sql.ps1')

function Get-SeedTables {
    param([string]$SeedPath)
    $text = Get-Content -Raw -LiteralPath $SeedPath
    $tables = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($m in [regex]::Matches($text, 'INSERT\s+(?:INTO\s+)?dbo\.\[?([A-Za-z_][A-Za-z0-9_]*)\]?', 'IgnoreCase')) {
        [void]$tables.Add($m.Groups[1].Value.ToUpperInvariant())
    }
    return $tables
}

function Test-BaseDataConsistency {
    param([string]$ManifestPath, [string]$SeedPath, [switch]$NoDb)
    $failures = [System.Collections.Generic.List[string]]::new()
    $json = Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json
    $seedTables = Get-SeedTables -SeedPath $SeedPath
    foreach ($mod in @($json.modules)) {
        $id = [string]$mod.moduleId
        $table = ([string]$mod.table).ToUpperInvariant()
        $seeded = [bool]$mod.seeded
        if (-not $NoDb) {
            $rows = @(Invoke-EosSqlQuery -Query "SET NOCOUNT ON; SELECT CONCAT(ISNULL(MASTER_TABLE,''),'|',ISNULL(M_DESC,'')) FROM dbo.MODULES WHERE CAST(M_IDX AS varchar(20))='$id';")
            if ($rows.Count -eq 0) { $failures.Add("$id 模块不存在于 MODULES"); continue }
            $dbTable = (($rows[0] -split '\|')[0]).Trim().ToUpperInvariant()
            if ($dbTable -ne $table) { $failures.Add("$id 主表不符：清单=$table 库=$dbTable") }
        }
        if ($seeded -and -not $seedTables.Contains($table)) {
            $failures.Add("$id $table 标记 seeded，但 25_base_data.sql 未见 INSERT")
        }
        if (-not $seeded -and $seedTables.Contains($table)) {
            $failures.Add("$id $table 标记未 seeded，但种子里有 INSERT（清单应改为 seeded=true）")
        }
        if ($seeded -and -not $NoDb) {
            $cnt = [int]((Invoke-EosSqlQuery -Query "SET NOCOUNT ON; SELECT CAST(COUNT(*) AS varchar(20)) FROM dbo.[$table];") | Select-Object -First 1)
            if ($cnt -lt 1) { $failures.Add("$id $table 标记 seeded，但库内 0 行") }
        }
    }
    return $failures
}

$manifest = Join-Path $root 'EOS.API.Tests/base-data-modules.json'
$seed = Join-Path $root 'db/bootstrap/25_base_data.sql'

if ($SelfTest) {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) 'eos-basedata-selftest'
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null
    $goodManifest = Join-Path $tmp 'good.json'
    $badManifest = Join-Path $tmp 'bad.json'
    $tmpSeed = Join-Path $tmp 'seed.sql'
    '{ "modules": [ { "moduleId": "X1", "name": "n", "table": "T_GOOD", "seeded": true } ] }' |
        Set-Content -LiteralPath $goodManifest -Encoding utf8
    '{ "modules": [ { "moduleId": "X1", "name": "n", "table": "T_MISSING", "seeded": true } ] }' |
        Set-Content -LiteralPath $badManifest -Encoding utf8
    'INSERT dbo.T_GOOD (A) VALUES (1);' | Set-Content -LiteralPath $tmpSeed -Encoding utf8

    $okFails = @(Test-BaseDataConsistency -ManifestPath $goodManifest -SeedPath $tmpSeed -NoDb)
    $badFails = @(Test-BaseDataConsistency -ManifestPath $badManifest -SeedPath $tmpSeed -NoDb)
    $selfOk = ($okFails.Count -eq 0 -and $badFails.Count -eq 1)
    if (-not $selfOk) {
        Write-Host "FAIL -SelfTest 正反自检不符（正例 $($okFails.Count) 条 / 反例 $($badFails.Count) 条）"
        exit 1
    }
    Write-Host 'PASS -SelfTest 正反自检通过'
    exit 0
}

$failures = @(Test-BaseDataConsistency -ManifestPath $manifest -SeedPath $seed -NoDb:$SkipDb)
if ($failures.Count -gt 0) {
    Write-Host "FAIL 基础数据一致性：$($failures.Count) 处不符"
    $failures | ForEach-Object { Write-Host "  - $_" }
    exit 1
}
Write-Host 'PASS 基础数据一致性：清单 / 建库种子 / 库内实际三者一致'
exit 0
