param(
    [string]$Server = 'localhost',
    [string]$Database = 'EOS.ERP',
    [switch]$Diagnose
)

<#
    单据关系效果边登记器（FIELD_RELATION 方案 A，迁移 058 后运行）。
    - 读取 79 个模块 OP 的 MATCH_STRUCT（含 1607 试点），解析“目标表 + 键列序列”；
    - 把每种定位组合幂等写入 FIELD_RELATION（RELATION_KIND=EFFECT，RELATION_ID 边组 + KEY_ORDINAL）；
    - 本脚本只登记“效果定位边”，不改写 MODULE_BUSINESS_ACTION/OP 配置。
#>

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# 连接参数统一由 scripts/dev/eos-sql.ps1 解析（优先 SQL 认证 MSSQL_ERP_CONN；
# 受限/沙箱环境下集成认证会被 SSPI 拒绝）。-Server/-Database 仅作兼容保留。
. (Join-Path $PSScriptRoot 'dev\eos-sql.ps1')
$EosArgs = (Get-EosSqlTarget).Args

function Invoke-SqlScalar([string]$Query) {
    $output = & sqlcmd @EosArgs -b -h -1 -W -Q $Query
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd 查询失败 rc=$LASTEXITCODE" }
    return (($output | Where-Object { $_ -match '^\s*\d+\s*$' } | Select-Object -Last 1)).Trim()
}

$kindColumn = Invoke-SqlScalar "SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.FIELD_RELATION') AND name = N'RELATION_KIND') THEN 1 ELSE 0 END;"
if ($kindColumn -ne '1') {
    throw 'FIELD_RELATION 尚无 RELATION_KIND 列：请先让用户重启 EOS.API 执行迁移 058，再运行本登记器。'
}

# 行内用 '|' 分隔前缀字段：按空白切分会把空字段（无明细表、无 SOURCE_TABLE/SOURCE_SCOPE 的模块）
# 整个吞掉，导致后续字段错位甚至被 Count 检查静默跳过（2904/3307 这类无明细表模块曾因此漏登记）。
$opRows = & sqlcmd @EosArgs -b -y 0 -Q @'
SET NOCOUNT ON;
SELECT CAST(m.M_IDX AS nvarchar(10)) + '|' + LTRIM(RTRIM(ISNULL(m.MASTER_TABLE,''))) + '|'
     + LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE,''))) + '|' + o.TARGET_TABLE + '|' + o.SOURCE_SCOPE + '|'
     + LTRIM(RTRIM(ISNULL(o.SOURCE_TABLE,''))) + '|' + o.MATCH_STRUCT
FROM dbo.MODULE_BUSINESS_ACTION_OP o
INNER JOIN dbo.MODULE_BUSINESS_ACTION a ON a.ACTION_ID = o.ACTION_ID
INNER JOIN dbo.MODULES m ON m.M_IDX = a.M_IDX
WHERE LTRIM(RTRIM(ISNULL(o.MATCH_STRUCT,''))) <> ''
ORDER BY a.M_IDX, a.EVENT_CODE, a.SEQ, o.OP_SEQ;
'@
if ($LASTEXITCODE -ne 0) { throw "读取 OP 定位键失败 rc=$LASTEXITCODE" }

$groups = [ordered]@{}
$unresolved = New-Object System.Collections.Generic.List[string]
$totalMatches = 0

foreach ($line in $opRows) {
    if ($line -notmatch '\|') { continue }
    # 限制为 7 段：MATCH_STRUCT（末段）即使含分隔符也保持完整
    $prefix = $line -split '\|', 7
    if ($prefix.Count -lt 7) { continue }
    $moduleId = [int]$prefix[0].Trim()
    $masterTable = $prefix[1].Trim()
    $detailTable = $prefix[2].Trim()
    $targetTable = $prefix[3].Trim().ToUpperInvariant()
    $opScope = $prefix[4].Trim().ToUpperInvariant()
    $opSourceTable = $prefix[5].Trim().ToUpperInvariant()
    $matchJson = $prefix[6]
    if ($matchJson.IndexOf('[') -lt 0) { continue }

    $match = $null
    try { $match = $matchJson | ConvertFrom-Json } catch { $unresolved.Add("module=$moduleId match 非合法 JSON"); continue }
    if ($null -eq $match) { continue }
    $matchItems = if ($match -is [System.Array]) { @($match) } else { @($match) }

    $tuples = New-Object System.Collections.Generic.List[string]
    $ordinal = 1
    foreach ($item in $matchItems) {
        $totalMatches++
        if ($null -eq $item.target -or $null -eq $item.source) { $unresolved.Add("module=$moduleId match 缺 target/source"); continue }
        $scope = ([string]$item.source.scope).Trim().ToUpperInvariant()
        $fromTable = switch ($scope) {
            'MASTER' { $masterTable }
            'DETAIL' { $detailTable }
            'TABLE'  { if ($item.source.table) { ([string]$item.source.table).Trim().ToUpperInvariant() } elseif ($opSourceTable) { $opSourceTable } else { '' } }
            default { '' }
        }
        if ($fromTable -eq '') {
            $unresolved.Add("module=$moduleId target=$targetTable scope=$scope 无法解析来源表")
            continue
        }
        $fromColumn = ([string]$item.source.field).Trim().ToUpperInvariant()
        $toColumn = ([string]$item.target).Trim().ToUpperInvariant()
        $tuples.Add("$fromTable|$fromColumn|$targetTable|$toColumn|$scope|$ordinal")
        $ordinal++
    }
    if ($tuples.Count -eq 0) { continue }
    if ($Diagnose -and $targetTable -eq 'PRODUCT' -and $tuples.Count -gt 0 -and -not $groups.Contains("$targetTable::" + ($tuples -join ';'))) {
        Write-Output ("  debug-product prefix=" + ($prefix -join ',') + " tuple=" + ($tuples -join ';'))
    }

    $signature = "$targetTable::" + ($tuples -join ';')
    if (-not $groups.Contains($signature)) {
        $groups[$signature] = [pscustomobject]@{
            TargetTable = $targetTable
            Tuples = $tuples
        }
    }
}

if ($groups.Count -eq 0) {
    Write-Output 'SKIP: 未发现需要登记的定位键。'
    exit 0
}
if ($Diagnose) {
    Write-Output ("诊断：解析定位键行 " + $totalMatches + " 项，去重边组 " + $groups.Count + " 组，未解析 " + $unresolved.Count + " 条")
    $groups.Keys | ForEach-Object { ($_ -split '::')[0] } | Group-Object | Sort-Object Count -Descending | Select-Object -First 10 | ForEach-Object { Write-Output ("  target=" + $_.Name + " count=" + $_.Count) }
    $unresolved | Select-Object -First 10 | ForEach-Object { Write-Output ("  unresolved: " + $_) }
}

# 读取既有 EFFECT 边用于幂等与 ID 分配（同样用 '|' 分隔：SOURCE_SCOPE 为空的边曾因空白切分错位）
$existing = & sqlcmd @EosArgs -b -y 0 -Q @'
SET NOCOUNT ON;
SELECT CAST(RELATION_ID AS nvarchar(20)) + '|' + CAST(KEY_ORDINAL AS nvarchar(10)) + '|' + FROM_TABLE + '|'
     + FROM_COLUMN + '|' + TO_TABLE + '|' + TO_COLUMN + '|' + ISNULL(SOURCE_SCOPE,'')
FROM dbo.FIELD_RELATION
WHERE RELATION_KIND = N'EFFECT'
ORDER BY RELATION_ID, KEY_ORDINAL;
'@
if ($LASTEXITCODE -ne 0) { throw "读取既有 EFFECT 边失败 rc=$LASTEXITCODE" }

$existingGroups = @{}
foreach ($line in $existing) {
    $p = $line -split '\|'
    if ($p.Count -lt 7) { continue }
    $rid = [long]$p[0]
    if (-not $existingGroups.ContainsKey($rid)) {
        $existingGroups[$rid] = New-Object System.Collections.Generic.List[string]
    }
    $existingGroups[$rid].Add("$($p[2])|$($p[3])|$($p[4])|$($p[5])|$($p[6])")
}

$nextId = [long](Invoke-SqlScalar 'SELECT ISNULL(MAX(RELATION_ID),0) FROM dbo.FIELD_RELATION;')
$sql = New-Object System.Text.StringBuilder
[void]$sql.AppendLine('SET NOCOUNT ON;')
[void]$sql.AppendLine('SET QUOTED_IDENTIFIER ON;')

$registeredGroups = 0
foreach ($entry in $groups.Values) {
    # 与既有边组比较（同键列序列即视为同一关系）
    # 幂等比较按"关系对"（from 表.列 → to 表.列）；来源域不参与比较——边表主键就是这四列
    # （同一列表对只存一行），同一关系既作明细定位又作显式上下文表时不该重复登记/反复改写。
    $signatureTuples = @($entry.Tuples | ForEach-Object { (($_ -split '\|')[0..3]) -join '|' })
    # 简单全等比较
    $matched = $false
    foreach ($list in $existingGroups.Values) {
        if ($list.Count -ne $signatureTuples.Count) { continue }
        $same = $true
        for ($i = 0; $i -lt $list.Count; $i++) {
            if (($list[$i] -split '\|')[0..3] -join '|' -ne $signatureTuples[$i]) { $same = $false; break }
        }
        if ($same) { $matched = $true; break }
    }
    if ($matched) { continue }

    $nextId++
    foreach ($tuple in $entry.Tuples) {
        $tp = $tuple -split '\|'
        $fromTable = $tp[0]; $fromColumn = $tp[1]; $toTable = $tp[2]; $toColumn = $tp[3]; $scope = $tp[4]; $ordinal = [int]$tp[5]
        $name = "$toTable 定位键（效果）"
        $where = "FROM_TABLE=N'$fromTable' AND FROM_COLUMN=N'$fromColumn' AND TO_TABLE=N'$toTable' AND TO_COLUMN=N'$toColumn'"
        [void]$sql.AppendLine("IF EXISTS (SELECT 1 FROM dbo.FIELD_RELATION WHERE $where)")
        [void]$sql.AppendLine('BEGIN')
        [void]$sql.AppendLine("    UPDATE dbo.FIELD_RELATION SET RELATION_ID=$nextId, RELATION_NAME=N'$name', RELATION_KIND=N'EFFECT', SOURCE_SCOPE=N'$scope', KEY_ORDINAL=$ordinal, DESCRIPTION=N'$name' WHERE $where;")
        [void]$sql.AppendLine('END')
        [void]$sql.AppendLine('ELSE')
        [void]$sql.AppendLine('BEGIN')
        [void]$sql.AppendLine("    INSERT dbo.FIELD_RELATION (FROM_TABLE, FROM_COLUMN, TO_TABLE, TO_COLUMN, DESCRIPTION, RELATION_ID, RELATION_NAME, RELATION_KIND, SOURCE_SCOPE, KEY_ORDINAL, CREATE_PERSON, CREATE_DATE)")
        [void]$sql.AppendLine("    VALUES (N'$fromTable', N'$fromColumn', N'$toTable', N'$toColumn', N'$name', $nextId, N'$name', N'EFFECT', N'$scope', $ordinal, N'seed-agent', SYSDATETIME());")
        [void]$sql.AppendLine('END')
    }
    $registeredGroups++
}

if ($registeredGroups -eq 0) {
    Write-Output 'SKIP: 全部定位键组合已登记。'
    exit 0
}

$tempDir = Join-Path $env:TEMP 'opencode'
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
$sqlFile = Join-Path $tempDir 'register-field-relation-edges.sql'
[IO.File]::WriteAllText($sqlFile, $sql.ToString(), [Text.Encoding]::UTF8)
& sqlcmd @EosArgs -b -f 65001 -i $sqlFile
if ($LASTEXITCODE -ne 0) { throw "登记执行失败 rc=$LASTEXITCODE" }

Write-Output ("PASS: 登记效果边 " + $registeredGroups + " 组（解析定位键 " + $totalMatches + " 项）")
if ($unresolved.Count -gt 0) {
    Write-Output ('WARN: 未解析项 ' + $unresolved.Count + ' 条：')
    $unresolved | Select-Object -First 20 | ForEach-Object { Write-Output "  $_" }
}
