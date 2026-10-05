<#
.SYNOPSIS
    从当前库重建结构种子 `db/bootstrap/10_schema.sql`。

.DESCRIPTION
    建库种子是系统现状的镜像：日常开发只写 DbUp 迁移，**版本发布时收口**——用本脚本把库内
    的结构现状回灌种子。

    导出范围：用户表、视图、标量/表值函数、存储过程。排除系统对象，以及历史备份表
    （`_BAK` / `_Backup` / `_GHOST` 命名，迁移注释里明确「不得作为参考」）。

    ⚠ 必须与 `40_journal_baseline.sql` 同一批落地：基线登记扩到最新迁移编号后，新库启动时
    不再执行那些迁移，结构必须已由本脚本补齐；只扩基线不补结构，新库会缺表缺列。

    实现说明：脚本化由 SMO（`Microsoft.SqlServer.Management.Smo`）完成。SMO 是 .NET Framework
    程序集，PowerShell 7 下加载会报 `Microsoft.SqlServer.Server.SqlContext` 找不到，故内部以
    Windows PowerShell 5.1 子进程执行导出，本脚本自身仍用 pwsh 调用。

.PARAMETER OutFile
    输出路径，默认 `db/bootstrap/10_schema.sql`。

.PARAMETER DryRun
    只导出到 `.verify/` 并报告对象数，不覆盖种子。

.EXAMPLE
    pwsh scripts/export-bootstrap-schema.ps1 -DryRun
    pwsh scripts/export-bootstrap-schema.ps1
#>
[CmdletBinding()]
param(
    [string]$OutFile = 'db/bootstrap/10_schema.sql',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$repoRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path | Split-Path -Parent
if (-not [IO.Path]::IsPathRooted($OutFile)) { $OutFile = Join-Path $repoRoot $OutFile }

if (-not $env:MSSQL_ERP_CONN) { throw 'MSSQL_ERP_CONN 未设置：先 . scripts/dev/set-eos-env.ps1' }

$work = Join-Path $repoRoot '.verify'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$smoFile = Join-Path $work 'export-schema-smo.ps1'

$smoCode = @'
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[System.Reflection.Assembly]::LoadWithPartialName('Microsoft.SqlServer.Smo') | Out-Null
[System.Reflection.Assembly]::LoadWithPartialName('Microsoft.SqlServer.ConnectionInfo') | Out-Null
[System.Reflection.Assembly]::LoadWithPartialName('Microsoft.SqlServer.Management.Sdk.Sfc') | Out-Null

$b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($env:MSSQL_ERP_CONN)
$srv = New-Object Microsoft.SqlServer.Management.Smo.Server($b.DataSource)
$srv.ConnectionContext.LoginSecure = $false
$srv.ConnectionContext.Login = $b.UserID
$srv.ConnectionContext.Password = $b.Password
$db = $srv.Databases[$b.InitialCatalog]

$sc = New-Object Microsoft.SqlServer.Management.Smo.Scripter($srv)
$o = $sc.Options
$o.ScriptDrops         = $false
$o.IncludeIfNotExists  = $false
$o.Indexes             = $true
$o.ClusteredIndexes    = $true
$o.NonClusteredIndexes = $true
$o.DriAll              = $true
$o.NoCommandTerminator = $true
$o.NoCollation         = $true
$o.NoFileGroup         = $true
$o.SchemaQualify       = $true
$o.WithDependencies    = $false
$o.AnsiPadding         = $false
$o.Permissions         = $false
$o.ExtendedProperties  = $false
$o.Statistics          = $false

$excludePattern = '(_BAK|_Backup|Backup_|_GHOST|_bak)'

$objects = @()
foreach ($t in ($db.Tables              | Where-Object { -not $_.IsSystemObject -and $_.Name -notmatch $excludePattern })) { $objects += $t }
foreach ($v in ($db.Views               | Where-Object { -not $_.IsSystemObject -and $_.Name -notmatch $excludePattern })) { $objects += $v }
foreach ($f in ($db.UserDefinedFunctions | Where-Object { -not $_.IsSystemObject -and $_.Name -notmatch $excludePattern })) { $objects += $f }
foreach ($p in ($db.StoredProcedures    | Where-Object { -not $_.IsSystemObject -and $_.Name -notmatch $excludePattern })) { $objects += $p }

# 输出顺序分两段：
#   ① 表 —— 表间有外键引用，按集合顺序输出会让先建的表引用到还没建的表
#      （实测 'FK_ASSISTANT_MESSAGE_SESSION' 引用 'ASSISTANT_SESSION' 失败），
#      故走 SMO 的依赖拓扑序。
#   ② 视图 / 函数 / 存储过程 —— SQL Server **不把函数体、视图体里的引用登记为依赖**，
#      这些对象在依赖树里被判成「无依赖」而排到表之前，创建时就会找不到表
#      （实测某个遗留函数报「列名无效」）。它们创建时只要求被引用的表已存在，
#      因此统一放在表之后，彼此按名序即可。
# 库里坏死的遗留对象不进基线：它们当初能创建成功，只是因为引用的表当时还不存在
# （SQL Server 的延迟名称解析），表建好之后就一直在报错。收进基线会让整份结构脚本
# 在干净库上跑不完，而它们本身也没有任何可用性。
# 清单由 `sp_refreshsqlmodule` 全库扫一遍得出。当前为空——历史上仅有的两项
# 已由迁移 316 退役，退役依据留在该迁移里。
$skipObjects = @()

$tables = @($objects | Where-Object { $_.GetType().Name -eq 'Table' })
$others = @($objects | Where-Object { $_.GetType().Name -ne 'Table' -and $skipObjects -notcontains $_.Name })

$deps = $sc.DiscoverDependencies($tables, $true)
foreach ($node in $sc.WalkDependencies($deps)) {
    if ($null -eq $node.Urn) { continue }
    $obj = $null
    try { $obj = $srv.GetSmoObject($node.Urn) } catch { continue }
    if ($null -eq $obj) { continue }
    Write-Output 'GO'
    $sc.Script($obj) | ForEach-Object { $_ }
}
foreach ($obj in $others) {
    Write-Output 'GO'
    $sc.Script($obj) | ForEach-Object { $_ }
}
Write-Output 'GO'
'@

$enc5 = New-Object System.Text.UTF8Encoding($true)   # 5.1 读中文要 BOM
[IO.File]::WriteAllText($smoFile, $smoCode, $enc5)

$raw = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $smoFile 2>&1 | ForEach-Object { [string]$_ })
if ($LASTEXITCODE -ne 0) {
    $raw | Select-Object -Last 12 | ForEach-Object { Write-Output $_ }
    throw 'SMO 导出失败'
}

$tables = @($raw | Select-String -Pattern '^CREATE TABLE' ).Count
$views  = @($raw | Select-String -Pattern '^CREATE VIEW' ).Count
$funcs  = @($raw | Select-String -Pattern '^CREATE FUNCTION' ).Count
$procs  = @($raw | Select-String -Pattern '^CREATE PROCEDURE|^CREATE PROC\b' ).Count
Write-Output ("SMO 导出：表 $tables / 视图 $views / 函数 $funcs / 过程 $procs，共 $($raw.Count) 行")

$lf = [string][char]10
$out = New-Object System.Collections.Generic.List[string]
$out.Add('/*')
$out.Add(' * EOS 数据库结构')
$out.Add(' *')
$out.Add(' * 包含表、标量/表值函数、视图，以及应用运行时调用的存储过程。')
$out.Add(' * 本脚本只建立结构，不含任何业务数据；元数据种子见 20_metadata.sql。')
$out.Add(' *')
$out.Add(' * 由 scripts/export-bootstrap-schema.ps1 从当前库导出（SMO），不要手工编辑。')
$out.Add(' * 执行前请先建库并切换到目标库，见 00_create_database.sql。')
$out.Add(' */')
$out.Add('')
$out.Add('SET ANSI_NULLS ON;')
$out.Add('SET QUOTED_IDENTIFIER ON;')
$out.Add('GO')

foreach ($line in $raw) {
    if ($line -match '^SET ANSI_NULLS' -or $line -match '^SET QUOTED_IDENTIFIER') { continue }
    if ($line -match '^USE \[') { continue }
    $out.Add($line)
}

$text = ($out -join $lf) + $lf
$target = if ($DryRun) { Join-Path $work '10_schema.exported.sql' } else { $OutFile }
$enc = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText($target, $text, $enc)
Write-Output ("已写入 {0}（{1} 行）" -f $target, $out.Count)
