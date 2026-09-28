#Requires -Version 7.0
<#
.SYNOPSIS
生成开源版数据库初始化脚本：完整结构 + 元数据种子 + 空业务数据。

.DESCRIPTION
产物写入 publish/overlay/db/bootstrap/：
  00_create_database.sql     建库
  10_schema.sql              表 / 函数 / 视图 / 受控存储过程（纯结构）
  20_metadata.sql            元数据种子（脱敏）
  30_admin.sql               初始管理员（仓库内静态维护）
  40_journal_baseline.sql    迁移基线登记（DbUp 启动时不重跑）

结构来自连接的源库 sys.* 元数据。源库中引用已删除表/列的孤儿模块会被自动剔除：
10_schema.sql 生成后放入临时空库实测，失败的模块移出清单并重生成，循环至可在空库完整执行。

.PARAMETER SkipMetadata
只生成结构、建库脚本与 journal 基线。
.PARAMETER SkipSchema
只导出元数据种子。
.PARAMETER SkipVerify
跳过空库实测（默认开启实测）。

.EXAMPLE
.\scripts\export-oss-seed.ps1
.EXAMPLE
.\scripts\export-oss-seed.ps1 -SkipMetadata
#>
[CmdletBinding()]
param(
    [switch]$SkipMetadata,
    [switch]$SkipSchema,
    [switch]$SkipVerify
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$config = Import-PowerShellDataFile -LiteralPath (Join-Path $root 'publish/seed.psd1')
$migrationDir = Join-Path $root 'EOS.API/Data/Migrations'
$outDir = Join-Path $root ($config.OutputDir -replace '/', [IO.Path]::DirectorySeparatorChar)

function Write-Step { param([string]$Message) Write-Host "== $Message" -ForegroundColor Cyan }
function Write-Pass { param([string]$Message) Write-Host "PASS $Message" -ForegroundColor Green }
function Write-Warn { param([string]$Message) Write-Host "WARN $Message" -ForegroundColor Yellow }

function Test-NameMatch {
    param([string]$Name, [string[]]$Patterns)
    foreach ($pattern in $Patterns) { if ($Name -like $pattern) { return $true } }
    return $false
}

function ConvertTo-PublicText {
    <#
      对外文本清洗：去掉内部决策编号及其小节指针（如 ADR-008 §4、决策 2.4），
      并整理因此留下的空括号与重复标点。库内原值不动，只影响导出的脚本。
    #>
    param([string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    if ($Text -notmatch 'ADR-') { return $Text }
    $t = $Text
    $t = [regex]::Replace($t, 'ADR-\d+\s*(?:§\s*[\d.]+)?', '')
    $t = [regex]::Replace($t, '(?:决策|评审|背景|附录|批次)\s*[\d.A-Za-z/]*', '')
    $t = [regex]::Replace($t, '§\s*[\d./]*', '')
    $t = [regex]::Replace($t, '[（(]\s*[，,、；;]\s*', '（')
    $t = [regex]::Replace($t, '\s*[，,、；;]\s*[）)]', '）')
    $t = [regex]::Replace($t, '[（(]\s*[）)]', '')
    $t = [regex]::Replace($t, '([，,、；;])\s*\1', '$1')
    $t = [regex]::Replace($t, '[ \t]{2,}', ' ')
    return $t.Trim()
}

New-Item -ItemType Directory -Path $outDir -Force | Out-Null

# ================================================================ 连接
Write-Step '加载数据库连接'
. (Join-Path $root 'EOS.API.Tests/AcceptanceCommon.ps1')
$conn = Get-EosConn
if (-not $conn.Server) { throw '无法读取本地数据库连接串（EOS.API/appsettings.Development.json）。' }
Write-Pass "源库：$($conn.Server) / $($conn.Database)"

# 空库/端到端实测统一走托管客户端：与导出与查询路径同一实现，且不依赖 sqlcmd
# 与本机 Windows 身份验证；错误以异常/消息形式完整回传，便于定位失败语句。
. (Join-Path $root 'scripts/dev/eos-sql.ps1')
$target = Get-EosSqlTarget
$adminCs = $target.AdoConnectionString
$verifyDb = 'EOS_OSS_VERIFY'
$verifyCs = $adminCs -replace '(?i)(database=)[^;]*', "`${1}$verifyDb"

# ================================================================ Schema 主体
if (-not $SkipSchema) {
    Write-Step '生成结构脚本（源库 sys.* 元数据）'

    # 建库脚本
    $create = [System.Collections.Generic.List[string]]::new()
    $create.Add('/*')
    $create.Add(' * 建库入口')
    $create.Add(' *')
    $create.Add(' * 数据库名与连接串 Initial Catalog 一致（见 EOS.API/appsettings.Development.example.json）。')
    $create.Add(' * 中文环境建议使用中文排序规则；实例未安装时可改 SQL_Latin1_General_CP1_CI_AS。')
    $create.Add(' * 完整初始化步骤见 db/README.md。')
    $create.Add(' */')
    $create.Add('')
    $create.Add('USE [master];')
    $create.Add('GO')
    $create.Add('')
    $create.Add("IF DB_ID(N'EOS.ERP') IS NULL")
    $create.Add('BEGIN')
    $create.Add('    CREATE DATABASE [EOS.ERP] COLLATE Chinese_PRC_CI_AS;')
    $create.Add('END')
    $create.Add('GO')
    $create.Add('')
    $create.Add('USE [EOS.ERP];')
    $create.Add('GO')
    [IO.File]::WriteAllLines((Join-Path $outDir '00_create_database.sql'), $create, [Text.UTF8Encoding]::new($true))

    # 对象清单
    $objDt = Invoke-EosSqlTable -Query @"
SET NOCOUNT ON;
SELECT o.name AS OBJ_NAME, o.type
FROM sys.objects o
WHERE SCHEMA_NAME(o.schema_id) = 'dbo' AND o.type IN ('U','FN','TF','IF','V','P')
ORDER BY o.type, o.name;
"@
    $excludeTables = @($config.ExcludeTables)
    $tables = @(); $funcs = @(); $views = @(); $sprocs = @()
    foreach ($row in $objDt.Rows) {
        $name = [string]$row['OBJ_NAME']
        switch (([string]$row['type']).Trim()) {
            'U' { if (-not (Test-NameMatch -Name $name -Patterns $excludeTables)) { $tables += $name } }
            { $_ -in @('FN','TF','IF') } { $funcs += $name }
            'V' { $views += $name }
            'P' { if (Test-NameMatch -Name $name -Patterns @($config.IncludeSprocs)) { $sprocs += $name } }
        }
    }
    Write-Pass "对象清单：表 $($tables.Count) / 函数 $($funcs.Count) / 视图 $($views.Count) / 存储过程 $($sprocs.Count)"

    # 模块定义
    $moduleDefs = @{}
    $modDt = Invoke-EosSqlTable -Query @"
SET NOCOUNT ON;
SELECT o.name, m.definition
FROM sys.objects o
JOIN sys.sql_modules m ON m.object_id = o.object_id
WHERE SCHEMA_NAME(o.schema_id) = 'dbo'
  AND (o.type IN ('FN','TF','IF','V')
       OR (o.type = 'P' AND o.name IN (
           SELECT name FROM (VALUES
               ('P_HRM_WAGE_CALC')
               -- 批核旧过程已随效果引擎接管全部退役，库里已无对象，导出清单不再登记
               
               
           ) AS t(name)
       )));
"@
    foreach ($row in $modDt.Rows) { $moduleDefs[[string]$row['name']] = [string]$row['definition'] }
    Write-Pass "模块定义：$($moduleDefs.Count) 个"

    # ---- 建表数据 ----
    function ConvertTo-TypeDecl {
        param([string]$TypeName, [int]$MaxLength, [int]$Precision, [int]$Scale)
        switch ($TypeName.ToLowerInvariant()) {
            'nvarchar'  { if ($MaxLength -eq -1) { return 'nvarchar(max)' } return "nvarchar($([int]($MaxLength / 2)))" }
            'varchar'   { if ($MaxLength -eq -1) { return 'varchar(max)' } return "varchar($MaxLength)" }
            'nchar'     { return "nchar($([int]($MaxLength / 2)))" }
            'char'      { return "char($MaxLength)" }
            'decimal'   { return "decimal($Precision, $Scale)" }
            'numeric'   { return "numeric($Precision, $Scale)" }
            'datetime2' { return "datetime2($Scale)" }
            'datetimeoffset' { return "datetimeoffset($Scale)" }
            'time'      { return "time($Scale)" }
            'varbinary' { if ($MaxLength -eq -1) { return 'varbinary(max)' } return "varbinary($MaxLength)" }
            'binary'    { return "binary($MaxLength)" }
            # vector 必须带维度声明（裸 vector 建表会报「找不到数据类型 vector」）；
            # 维度按 SQL Server 的存储布局反推：max_length = 维度 * 4 + 8 字节头。
            'vector'    { if ($MaxLength -gt 8) { return "vector($([int](($MaxLength - 8) / 4)))" } return 'vector' }
            default     { return $TypeName }
        }
    }

    # 所有 U 表 + 列（一次查询带出表名）
    $colsDt = Invoke-EosSqlTable -Query @"
SET NOCOUNT ON;
SELECT o.name AS TABLE_NAME, c.object_id, c.name AS COL_NAME,
       TYPE_NAME(c.user_type_id) AS TYPE_NAME, c.max_length, c.precision, c.scale,
       c.is_identity, c.is_computed, c.is_nullable,
       ISNULL(dc.definition,'') AS DEFAULT_DEF, ISNULL(cc.definition,'') AS COMPUTED_DEF
FROM sys.columns c
JOIN sys.objects o ON c.object_id = o.object_id AND o.type = 'U'
LEFT JOIN sys.default_constraints dc ON c.default_object_id = dc.object_id
LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
WHERE SCHEMA_NAME(o.schema_id) = 'dbo'
ORDER BY o.name, c.column_id;
"@
    $colsByTable = @{}      # oid -> List[pscustomobject]
    $tableOidByName = @{}   # name -> oid
    foreach ($row in $colsDt.Rows) {
        $oid = [long]$row['object_id']
        $tname = [string]$row['TABLE_NAME']
        if (-not $colsByTable.ContainsKey($oid)) { $colsByTable[$oid] = [System.Collections.Generic.List[object]]::new() }
        if (-not $tableOidByName.ContainsKey($tname)) { $tableOidByName[$tname] = $oid }
        $colsByTable[$oid].Add([pscustomobject]@{
                Oid = $oid
                Name = [string]$row['COL_NAME']
                TypeDecl = (ConvertTo-TypeDecl -TypeName ([string]$row['TYPE_NAME']) -MaxLength ([int]$row['max_length']) -Precision ([int]$row['precision']) -Scale ([int]$row['scale']))
                IsId = [bool]$row['is_identity']
                IsComputed = [bool]$row['is_computed']
                Nullable = [bool]$row['is_nullable']
                Default = [string]$row['DEFAULT_DEF']
                Computed = [string]$row['COMPUTED_DEF']
            })
    }
    Write-Pass "列元数据：$($colsByTable.Count) 张表"

    # 主键
    $pkDt = Invoke-EosSqlTable -Query @"
SET NOCOUNT ON;
SELECT tc.parent_object_id, COL_NAME(tc.parent_object_id, kc.column_id) AS COL_NAME, kc.key_ordinal
FROM sys.key_constraints tc
JOIN sys.index_columns kc ON tc.parent_object_id = kc.object_id AND tc.unique_index_id = kc.index_id
WHERE tc.type = 'PK';
"@
    $pkByTable = @{}
    foreach ($row in $pkDt.Rows) {
        $oid = [long]$row['parent_object_id']
        if (-not $pkByTable.ContainsKey($oid)) { $pkByTable[$oid] = [System.Collections.Generic.List[object]]::new() }
        $pkByTable[$oid].Add([pscustomobject]@{ Name = [string]$row['COL_NAME']; Order = [int]$row['key_ordinal'] })
    }

    # 唯一约束
    $uqDt = Invoke-EosSqlTable -Query @"
SET NOCOUNT ON;
SELECT tc.parent_object_id, tc.name AS CONSTRAINT_NAME,
       COL_NAME(tc.parent_object_id, kc.column_id) AS COL_NAME, kc.key_ordinal
FROM sys.key_constraints tc
JOIN sys.index_columns kc ON tc.parent_object_id = kc.object_id AND tc.unique_index_id = kc.index_id
WHERE tc.type = 'UQ';
"@
    $uqByTable = @{}
    foreach ($row in $uqDt.Rows) {
        $oid = [long]$row['parent_object_id']
        if (-not $uqByTable.ContainsKey($oid)) { $uqByTable[$oid] = @{} }
        $cname = [string]$row['CONSTRAINT_NAME']
        if (-not $uqByTable[$oid].ContainsKey($cname)) { $uqByTable[$oid][$cname] = [System.Collections.Generic.List[object]]::new() }
        $uqByTable[$oid][$cname].Add([pscustomobject]@{ Name = [string]$row['COL_NAME']; Order = [int]$row['key_ordinal'] })
    }

    # Check
    $ckDt = Invoke-EosSqlTable -Query @"
SET NOCOUNT ON;
SELECT cc.parent_object_id, cc.name AS CONSTRAINT_NAME, ISNULL(cc.definition,'') AS CONSTRAINT_DEF
FROM sys.check_constraints cc;
"@
    $ckByTable = @{}
    foreach ($row in $ckDt.Rows) {
        $oid = [long]$row['parent_object_id']
        if (-not $ckByTable.ContainsKey($oid)) { $ckByTable[$oid] = [System.Collections.Generic.List[object]]::new() }
        $ckByTable[$oid].Add([pscustomobject]@{ Name = [string]$row['CONSTRAINT_NAME']; Def = [string]$row['CONSTRAINT_DEF'] })
    }

    # 外键
    $fkDt = Invoke-EosSqlTable -Query @"
SET NOCOUNT ON;
SELECT fk.name AS FK_NAME, fk.parent_object_id, fk.referenced_object_id,
       COL_NAME(fkc.parent_object_id, fkc.parent_column_id) AS PARENT_COL,
       COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) AS REF_COL,
       fkc.constraint_column_id, fk.delete_referential_action_desc
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
ORDER BY fk.name, fkc.constraint_column_id;
"@
    $fkPairs = [System.Collections.Generic.List[object]]::new()
    foreach ($row in $fkDt.Rows) {
        $fkPairs.Add([pscustomobject]@{
                Name = [string]$row['FK_NAME']
                ParentOid = [long]$row['parent_object_id']
                RefOid = [long]$row['referenced_object_id']
                PC = [string]$row['PARENT_COL']
                RC = [string]$row['REF_COL']
                Delete = ([string]$row['delete_referential_action_desc']).Replace('_ACTION','')
            })
    }

    # 非聚簇非唯一索引
    $ixDt = Invoke-EosSqlTable -Query @"
SET NOCOUNT ON;
SELECT i.object_id, i.name AS INDEX_NAME, i.type_desc, ISNULL(i.filter_definition,'') AS FILTER_DEF,
       ic.key_ordinal, COL_NAME(ic.object_id, ic.column_id) AS COL_NAME, ic.is_descending_key, ic.is_included_column
FROM sys.indexes i
JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
JOIN sys.objects o ON i.object_id = o.object_id
WHERE i.type > 0 AND i.is_primary_key = 0 AND i.is_unique_constraint = 0 AND i.is_unique = 0
  AND SCHEMA_NAME(o.schema_id) = 'dbo'
ORDER BY i.object_id, i.index_id, ic.key_ordinal;
"@
    $ixCols = @{}      # "oid|name" -> List[ordered]
    $ixFilters = @{}   # "oid|name" -> filter
    foreach ($row in $ixDt.Rows) {
        $oid = [long]$row['object_id']
        $ixName = [string]$row['INDEX_NAME']
        $key = "$oid|$ixName"
        if (-not $ixCols.ContainsKey($key)) {
            $ixCols[$key] = [System.Collections.Generic.List[object]]::new()
            $ixFilters[$key] = [string]$row['FILTER_DEF']
        }
        $ixCols[$key].Add([pscustomobject]@{
                Name = [string]$row['COL_NAME']
                Order = [int]$row['key_ordinal']
                Desc = [bool]$row['is_descending_key']
                Included = [bool]$row['is_included_column']
            })
    }

    # ---- 构造函数（均直接读取本作用域变量，不使用嵌套作用域）----
    function Add-TableBlock {
        param($Lines, [string]$TableName)
        if (-not $script:tableOidByName.ContainsKey($TableName)) { Write-Warn "跳过：$TableName（无列定义）"; return }
        $oid = $script:tableOidByName[$TableName]
        $cols = $script:colsByTable[$oid]
        $L = [System.Collections.Generic.List[string]]::new()
        $L.Add("CREATE TABLE dbo.[$TableName] (")
        foreach ($col in $cols) {
            if ($col.IsComputed) {
                $L.Add("    [$($col.Name)] AS $($col.Computed),")
            } else {
                $cd = "    [$($col.Name)] $($col.TypeDecl)"
                if ($col.IsId) { $cd += ' IDENTITY(1,1)' }
                $cd += if ($col.Nullable) { ' NULL' } else { ' NOT NULL' }
                if ($col.Default) { $cd += " CONSTRAINT [DF_$($col.Name)_$($col.Oid)] DEFAULT $($col.Default)" }
                $L.Add("$cd,")
            }
        }
        if ($script:pkByTable.ContainsKey($oid)) {
            $pkNames = @($script:pkByTable[$oid] | Sort-Object Order | ForEach-Object { "[$($_.Name)]" })
            $L.Add("    CONSTRAINT [PK_$TableName] PRIMARY KEY CLUSTERED (" + ($pkNames -join ', ') + '),')
        }
        if ($script:uqByTable.ContainsKey($oid)) {
            foreach ($un in ($script:uqByTable[$oid].Keys | Sort-Object)) {
                $uCols = @($script:uqByTable[$oid][$un] | Sort-Object Order | ForEach-Object { "[$($_.Name)]" })
                $L.Add("    CONSTRAINT [$un] UNIQUE (" + ($uCols -join ', ') + '),')
            }
        }
        if ($script:ckByTable.ContainsKey($oid)) {
            foreach ($ck in $script:ckByTable[$oid]) {
                $L.Add("    CONSTRAINT [$($ck.Name)] CHECK $($ck.Def),")
            }
        }
        $last = $L[$L.Count - 1].TrimEnd()
        if ($last.EndsWith(',')) { $L[$L.Count - 1] = $last.TrimEnd(',') }
        $Lines.Add('')
        $Lines.Add(('-' * 78))
        $Lines.Add("-- $TableName")
        $Lines.Add(('-' * 78))
        $Lines.Add($L[0])
        for ($i = 1; $i -lt $L.Count; $i++) { $Lines.Add($L[$i]) }
        $Lines.Add(');')
        $Lines.Add('GO')
        # 索引
        foreach ($key in ($script:ixCols.Keys | Where-Object { $_ -like "$oid|*" })) {
            $cols2 = @($script:ixCols[$key] | Where-Object { -not $_.Included } | Sort-Object Order | ForEach-Object {
                    $p = "[$($_.Name)]"; if ($_.Desc) { $p += ' DESC' }; $p })
            $inc = @($script:ixCols[$key] | Where-Object { $_.Included } | Sort-Object Order | ForEach-Object { "[$($_.Name)]" })
            if ($cols2.Count -eq 0) { continue }
            $ixName = ($key -split '\|')[1]
            $clause = "CREATE NONCLUSTERED INDEX [$ixName] ON dbo.[$TableName] (" + ($cols2 -join ', ') + ')'
            if ($inc.Count -gt 0) { $clause += ' INCLUDE (' + ($inc -join ', ') + ')' }
            if ($script:ixFilters[$key]) { $clause += " WHERE $($script:ixFilters[$key])" }
            $Lines.Add("$clause;")
            $Lines.Add('GO')
        }
    }

    function Add-FkBlock {
        param($Lines)
        $nameByOid = @{}
        foreach ($oid in $script:tableOidByName.Values) {
            $q = Invoke-EosSqlTable -Query "SELECT name FROM sys.objects WHERE object_id = $oid;"
            if ($q.Rows.Count -gt 0) { $nameByOid[$oid] = [string]$q.Rows[0][0] }
        }
        $grouped = @{}
        foreach ($f in $script:fkPairs) {
            $gkey = "$($f.ParentOid)|$($f.Name)"
            if (-not $grouped.ContainsKey($gkey)) {
                $grouped[$gkey] = [ordered]@{ Parent = $f.ParentOid; Ref = $f.RefOid; Name = $f.Name; Delete = $f.Delete; Pairs = [System.Collections.Generic.List[object]]::new() }
            }
            $grouped[$gkey].Pairs.Add([pscustomobject]@{ P = $f.PC; R = $f.RC })
        }
        if ($grouped.Count -eq 0) { return }
        $Lines.Add('')
        $Lines.Add(('-' * 78))
        $Lines.Add("-- 外键（$($grouped.Count)）")
        $Lines.Add(('-' * 78))
        $Lines.Add('')
        foreach ($gkey in ($grouped.Keys | Sort-Object)) {
            $g = $grouped[$gkey]
            if (-not $nameByOid.ContainsKey($g.Parent) -or -not $nameByOid.ContainsKey($g.Ref)) { continue }
            $pairs = @($g.Pairs | ForEach-Object { "[$($_.P)]" })
            $rpairs = @($g.Pairs | ForEach-Object { "[$($_.R)]" })
            $del = if ($g.Delete -match 'CASCADE|SET NULL|SET DEFAULT') { " ON DELETE $($g.Delete)" } else { '' }
            $Lines.Add("ALTER TABLE dbo.[$($nameByOid[$g.Parent])] WITH CHECK ADD CONSTRAINT [$($g.Name)] FOREIGN KEY (" + ($pairs -join ', ') + ") REFERENCES dbo.[$($nameByOid[$g.Ref])] (" + ($rpairs -join ', ') + ")$del;")
        }
        $Lines.Add('GO')
    }

    function Add-ModuleBlock {
        param($Lines, [string]$Title, [string[]]$Names)
        $sorted = @($Names | Sort-Object)
        if ($sorted.Count -eq 0) { return }
        $Lines.Add('')
        $Lines.Add(('-' * 78))
        $Lines.Add("-- $Title")
        $Lines.Add(('-' * 78))
        $Lines.Add('')
        foreach ($name in $sorted) {
            if (-not $script:moduleDefs.ContainsKey($name)) { Write-Warn "无定义：$name"; continue }
            # 模块定义 = 版权注释头 + CREATE 语句。注释头内容（公司/作者/日期/描述）
            # 一律不发布，因此只保留从首个 CREATE/ALTER 开始的部分；
            # 之后的内容（含块注释与行注释）原样保留，不触碰配对关系。
            $def = $script:moduleDefs[$name]
            $stmt = [regex]::Match($def, '(?im)^\s*(CREATE|ALTER)\s+(FUNCTION|PROCEDURE|PROC|VIEW|TRIGGER)\b')
            if ($stmt.Success -and $stmt.Index -gt 0) {
                $def = $def.Substring($stmt.Index)
            }
            # 定义内部若仍有 Change Log 等历史行注释则剔除（单行，安全）
            $defLines = $def -split "`n"
            $kept = [System.Collections.Generic.List[string]]::new()
            $sensitiveRx = '(?i)change log|initialize by|it_micro|dongan|zensee|honghui|client_b|client_c|create date|modify date|copyright|author\s*:|creator'
            foreach ($dl in $defLines) {
                $trim = $dl.Trim()
                if ($trim.StartsWith('--') -and $trim -match $sensitiveRx) { continue }
                $kept.Add($dl)
            }
            $def = ($kept -join "`n").Trim()
            # 定义内的行注释同样去掉内部决策编号引用
            $def = ($def -split "`n" | ForEach-Object {
                    if ($_.TrimStart().StartsWith('--')) { ConvertTo-PublicText $_ } else { $_ }
                }) -join "`n"
            $Lines.Add("-- $name")
            $Lines.Add($def.TrimEnd())
            $Lines.Add('GO')
            $Lines.Add('')
        }
    }

    # ---- 生成 10_schema.sql ----
    $schemaPath = Join-Path $outDir '10_schema.sql'
    $skipped = [System.Collections.Generic.List[string]]::new()

    function New-SchemaLines {
        param([string[]]$FuncNames, [string[]]$ViewNames, [string[]]$SprocNames)
        $lines = [System.Collections.Generic.List[string]]::new()
        $lines.Add('/*')
        $lines.Add(' * EOS 数据库结构')
        $lines.Add(' *')
        $lines.Add(' * 包含表、标量/表值函数、视图，以及应用运行时调用的存储过程。')
        $lines.Add(' * 本脚本只建立结构，不含任何业务数据；元数据种子见 20_metadata.sql。')
        $lines.Add(' *')
        $lines.Add(' * 执行前请先建库并切换到目标库，见 00_create_database.sql。')
        $lines.Add(' */')
        $lines.Add('')
        $lines.Add('SET ANSI_NULLS ON;')
        $lines.Add('SET QUOTED_IDENTIFIER ON;')
        $lines.Add('GO')
        $lines.Add('')
        foreach ($t in ($script:tables | Sort-Object)) { Add-TableBlock -Lines $lines -TableName $t }
        Add-FkBlock -Lines $lines
        Add-ModuleBlock -Lines $lines -Title '标量/表值函数' -Names $FuncNames
        Add-ModuleBlock -Lines $lines -Title '视图' -Names $ViewNames
        Add-ModuleBlock -Lines $lines -Title '存储过程' -Names $SprocNames
        return $lines
    }

    function Test-InEmptyDb {
        $sql = "IF DB_ID('$verifyDb') IS NOT NULL BEGIN ALTER DATABASE [$verifyDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$verifyDb]; END; CREATE DATABASE [$verifyDb];"
        Invoke-EosManagedQuery -ConnectionString $adminCs -Sql $sql | Out-Null
        return $verifyDb
    }

    function Invoke-SchemaInVerifyDb {
        # 逐批执行并记录「哪一个对象」失败：SQL Server 对 CREATE VIEW/FUNCTION 的
        # 列名错误不回带对象名，只能靠文件里 `-- <对象名>` 注释定位批次归属。
        $scriptText = Get-Content -Raw -LiteralPath $schemaPath
        $batches = [regex]::Split($scriptText, '(?im)^\s*GO\s*$') | Where-Object { $_.Trim() -ne '' }
        $failedObjects = [System.Collections.Generic.List[string]]::new()
        $unattributed = [System.Collections.Generic.List[string]]::new()
        $output = [System.Collections.Generic.List[string]]::new()
        $connection = New-Object System.Data.SqlClient.SqlConnection $verifyCs
        try {
            $connection.Open()
            foreach ($batch in $batches) {
                $command = $connection.CreateCommand()
                $command.CommandText = $batch
                $command.CommandTimeout = 300
                try {
                    $reader = $command.ExecuteReader()
                    try { while ($reader.Read()) { } } finally { $reader.Dispose() }
                } catch {
                    $message = $_.Exception.Message
                    $output.Add($message)
                    $nameMatch = [regex]::Match($batch, '(?m)^--\s*([A-Za-z_][A-Za-z0-9_]*)')
                    if ($nameMatch.Success) { $failedObjects.Add($nameMatch.Groups[1].Value) }
                    else { $unattributed.Add(($message + '  ← 批次首行：' + (($batch.Trim() -split "`n")[0]))) }
                } finally {
                    $command.Dispose()
                }
            }
        } finally {
            $connection.Dispose()
        }
        return @{ FailedObjects = @($failedObjects); Unattributed = @($unattributed); Output = @($output) }
    }

    $schemaPassed = $false
    if ($SkipVerify) {
        Write-Warn '跳过空库实测（-SkipVerify），产物可能含孤儿模块'
        $lines = New-SchemaLines -FuncNames $funcs -ViewNames $views -SprocNames $sprocs
        [IO.File]::WriteAllLines($schemaPath, $lines, [Text.UTF8Encoding]::new($true))
        $schemaPassed = $true
    } else {
        Write-Step '空库实测收敛（孤儿模块自动剔除）'
        $funcsEx = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $viewsEx = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $sprocsEx = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $round = 0
        while ($round -lt 10) {
            $round++
            $fn = @($funcs | Where-Object { -not $funcsEx.Contains($_) })
            $vw = @($views | Where-Object { -not $viewsEx.Contains($_) })
            $sp = @($sprocs | Where-Object { -not $sprocsEx.Contains($_) })
            $lines = New-SchemaLines -FuncNames $fn -ViewNames $vw -SprocNames $sp
            [IO.File]::WriteAllLines($schemaPath, $lines, [Text.UTF8Encoding]::new($true))
            [void](Test-InEmptyDb)
            $result = Invoke-SchemaInVerifyDb
            if ($result.FailedObjects.Count -eq 0 -and $result.Unattributed.Count -eq 0) {
                Write-Pass "空库实测通过（第 $round 轮）"
                $schemaPassed = $true
                break
            }
            if ($result.Unattributed.Count -gt 0) {
                # 表/索引等非模块对象失败无法靠剔除收敛，直接判失败。
                throw "空库实测第 $round 轮存在非模块对象失败，已中止：`n$([string]::Join("`n", ($result.Unattributed | Select-Object -First 5)))"
            }
            $anyNew = $false
            foreach ($name in $result.FailedObjects) {
                if ($funcsEx.Add($name)) { $anyNew = $true }
                if ($viewsEx.Add($name)) { $anyNew = $true }
                if ($sprocsEx.Add($name)) { $anyNew = $true }
            }
            if (-not $anyNew) {
                throw "空库实测第 $round 轮失败且无新剔除对象，已中止：$([string]::Join('、', $result.FailedObjects))"
            }
            Write-Host "第 $round 轮剔除：$([string]::Join('、', $result.FailedObjects))"
        }
        if (-not $schemaPassed) { throw "空库实测在 10 轮内未通过，已中止：$schemaPath" }
        foreach ($name in ($funcsEx + $viewsEx + $sprocsEx)) { $skipped.Add($name) }
        if ($skipped.Count -gt 0) {
            Write-Warn "剔除 $($skipped.Count) 个无法在空库创建的对象：$([string]::Join('、', ($skipped | Sort-Object)))"
            $skipPath = Join-Path $root 'publish/report/skipped-modules.txt'
            New-Item -ItemType Directory -Path (Split-Path -Parent $skipPath) -Force | Out-Null
            [IO.File]::WriteAllLines($skipPath, ($skipped | Sort-Object), [Text.UTF8Encoding]::new($false))
        }
    }
    Write-Pass "10_schema.sql：$([math]::Round(([IO.File]::ReadAllText($schemaPath)).Length / 1KB)) KB"
}

# ================================================================ 迁移基线
Write-Step '生成迁移 journal 基线'
$migrationFiles = @(Get-ChildItem -LiteralPath $migrationDir -Filter *.sql | Sort-Object Name)
$journal = [System.Collections.Generic.List[string]]::new()
$journal.Add('/*')
$journal.Add(' * 迁移基线登记')
$journal.Add(' *')
$journal.Add(' * 10_schema.sql 已包含下列迁移的最终结构，因此在此预先登记到 DbUp 的日志表，')
$journal.Add(' * 使应用启动时不会重复执行它们。应用会自动创建 ERP_SCHEMA_JOURNAL；')
$journal.Add(' * 这里显式建表以便在启动前完成登记。')
$journal.Add(' */')
$journal.Add('')
$journal.Add('IF OBJECT_ID(N''dbo.ERP_SCHEMA_JOURNAL'', N''U'') IS NULL')
$journal.Add('BEGIN')
$journal.Add('    CREATE TABLE dbo.ERP_SCHEMA_JOURNAL (')
$journal.Add('        [schemaversionid] INT IDENTITY(1,1) NOT NULL,')
$journal.Add('        [scriptname]      NVARCHAR(255)     NOT NULL,')
$journal.Add('        [applied]         DATETIME          NOT NULL,')
$journal.Add('        CONSTRAINT [PK_ERP_SCHEMA_JOURNAL_id] PRIMARY KEY CLUSTERED ([schemaversionid])')
$journal.Add('    );')
$journal.Add('END;')
$journal.Add('GO')
$journal.Add('')
$baselineMax = 0
foreach ($file in $migrationFiles) {
    if ($file.BaseName -match '^(\d+)_') {
        $num = [int]$Matches[1]
        if ($num -gt $baselineMax) { $baselineMax = $num }
    }
    $resourceName = "EOS.API.Data.Migrations.$($file.Name)"
    $escaped = $resourceName.Replace("'", "''")
    $journal.Add("IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'$escaped')")
    $journal.Add("    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'$escaped', GETDATE());")
}
$journal.Add('GO')
$journalPath = Join-Path $outDir '40_journal_baseline.sql'
[IO.File]::WriteAllLines($journalPath, $journal, [Text.UTF8Encoding]::new($true))
Write-Pass "40_journal_baseline.sql：登记 $($migrationFiles.Count) 个迁移（最大编号 $baselineMax）"

$publishConfigPath = Join-Path $root 'publish/publish.psd1'
$publishConfig = Import-PowerShellDataFile -LiteralPath $publishConfigPath
if ([int]$publishConfig.MigrationBaseline -ne $baselineMax) {
    Write-Warn "publish.psd1 的 MigrationBaseline = $($publishConfig.MigrationBaseline)，与实际最大迁移编号 $baselineMax 不一致，请同步更新。"
}

# ================================================================ 元数据
if ($SkipMetadata) {
    Write-Warn '已跳过元数据导出（-SkipMetadata）'
    Write-Host ''
    Write-Pass "产物目录：$outDir"
    exit 0
}

Write-Step '导出元数据种子'

$metadataTables = @($config.MetadataTables)
$auditPersons = @($config.AuditPersonColumns)
$auditDates = @($config.AuditDateColumns)
$scrubToNullConfig = $config.ScrubToNull
$scrubToTextConfig = $config.ScrubToText
$batchRows = [int]$config.BatchRows

$columnsDt = Invoke-EosSqlTable -Query @'
SET NOCOUNT ON;
SELECT o.name AS TABLE_NAME, c.name AS COLUMN_NAME, TYPE_NAME(c.user_type_id) AS DATA_TYPE,
       c.is_identity, c.is_computed, c.is_nullable
FROM sys.columns c
JOIN sys.objects o ON c.object_id = o.object_id AND o.type = 'U'
WHERE SCHEMA_NAME(o.schema_id) = 'dbo'
ORDER BY o.name, c.column_id;
'@
$columnsByTable = @{}
foreach ($row in $columnsDt.Rows) {
    $table = [string]$row['TABLE_NAME']
    if (-not $columnsByTable.ContainsKey($table)) { $columnsByTable[$table] = [System.Collections.Generic.List[object]]::new() }
    $columnsByTable[$table].Add([pscustomobject]@{
            Name = [string]$row['COLUMN_NAME']
            Type = ([string]$row['DATA_TYPE']).ToLowerInvariant()
            IsIdentity = [bool]$row['is_identity']
            IsComputed = [bool]$row['is_computed']
            IsNullable = [bool]$row['is_nullable']
        })
}
$rowCountDt = Invoke-EosSqlTable -Query @'
SET NOCOUNT ON;
SELECT o.name AS TABLE_NAME, SUM(p.rows) AS ROW_COUNT
FROM sys.objects o
JOIN sys.partitions p ON o.object_id = p.object_id AND p.index_id IN (0,1)
WHERE SCHEMA_NAME(o.schema_id) = 'dbo' AND o.type = 'U'
GROUP BY o.name;
'@
$rowCounts = @{}
foreach ($row in $rowCountDt.Rows) { $rowCounts[[string]$row['TABLE_NAME']] = [long]$row['ROW_COUNT'] }

function Format-SqlLiteral {
    param($Value, [string]$Type)
    if ($null -eq $Value -or $Value -is [DBNull]) { return 'NULL' }
    switch ($Type) {
        { $_ -in @('bit') } { return ([bool]$Value ? '1' : '0') }
        { $_ -in @('int','bigint','smallint','tinyint','decimal','numeric','float','real','money','smallmoney') } { return ([string]$Value) }
        { $_ -in @('datetime','datetime2','smalldatetime','date','time','datetimeoffset') } { return "'" + ([datetime]$Value).ToString('yyyy-MM-ddTHH:mm:ss.fff') + "'" }
        { $_ -in @('uniqueidentifier') } { return "'" + ([string]$Value) + "'" }
        { $_ -in @('varbinary','binary','image','rowversion','timestamp') } {
            $bytes = [byte[]]$Value
            return if ($bytes.Length -eq 0) { '0x' } else { '0x' + [BitConverter]::ToString($bytes).Replace('-','') }
        }
        default {
            $escaped = (ConvertTo-PublicText ([string]$Value)).Replace("'", "''")
            return "N'$escaped'"
        }
    }
}

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('/*')
$lines.Add(' * 元数据种子')
$lines.Add(' *')
$lines.Add(' * 表与字段登记、菜单模块、通用查询列与条件、报表与表单版式、流程定义。')
$lines.Add(' * 系统的界面完全由这些元数据驱动；不含任何业务数据。')
$lines.Add(' *')
$lines.Add(' * 幂等：每张表先清空再写入，可重复执行。')
$lines.Add(' */')
$lines.Add('')
$lines.Add('SET NOCOUNT ON;')
$lines.Add('GO')
$lines.Add('')

$exported = [System.Collections.Generic.List[object]]::new()
$scrubbedCells = 0

# 依赖父表（有外键入向引用的元数据表）先导，避免种子 INSERT 命中外键
$topoOrder = [System.Collections.Generic.List[string]]::new()
$remaining = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($t in $metadataTables) { [void]$remaining.Add($t) }
$fkParent = @{
    'FIELD_DATASOURCE' = @('FIELDS')
    'ASSISTANT_MESSAGE' = @('ASSISTANT_SESSION')
    'AUDIT_FIELD_CHANGE' = @('AUDIT_EVENT')
}
$seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
while ($remaining.Count -gt 0) {
    $progressed = $false
    foreach ($t in @($remaining)) {
        $parents = @()
        if ($fkParent.ContainsKey($t)) { $parents = @($fkParent[$t] | Where-Object { $metadataTables -contains $_ }) }
        $blocked = $false
        foreach ($p in $parents) {
            if ($metadataTables -contains $p -and -not $seen.Contains($p)) { $blocked = $true; break }
        }
        if ($blocked) { continue }
        $topoOrder.Add($t); [void]$seen.Add($t); [void]$remaining.Remove($t); $progressed = $true
    }
    if (-not $progressed) { foreach ($t in @($remaining)) { $topoOrder.Add($t); [void]$remaining.Remove($t) }; break }
}

foreach ($table in $topoOrder) {
    if (-not $columnsByTable.ContainsKey($table)) {
        Write-Warn "元数据表在库中不存在，跳过：$table"
        continue
    }
    $cols = @($columnsByTable[$table] | Where-Object { -not $_.IsComputed })
    $colNames = @($cols | Select-Object -ExpandProperty Name)
    $selectList = ($colNames | ForEach-Object { "[$_]" }) -join ', '
    $dt = Invoke-EosSqlTable -Query "SET NOCOUNT ON; SELECT $selectList FROM dbo.[$table];"
    $scrubToNull = $null
    if ($scrubToNullConfig.ContainsKey($table)) { $scrubToNull = $scrubToNullConfig[$table] }
    $scrubToText = $null
    if ($scrubToTextConfig.ContainsKey($table)) { $scrubToText = $scrubToTextConfig[$table] }
    $hasIdentity = @($cols | Where-Object { $_.IsIdentity }).Count -gt 0

    # 敏感行剔除：任一字段命中客户/公司痕迹即整行跳过，避免业务痕迹进入公开元数据
    $sensitiveRegexes = @($config.SensitiveRowPatterns | ForEach-Object { [Regex]::new($_) })
    $droppedSensitive = 0
    $keptRows = 0

    $lines.Add(('-' * 78))
    $lines.Add("-- $table（总 $($dt.Rows.Count) 行，剔除 $droppedSensitive）")
    $lines.Add(('-' * 78))
    $lines.Add("DELETE FROM dbo.[$table];")
    if ($hasIdentity) { $lines.Add("SET IDENTITY_INSERT dbo.[$table] ON;") }

    $valueRows = [System.Collections.Generic.List[string]]::new()
    foreach ($row in $dt.Rows) {
        # 全字段敏感探测（仅文本列）
        $isSensitive = $false
        foreach ($col in $cols) {
            if ($isSensitive) { break }
            $v = $row[$col.Name]
            if ($null -eq $v -or $v -is [DBNull]) { continue }
            if ($col.Type -notin @('varchar','nvarchar','char','nchar')) { continue }
            $s = [string]$v
            foreach ($rx in $sensitiveRegexes) {
                if ($rx.IsMatch($s)) { $isSensitive = $true; $droppedSensitive++; break }
            }
        }
        if ($isSensitive) { continue }
        $keptRows++

        $cells = foreach ($col in $cols) {
            $value = $row[$col.Name]
            # 仅对可空列做置空清洗；NOT NULL 列保留原值（其库值即为真值且导出行能被接受）
            $nullable = $col.IsNullable
            if ($nullable -and $col.Name -in $auditDates) { $value = $null }
            elseif ($nullable -and $col.Name -in $auditPersons) {
                $value = if ($null -eq $row[$col.Name] -or $row[$col.Name] -is [DBNull]) { $null } else { 'admin' }
            } elseif ($nullable -and $scrubToNull -and $scrubToNull.ContainsKey($col.Name)) {
                $text = if ($value -is [DBNull]) { '' } else { [string]$value }
                if ($text -and $text -match $scrubToNull[$col.Name]) { $value = $null; $scrubbedCells++ }
            } elseif ($scrubToText -and $scrubToText.ContainsKey($col.Name)) {
                $text = if ($value -is [DBNull]) { '' } else { [string]$value }
                if ($text) {
                    $replaced = [regex]::Replace($text, $scrubToText[$col.Name], '')
                    $replaced = $replaced -replace '[\s;；，,]{2,}', ';' -replace '^\s*[;；]+\s*', '' -replace '^(MODI_URL|M_URL|NEW_URL|HELP_URL)：?(;|；|$)', ''
                    if ($replaced -ne $text) {
                        $text = $replaced.Trim()
                        if ($text -match '(?i)\.aspx') {
                            # 一次替换后仍有路径残留则整体置空，防止部分清洗产生坏格式
                            $text = ''
                        }
                        if ($text.Length -gt 0) { $value = $text } else { $value = $null }
                        $scrubbedCells++
                    }
                }
            }
            Format-SqlLiteral -Value $value -Type $col.Type
        }
        $valueRows.Add('(' + ($cells -join ', ') + ')')
    }
    $insertPrefix = "INSERT dbo.[$table] (" + (($colNames | ForEach-Object { "[$_]" }) -join ', ') + ') VALUES'
    for ($i = 0; $i -lt $valueRows.Count; $i += $batchRows) {
        $chunk = @($valueRows[$i..([Math]::Min($i + $batchRows - 1, $valueRows.Count - 1))])
        $lines.Add($insertPrefix)
        for ($j = 0; $j -lt $chunk.Count; $j++) {
            $suffix = if ($j -eq $chunk.Count - 1) { ';' } else { ',' }
            $lines.Add('    ' + $chunk[$j] + $suffix)
        }
    }
    if ($hasIdentity) { $lines.Add("SET IDENTITY_INSERT dbo.[$table] OFF;") }
    $lines.Add('GO')
    $lines.Add('')
    $exported.Add([pscustomobject]@{ Table = $table; Rows = $keptRows; Dropped = $droppedSensitive })
    Write-Host "  $table：$keptRows 行（剔除 $droppedSensitive）" -ForegroundColor Gray
}

$metadataPath = Join-Path $outDir '20_metadata.sql'
[IO.File]::WriteAllLines($metadataPath, $lines, [Text.UTF8Encoding]::new($true))
$totalRows = ($exported | Measure-Object -Property Rows -Sum).Sum
Write-Pass "20_metadata.sql：$($exported.Count) 张表，$totalRows 行，清洗 $scrubbedCells 个单元格"

Write-Step '未登记表检查'
$known = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($name in @($metadataTables) + @($config.DictionaryTables) + @($config.ExcludeTables)) { [void]$known.Add($name) }
$unregList = [System.Collections.Generic.List[object]]::new()
foreach ($table in ($rowCounts.Keys | Sort-Object)) {
    if (-not $known.Contains($table) -and $rowCounts[$table] -gt 0) {
        $unregList.Add([pscustomobject]@{ Table = $table; Rows = $rowCounts[$table] })
    }
}
$unregistered = @($unregList | Sort-Object Rows -Descending)
if ($unregistered.Count -eq 0) {
    Write-Pass '无未登记且有数据的表'
} else {
    Write-Warn "$($unregistered.Count) 张表在库中有数据但未登记，将以空表发布："
    foreach ($item in ($unregistered | Select-Object -First 40)) {
        Write-Host ("     {0,-40} {1,10} 行" -f $item.Table, $item.Rows) -ForegroundColor Yellow
    }
    if ($unregistered.Count -gt 40) { Write-Host "     …其余 $($unregistered.Count - 40) 张省略" -ForegroundColor Yellow }
}

# ================================================================ 端到端实测
# 结构 + 元数据 + 账号 + 迁移基线按序落到干净库，并断言数据口径；
# 任一项不通过即判失败——产物建不出库或带业务数据，都不应发布。
if (-not $SkipVerify -and -not $SkipMetadata) {
    Write-Step '端到端实测（结构 + 元数据 + 账号 + 迁移基线）'
    [void](Test-InEmptyDb)
    $totalErrors = 0
    foreach ($name in @('10_schema.sql', '20_metadata.sql', '30_admin.sql', '40_journal_baseline.sql')) {
        $path = Join-Path $outDir $name
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $r = Invoke-EosManagedFile -ConnectionString $verifyCs -Path $path -NoErrorStop
        if ($r.ExitCode -ne 0) {
            $totalErrors += $r.ExitCode
            Write-Warn "$name 在验证库执行时报 $($r.ExitCode) 个错误："
            Write-Host ($r.Output | Select-Object -Last 5)
        }
    }
    $probe = @"
SET NOCOUNT ON;
SELECT N'迁移登记=' + CAST(COUNT(*) AS varchar(10)) FROM dbo.ERP_SCHEMA_JOURNAL;
SELECT N'管理员账号=' + CAST(COUNT(*) AS varchar(10)) FROM dbo.SYSDL WHERE USER_ID = 'admin';
SELECT N'模块数=' + CAST(COUNT(*) AS varchar(10)) FROM dbo.MODULES;
SELECT N'字段数=' + CAST(COUNT(*) AS varchar(10)) FROM dbo.FIELDS;
SELECT N'业务表合计=' + CAST(ISNULL(SUM(p.rows), 0) AS varchar(10))
FROM sys.objects o JOIN sys.partitions p ON o.object_id = p.object_id AND p.index_id IN (0,1)
WHERE o.type = 'U' AND o.name IN ('COP_ORDER_M','COP_ORDER_D','PUR_PURCHASE_M','PUR_RECEIVE_D','MOC_PRODUCE_M','INV_PRO_DEPOT','PRODUCT','AUDIT_EVENT');
"@
    $probeOut = Invoke-EosManagedQuery -ConnectionString $verifyCs -Sql $probe
    foreach ($line in $probeOut) { Write-Host "     $line" -ForegroundColor Gray }
    $journal = (($probeOut | Where-Object { $_ -like '迁移登记=*' }) -replace '.*=', '')
    $biz = (($probeOut | Where-Object { $_ -like '业务表合计=*' }) -replace '.*=', '')
    $admin = (($probeOut | Where-Object { $_ -like '管理员账号=*' }) -replace '.*=', '')
    if ($totalErrors -gt 0) { throw "端到端实测失败：脚本执行共报 $totalErrors 个错误。" }
    if ([int]$journal -ne $migrationFiles.Count) { throw "端到端实测失败：迁移登记 $journal 条，期望 $($migrationFiles.Count) 条。" }
    if ([int]$admin -ne 1) { throw "端到端实测失败：管理员账号 $admin 个，期望 1 个。" }
    if ([int]$biz -ne 0) { throw "端到端实测失败：抽样业务表合计 $biz 行，期望 0 行。" }
    Write-Pass "端到端实测通过（迁移登记 $journal 条、管理员 $admin 个、抽样业务表 $biz 行）"
    Invoke-EosManagedQuery -ConnectionString $adminCs -Sql "IF DB_ID('$verifyDb') IS NOT NULL BEGIN ALTER DATABASE [$verifyDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$verifyDb]; END;" | Out-Null
    Write-Pass "验证库 $verifyDb 已清理"
}

Write-Host ''
Write-Pass "产物目录：$outDir"
