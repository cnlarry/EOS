<#
.SYNOPSIS
    重建迁移基线登记 `db/bootstrap/40_journal_baseline.sql`。

.DESCRIPTION
    建库种子是系统现状的镜像。收口时除了把结构与元数据回灌种子，还要把**已固化的迁移**
    登记进 `db.ERP_SCHEMA_JOURNAL`——新库执行完种子就已经等于当前状态，那些迁移不该再跑；
    登记后应用启动时只会执行编号更大的新迁移。

    ⚠ 必须与 `10_schema.sql` / `20_metadata.sql` 同一批落地。只扩登记不补结构与元数据，
    新库会因为「迁移不跑、种子又没有」而缺表缺数据；反之只补种子不扩登记，迁移会在已经
    对齐的库上重跑，多数迁移的守卫是按「有事可做」写的（例如 274 要求删除集恰为 181），
    面对空集会直接抛错、应用起不来。

.PARAMETER OutFile
    输出路径，默认 `db/bootstrap/40_journal_baseline.sql`。

.PARAMETER MigrationsDir
    迁移目录，默认 `EOS.API/Data/Migrations`。

.PARAMETER DryRun
    只报告将登记的条数与编号范围，不写文件。

.EXAMPLE
    pwsh scripts/export-bootstrap-baseline.ps1 -DryRun
    pwsh scripts/export-bootstrap-baseline.ps1
#>
[CmdletBinding()]
param(
    [string]$OutFile = 'db/bootstrap/40_journal_baseline.sql',
    [string]$MigrationsDir,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$repoRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path | Split-Path -Parent
if (-not [IO.Path]::IsPathRooted($OutFile)) { $OutFile = Join-Path $repoRoot $OutFile }
if (-not $MigrationsDir) { $MigrationsDir = Join-Path $repoRoot 'EOS.API/Data/Migrations' }
if (-not (Test-Path -LiteralPath $MigrationsDir)) { throw "找不到迁移目录：$MigrationsDir" }

# 登记范围 = **当前库台账里已有的迁移**：它们的最终结果已由本次收口写进 `10_schema.sql`
# 与 `20_metadata.sql`，所以新库不该重跑。磁盘上更新的迁移（收口之后才加的）不登记，
# 新库会照常执行它们，从而与开发库最终一致。
. "$PSScriptRoot/dev/eos-sql.ps1"
$rows = Invoke-EosSqlQuery -Query 'SELECT [ScriptName] FROM dbo.ERP_SCHEMA_JOURNAL ORDER BY [Id]'
$resourceNames = @($rows | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($resourceNames.Count -eq 0) {
    throw '库台账为空：本脚本用于「收口」，要在已有数据的开发库上运行，不要对着新库跑。'
}

$onDisk = @(Get-ChildItem -LiteralPath $MigrationsDir -Filter '*.sql' -File | ForEach-Object { 'EOS.API.Data.Migrations.' + $_.Name })
$pending = @($onDisk | Where-Object { $resourceNames -notcontains $_ })
Write-Output ("将登记 {0} 个已固化迁移；磁盘另有 {1} 个未固化，留给新库执行" -f $resourceNames.Count, $pending.Count)
$pending | ForEach-Object { Write-Output ("  未固化（不登记）：" + $_) }

$lf = [string][char]10
$out = New-Object System.Collections.Generic.List[string]
$out.Add('/*')
$out.Add(' * 迁移基线登记')
$out.Add(' *')
$out.Add(' * 10_schema.sql 与 20_metadata.sql 已包含下列迁移的最终结果，因此在此预先登记到 DbUp 的')
$out.Add(' * 日志表，使应用启动时不会重复执行它们。应用会自动创建 ERP_SCHEMA_JOURNAL；这里显式')
$out.Add(' * 建表以便在启动前完成登记。')
$out.Add(' *')
$out.Add(' * 本文件由 scripts/export-bootstrap-baseline.ps1 依当前迁移目录生成，新增迁移后重跑即可。')
$out.Add(' */')
$out.Add('')
$out.Add("IF OBJECT_ID(N'dbo.ERP_SCHEMA_JOURNAL', N'U') IS NULL")
$out.Add('BEGIN')
$out.Add('    CREATE TABLE dbo.ERP_SCHEMA_JOURNAL (')
$out.Add('        [schemaversionid] INT IDENTITY(1,1) NOT NULL,')
$out.Add('        [scriptname]      NVARCHAR(255)     NOT NULL,')
$out.Add('        [applied]         DATETIME          NOT NULL,')
$out.Add('        CONSTRAINT [PK_ERP_SCHEMA_JOURNAL_id] PRIMARY KEY CLUSTERED ([schemaversionid])')
$out.Add('    );')
$out.Add('END;')
$out.Add('GO')
$out.Add('')

foreach ($name in $resourceNames) {
    $out.Add("IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'$name')")
    $out.Add("    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'$name', GETDATE());")
}
$out.Add('')

if ($DryRun) {
    Write-Output ("（-DryRun：未写文件；预计 {0} 行）" -f $out.Count)
    Write-Output ("首个：{0}" -f $resourceNames[0])
    Write-Output ("末个：{0}" -f $resourceNames[-1])
    exit 0
}

$enc = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText($OutFile, ($out -join $lf) + $lf, $enc)
Write-Output ("已写入 {0}（{1} 行）" -f $OutFile, $out.Count)
