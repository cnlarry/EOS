<#
.SYNOPSIS
    迁移脚本与 DbUp 台账的一致性门禁：幽灵名、重复行、未落库。

.DESCRIPTION
    DbUp 以「嵌入资源名」判断脚本是否已执行，资源名由文件名推导
    （EOS.API/Data/Migrations/<文件名>.sql → EOS.API.Data.Migrations.<文件名>.sql），
    登记在 dbo.ERP_SCHEMA_JOURNAL。所以文件名与台账必须一一对应：

      · 幽灵名（台账有、磁盘无）：改名或删除脚本时没有同步台账。后果是下次启动时该脚本
        按新名重跑一次（新名不在台账里，被判为「未执行」）。属事故，本检查失败。
      · 重复行（同一脚本名多行）：并行 DbUp 实例或手工登记留下的噪音，本检查失败。
      · 未落库（磁盘有、台账无）：新写的迁移在下次启动前即处于此状态，仅提示。

.PARAMETER ConnectionString
    业务库连接串；缺省依次取环境变量 MSSQL_ERP_CONN、集成认证 localhost/EOS.ERP。

.PARAMETER SelfTest
    正反自检：用构造数据验证判定逻辑本身，不连库。

.EXAMPLE
    pwsh scripts/check-migration-journal.ps1
#>
[CmdletBinding()]
param(
    [string] $ConnectionString,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'

$repoRoot       = Split-Path -Parent $PSScriptRoot
$migrationDir   = Join-Path $repoRoot 'EOS.API/Data/Migrations'
$resourcePrefix = 'EOS.API.Data.Migrations.'

function Get-MigrationResourceNames {
    <# 磁盘上的迁移脚本 → DbUp 资源名（与嵌入资源命名规则一致）。 #>
    param([string] $Directory, [string] $Prefix)
    if (-not (Test-Path -LiteralPath $Directory)) { throw "迁移目录不存在：$Directory" }
    return @(Get-ChildItem -LiteralPath $Directory -Filter '*.sql' -File |
        ForEach-Object { $Prefix + $_.Name })
}

function Compare-MigrationJournal {
    <#
      纯函数：输入磁盘资源名与台账脚本名（可含重复），输出三类差异。
      与库、与文件系统解耦，便于自检。
    #>
    param(
        [string[]] $DiskNames,
        [string[]] $JournalNames
    )
    $diskSet    = @($DiskNames | Sort-Object -Unique)
    $journalSet = @($JournalNames | Sort-Object -Unique)
    return [pscustomobject]@{
        Ghost      = @($journalSet | Where-Object { $diskSet -notcontains $_ })
        Missing    = @($diskSet | Where-Object { $journalSet -notcontains $_ })
        Duplicates = @($JournalNames | Group-Object | Where-Object { $_.Count -gt 1 } |
            ForEach-Object { "$($_.Name) x$($_.Count)" })
    }
}

if ($SelfTest) {
    $failures = New-Object System.Collections.Generic.List[string]
    $check = {
        param($name, $expected, $actual)
        if ($expected -ne $actual) { $failures.Add("$name 期望 $expected 实际 $actual") }
    }

    # 磁盘比台账多：新迁移未落库属正常，不算失败
    $r1 = Compare-MigrationJournal -DiskNames @('a.sql', 'b.sql', 'c.sql') -JournalNames @('a.sql', 'b.sql')
    & $check '一致场景幽灵数' 0 $r1.Ghost.Count
    & $check '一致场景未落库数' 1 $r1.Missing.Count
    & $check '一致场景重复数' 0 $r1.Duplicates.Count

    # 台账比磁盘多：改名未同步，必须失败
    $r2 = Compare-MigrationJournal -DiskNames @('a.sql') -JournalNames @('a.sql', 'ghost.sql')
    & $check '幽灵场景幽灵数' 1 $r2.Ghost.Count
    & $check '幽灵场景未落库数' 0 $r2.Missing.Count

    # 同一脚本多行
    $r3 = Compare-MigrationJournal -DiskNames @('a.sql') -JournalNames @('a.sql', 'a.sql')
    & $check '重复场景重复数' 1 $r3.Duplicates.Count

    if ($failures.Count -gt 0) {
        $failures | ForEach-Object { Write-Host "  [FAIL] $_" }
        Write-Host 'SELFTEST FAIL'
        exit 1
    }
    Write-Host 'SELFTEST PASS（3 场景 6 断言）'
    exit 0
}

. (Join-Path $PSScriptRoot 'dev/eos-sql.ps1')

$query = @{ Query = 'SELECT ScriptName FROM dbo.ERP_SCHEMA_JOURNAL' }
if ($ConnectionString) { $query.ConnectionString = $ConnectionString }

$diskNames = Get-MigrationResourceNames -Directory $migrationDir -Prefix $resourcePrefix
$journalNames = @(Invoke-EosSqlQuery @query | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })

$result = Compare-MigrationJournal -DiskNames $diskNames -JournalNames $journalNames

Write-Host "迁移脚本 $($diskNames.Count) 个；台账记录 $($journalNames.Count) 行。"
if ($result.Missing.Count -gt 0) {
    Write-Host "未落库（下次启动会自动执行）：$($result.Missing.Count) 个"
}
if ($result.Ghost.Count -gt 0) {
    Write-Host "幽灵名（台账有、磁盘无）：$($result.Ghost.Count) 个"
    $result.Ghost | Select-Object -First 20 | ForEach-Object { Write-Host "  - $_" }
}
if ($result.Duplicates.Count -gt 0) {
    Write-Host "重复行：$($result.Duplicates.Count) 个脚本"
    $result.Duplicates | Select-Object -First 20 | ForEach-Object { Write-Host "  - $_" }
}

if ($result.Ghost.Count -gt 0 -or $result.Duplicates.Count -gt 0) {
    Write-Host 'FAIL：文件与台账不一致——被改名的脚本会在下次启动时重跑。'
    exit 1
}

Write-Host 'PASS：文件与台账一一对应。'
exit 0
