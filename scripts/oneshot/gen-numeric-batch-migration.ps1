# 一次性｜锚点：ADR-038（各批 float 列转 decimal 的类型迁移）
<#
.SYNOPSIS
    一次性脚本：为指定业务域生成分片的类型迁移 SQL。

.DESCRIPTION
    ALTER COLUMN 会被列的 DEFAULT 约束与包含该列的索引阻塞（错误 5074），
    因此按表生成三段：拆除依赖 → 改类型 → 重建依赖。

    每 ChunkSize 张表一个迁移文件、各自事务提交：单脚本重写过多表会让数据文件与日志暴涨
    （批 A 实测触发错误 1105）。

    语义取自 docs/plans/numeric-role-full.csv；依赖对象取自 sys.*。
    前置条件：numeric-batch-prescan.ps1 对同一范围已确认无"超限"列。

.PARAMETER Prefixes
    表名前缀，如 MOC_,INV_,MOU_,SFC_,BOM_。

.PARAMETER StartNo
    起始迁移编号。

.PARAMETER ChunkSize
    每个迁移文件包含的表数，默认 8。

.PARAMETER Tag
    文件名标识，如 batch_b。
#>
param(
    [string[]] $Prefixes = @('COP_', 'PUR_'),
    [int] $StartNo = 359,
    [int] $ChunkSize = 8,
    [string] $Tag = 'batch_a'
)

$ErrorActionPreference = 'Stop'
try {
    [Console]::OutputEncoding = [Text.Encoding]::UTF8
    $root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    . (Join-Path $root 'scripts/dev/eos-sql.ps1')
    $migDir = Join-Path $root 'EOS.API/Data/Migrations'

    $specs = @{
        'AMOUNT'  = 'decimal(18,4)'
        'QTY'     = 'decimal(18,6)'
        'PRICE'   = 'decimal(18,6)'
        'RATE'    = 'decimal(18,10)'
        'PERCENT' = 'decimal(9,6)'
    }

    # 单列精度例外：仅在数据本身合法、只是量级超出本族整数位时才使用。
    # 若越界来自录错的值，先按"数据服从模型"修正数据（见迁移 386），不要放宽精度。
    $roleOverrides = @{}
    $Prefixes = @($Prefixes | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
    $conditions = ($Prefixes | ForEach-Object { "LEFT(o.name,$($_.Length))=N'$($_)'" }) -join ' OR '
    $scope = "($conditions)"

    $roleMap = @{}
    foreach ($r in (Import-Csv (Join-Path $root 'docs\plans\numeric-role-full.csv') -Encoding utf8)) {
        $roleMap["$($r.Table)|$($r.Column)"] = $r.Role
    }

    $colRaw = Invoke-EosSqlQuery -Query @"
SELECT o.name + N'|' + c.name + N'|' + CASE WHEN c.is_nullable = 1 THEN N'NULL' ELSE N'NOT NULL' END
FROM sys.columns c
JOIN sys.objects o ON c.object_id = o.object_id AND o.type = 'U'
JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE s.name = N'dbo' AND TYPE_NAME(c.user_type_id) = 'float' AND c.is_computed = 0 AND $scope
ORDER BY o.name, c.column_id;
"@

    $dcRaw = Invoke-EosSqlQuery -Query @"
SELECT o.name + N'|' + c.name + N'|' + dc.name + N'|' + dc.definition
FROM sys.default_constraints dc
JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
JOIN sys.objects o ON c.object_id = o.object_id
JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE s.name = N'dbo' AND TYPE_NAME(c.user_type_id) = 'float' AND $scope
ORDER BY o.name, c.name;
"@

    # 索引必须取该索引的全部列：键列通常不是 float，只按 float 筛会重建出 ON ... () 的空索引。
    # 必须包含主键与唯一约束——它们的键列同样可能是数值列（如 CLIENT_PRICE_D.PK 含 REBATE），
    # 漏掉会在 ALTER COLUMN 时报 5074。
    $idxRaw = Invoke-EosSqlQuery -Query @"
SELECT o.name + N'|' + i.name + N'|' + CAST(ic.is_included_column AS varchar(1)) + N'|'
     + c.name + N'|' + CAST(ic.key_ordinal AS varchar(3)) + N'|' + CAST(ic.index_column_id AS varchar(3))
     + N'|' + CAST(i.is_unique AS varchar(1)) + N'|' + ISNULL(i.filter_definition, N'')
     + N'|' + CAST(i.is_primary_key AS varchar(1)) + N'|' + CAST(i.is_unique_constraint AS varchar(1))
     + N'|' + CAST(i.type AS varchar(1))
FROM sys.index_columns ic
JOIN sys.indexes i ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
JOIN sys.objects o ON c.object_id = o.object_id
JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE s.name = N'dbo' AND i.type IN (1, 2)
  AND EXISTS (
      SELECT 1 FROM sys.index_columns ic2
      JOIN sys.columns c2 ON ic2.object_id = c2.object_id AND ic2.column_id = c2.column_id
      WHERE ic2.object_id = ic.object_id AND ic2.index_id = ic.index_id
        AND TYPE_NAME(c2.user_type_id) = 'float' AND $scope)
ORDER BY o.name, i.name, ic.is_included_column, ic.index_column_id;
"@

    $byTable = @{}
    foreach ($line in @($colRaw)) {
        $t = ([string]$line).Trim(); if (-not $t) { continue }
        $p = $t -split '\|'
        if (-not $byTable.ContainsKey($p[0])) { $byTable[$p[0]] = @{ Cols = @(); Constraint = @(); Index = @{} } }
        $role = $roleMap["$($p[0])|$($p[1])"]
        $target = if ($roleOverrides.ContainsKey("$($p[0])|$($p[1])")) { $roleOverrides["$($p[0])|$($p[1])"] } else { $specs[$role] }
        $byTable[$p[0]].Cols += [pscustomobject]@{ Column = $p[1]; Nullability = $p[2]; Role = $role; Target = $target }
    }
    foreach ($line in @($dcRaw)) {
        $t = ([string]$line).Trim(); if (-not $t) { continue }
        $p = $t -split '\|', 4
        if ($byTable.ContainsKey($p[0])) {
            $byTable[$p[0]].Constraint += [pscustomobject]@{ Column = $p[1]; Name = $p[2]; Definition = $p[3] }
        }
    }
    foreach ($line in @($idxRaw)) {
        $t = ([string]$line).Trim(); if (-not $t) { continue }
        $p = $t -split '\|', 12
        $tbl = $p[0]; $idx = $p[1]
        if (-not $byTable.ContainsKey($tbl)) { continue }
        if (-not $byTable[$tbl].Index.ContainsKey($idx)) {
            $byTable[$tbl].Index[$idx] = @{
                Keys = @(); Includes = @(); IsUnique = $p[6]; Filter = $p[7]
                IsPk = $p[8]; IsUq = $p[9]; Type = $p[10]
            }
        }
        if ($p[2] -eq '0') { $byTable[$tbl].Index[$idx].Keys += [pscustomobject]@{ Name = $p[3]; Ord = [int]$p[4] } }
        else { $byTable[$tbl].Index[$idx].Includes += [pscustomobject]@{ Name = $p[3]; Ord = [int]$p[5] } }
    }

    # 只保留至少有一列需要改类型的表（NON_NUMERIC / 未登记的列会得到空段）
    $tables = @($byTable.Keys | Where-Object { @($byTable[$_].Cols | Where-Object { $_.Target }).Count -gt 0 } | Sort-Object)
    $chunks = @()
    for ($i = 0; $i -lt $tables.Count; $i += $ChunkSize) {
        $chunks += , @($tables[$i..([Math]::Min($i + $ChunkSize - 1, $tables.Count - 1))])
    }

    Get-ChildItem $migDir -Filter "*${Tag}_numeric_types*.sql" | Remove-Item -Force

    $totalAlter = 0; $totalDrop = 0
    for ($ci = 0; $ci -lt $chunks.Count; $ci++) {
        $no = $StartNo + $ci
        $part = $ci + 1
        $lines = New-Object System.Collections.ArrayList
        [void]$lines.Add("-- $no：$Tag 数值列类型标准化 第 $part/$($chunks.Count) 批")
        [void]$lines.Add('-- 依据 docs/decisions/ADR-038-数值列的语义标准化.md 第 3 节的五族标准类型。')
        [void]$lines.Add('-- ALTER COLUMN 会被 DEFAULT 约束与包含该列的索引阻塞（错误 5074），故按表三段：拆依赖 → 改类型 → 重建。')
        [void]$lines.Add('-- 按表分片是为了避免单事务重写过多表导致文件组/日志暴涨（曾触发错误 1105）。')
        [void]$lines.Add('')

        foreach ($tbl in $chunks[$ci]) {
            $entry = $byTable[$tbl]
            [void]$lines.Add("-- ===== $tbl =====")
            foreach ($idxName in ($entry.Index.Keys | Sort-Object)) {
                $ix = $entry.Index[$idxName]
                if ($ix.IsPk -eq '1' -or $ix.IsUq -eq '1') {
                    [void]$lines.Add("ALTER TABLE [dbo].[$tbl] DROP CONSTRAINT [$idxName];")
                }
                else {
                    [void]$lines.Add("DROP INDEX [$idxName] ON [dbo].[$tbl];")
                }
                $totalDrop++
            }
            foreach ($c in $entry.Constraint) {
                [void]$lines.Add("ALTER TABLE [dbo].[$tbl] DROP CONSTRAINT [$($c.Name)];")
                $totalDrop++
            }
            foreach ($c in $entry.Cols) {
                if (-not $c.Target) { continue }
                [void]$lines.Add("ALTER TABLE [dbo].[$tbl] ALTER COLUMN [$($c.Column)] $($c.Target) $($c.Nullability);")
                $totalAlter++
            }
            foreach ($c in $entry.Constraint) {
                [void]$lines.Add("ALTER TABLE [dbo].[$tbl] ADD CONSTRAINT [$($c.Name)] DEFAULT $($c.Definition) FOR [$($c.Column)];")
            }
            foreach ($idxName in ($entry.Index.Keys | Sort-Object)) {
                $ix = $entry.Index[$idxName]
                $keyList = (($ix.Keys | Sort-Object Ord | ForEach-Object { "[$($_.Name)] ASC" }) -join ', ')
                $clustered = if ($ix.Type -eq '1') { 'CLUSTERED' } else { 'NONCLUSTERED' }
                if ($ix.IsPk -eq '1') {
                    $stmt = "ALTER TABLE [dbo].[$tbl] ADD CONSTRAINT [$idxName] PRIMARY KEY $clustered ($keyList)"
                }
                elseif ($ix.IsUq -eq '1') {
                    $stmt = "ALTER TABLE [dbo].[$tbl] ADD CONSTRAINT [$idxName] UNIQUE $clustered ($keyList)"
                }
                else {
                    $unique = if ($ix.IsUnique -eq '1') { 'UNIQUE ' } else { '' }
                    $stmt = "CREATE ${unique}${clustered} INDEX [$idxName] ON [dbo].[$tbl] ($keyList)"
                    if ($ix.Includes.Count -gt 0) {
                        $incList = (($ix.Includes | Sort-Object Ord | ForEach-Object { "[$($_.Name)]" }) -join ', ')
                        $stmt += " INCLUDE ($incList)"
                    }
                    if ($ix.Filter) { $stmt += " WHERE $($ix.Filter)" }
                }
                [void]$lines.Add($stmt + ';')
            }
            [void]$lines.Add('GO')
            [void]$lines.Add('')
        }

        # 元数据同步随片走：物理改 decimal 后 F_TYPE 必须同批改，否则精度校验仍跳过这些字段
        $tblList = ($chunks[$ci] | ForEach-Object { "N'$_'" }) -join ', '
        [void]$lines.Add('-- 同步本片各表的 F_TYPE（物理已是 decimal）')
        [void]$lines.Add('UPDATE f')
        [void]$lines.Add('SET f.F_TYPE = N''decimal''')
        [void]$lines.Add('FROM [dbo].[FIELDS] AS f')
        [void]$lines.Add("WHERE f.T_ID IN ($tblList)")
        [void]$lines.Add('  AND f.F_TYPE IN (N''float'', N''nvarchar'')')
        [void]$lines.Add('  AND EXISTS (')
        [void]$lines.Add('      SELECT 1 FROM sys.columns AS c')
        [void]$lines.Add('      JOIN sys.objects AS o ON c.object_id = o.object_id AND o.type = ''U''')
        [void]$lines.Add('      JOIN sys.schemas AS s ON o.schema_id = s.schema_id')
        [void]$lines.Add('      WHERE s.name = N''dbo'' AND o.name = f.T_ID AND c.name = f.F_ID')
        [void]$lines.Add('        AND TYPE_NAME(c.user_type_id) = ''decimal'');')
        [void]$lines.Add('GO')
        [void]$lines.Add('')

        $name = "${no}_${Tag}_numeric_types_${part}of$($chunks.Count).sql"
        $lines -join "`r`n" | Set-Content -Path (Join-Path $migDir $name) -Encoding utf8
        "  $name  <- $($chunks[$ci].Count) 表"
    }

    "TABLES=$($tables.Count)"
    "PARTS=$($chunks.Count)"
    "ALTER_COLUMN=$totalAlter"
    "DROP_DEPENDENCY=$totalDrop"
    exit 0
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
