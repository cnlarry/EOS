<#
.SYNOPSIS
    从当前库重建元数据种子 `db/bootstrap/20_metadata.sql`。

.DESCRIPTION
    建库种子是系统现状的镜像：日常开发只写 DbUp 迁移，**版本发布时收口**——用本脚本把
    库内的元数据现状回灌种子，并把已执行的迁移登记进 `40_journal_baseline.sql` 的基线，
    使新克隆的库直接等于当前状态、不再需要跑迁移。

    本脚本只负责**元数据段**；结构与基线分别由 `export-bootstrap-schema.ps1` 与
    `export-bootstrap-baseline.ps1` 负责。三者必须同一批提交，否则新库会既缺结构又缺数据。

    输出格式与既有种子保持一致：每张元数据表先 `DELETE` 再 `INSERT` 全量行，
    逐行一个 VALUES 元组；段头注释保留「-- 表名（总 N 行，剔除 M）」的形状。
    列清单以**库内当前结构**为准（种子里的旧列名会随迁移失效，不能当依据）。

    脱敏：库内的真实企业名/客户名不得随种子入库。命中词表的值必须能在脱敏映射里
    找到替换值，否则脚本**中止并列出来**——不猜、不静默丢弃。词表与映射都放版本库
    之外，避免它们自身成为泄露源：
      · $HOME/.eos/sensitive-terms.txt   一行一个敏感词
      · $HOME/.eos/desensitize-map.txt   一行一条「敏感词=替换值」，替换值为空表示删除该词

.PARAMETER OutFile
    输出路径，默认 `db/bootstrap/20_metadata.sql`。

.PARAMETER TermsFile
    敏感词表路径，默认 `$HOME/.eos/sensitive-terms.txt`；不存在时跳过脱敏校验。

.PARAMETER MapFile
    脱敏映射路径，默认 `$HOME/.eos/desensitize-map.txt`。

.PARAMETER DryRun
    只报告（段行数对比、脱敏命中、未覆盖的敏感词），不写文件。

.EXAMPLE
    pwsh scripts/export-bootstrap-metadata.ps1 -DryRun
    pwsh scripts/export-bootstrap-metadata.ps1
#>
[CmdletBinding()]
param(
    [string]$OutFile = 'db/bootstrap/20_metadata.sql',
    [string]$TermsFile,
    [string]$MapFile,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

. "$PSScriptRoot/dev/eos-sql.ps1"

$repoRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path | Split-Path -Parent
if (-not [IO.Path]::IsPathRooted($OutFile)) { $OutFile = Join-Path $repoRoot $OutFile }
if (-not (Test-Path -LiteralPath $OutFile)) { throw "找不到现有种子文件：$OutFile" }

if (-not $TermsFile) { $TermsFile = Join-Path $HOME '.eos/sensitive-terms.txt' }
if (-not $MapFile) { $MapFile = Join-Path $HOME '.eos/desensitize-map.txt' }

# ---------------------------------------------------------------- 脱敏表
function Read-Terms {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    @([IO.File]::ReadLines($Path) | ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') } | Sort-Object -Unique)
}

function Read-Map {
    param([string]$Path)
    # 必须用**大小写敏感**的有序字典：同一个代号的两种大小写写法是两条独立规则（替换值不同），
    # 用 [ordered]@{} 会让后一条覆盖前一条——PowerShell 的默认键比较不区分大小写。
    $map = New-Object System.Collections.Specialized.OrderedDictionary([StringComparer]::Ordinal)
    if (-not (Test-Path -LiteralPath $Path)) { return $map }
    foreach ($line in [IO.File]::ReadLines($Path)) {
        $t = $line.Trim()
        if (-not $t -or $t.StartsWith('#')) { continue }
        $i = $t.IndexOf('=')
        if ($i -lt 1) { continue }
        $map[$t.Substring(0, $i)] = $t.Substring($i + 1)
    }
    return $map
}

$terms = Read-Terms -Path $TermsFile
$map = Read-Map -Path $MapFile
$unmapped = New-Object System.Collections.Generic.HashSet[string]
$hitCount = 0

function Protect-Text {
    param([string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    $out = $Text
    foreach ($key in $map.Keys) {
        if ($out.Contains($key)) {
            $script:hitCount += ($out.Split($key).Count - 1)
            $out = $out.Replace($key, $map[$key])
        }
    }
    foreach ($t in $terms) {
        if ($out.Contains($t)) { [void]$script:unmapped.Add($t) }
    }
    return $out
}

# 口径 B 用：判断一段文本是否仍带真实客户/企业标识（整行客户资产的排除依据）
function Test-ContainsSensitive {
    param([string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return $false }
    foreach ($t in $terms) { if ($Text.Contains($t)) { return $true } }
    return $false
}

# ---------------------------------------------------------------- 值格式化
function Format-SqlValue {
    param($Value, [string]$Type)

    if ($null -eq $Value -or $Value -is [DBNull]) { return 'NULL' }

    switch -Regex ($Type) {
        '^bit$' { if ([bool]$Value) { return '1' } else { return '0' } }
        '^(tinyint|smallint|int|bigint)$' { return ([string]$Value) }
        '^(decimal|numeric|money|smallmoney|float|real)$' {
            return ([string]$Value.ToString([Globalization.CultureInfo]::InvariantCulture))
        }
        '^(datetime|datetime2|smalldatetime|date)$' {
            return "'" + ([datetime]$Value).ToString('yyyy-MM-ddTHH:mm:ss.fff') + "'"
        }
        '^(binary|varbinary|image)$' {
            return '0x' + (($Value | ForEach-Object { $_.ToString('X2') }) -join '')
        }
        '^(time)$' { return "'" + ([TimeSpan]$Value).ToString('hh\:mm\:ss\.fffffff') + "'" }
        default {
            $s = Protect-Text ([string]$Value)
            return "N'" + $s.Replace("'", "''") + "'"
        }
    }
}

# ---------------------------------------------------------------- 段清单（沿用现有文件的段顺序）
$existing = @(Get-Content -LiteralPath $OutFile -Encoding utf8)
$segments = @()
for ($i = 0; $i -lt $existing.Count; $i++) {
    if ($existing[$i] -match '^--\s*(\w+)（总\s*([\d]+)\s*行，剔除\s*([\d]+)）') {
        $segments += [pscustomobject]@{
            Name      = $matches[1]
            HeaderIdx = $i
            OldRows   = [int]$matches[2]
            Dropped   = [int]$matches[3]
        }
    }
}
if ($segments.Count -eq 0) { throw '未能从现有种子文件解析出段清单（段头注释形状变了？）' }
Write-Output ("段清单: " + (($segments | ForEach-Object { $_.Name }) -join ', '))

# ---------------------------------------------------------------- 逐段导出
$target = Get-EosSqlTarget
$conn = New-Object System.Data.SqlClient.SqlConnection($target.AdoConnectionString)
$conn.Open()
$out = New-Object System.Collections.Generic.List[string]
$report = @()

$out.Add('/*')
$out.Add(' * 元数据种子')
$out.Add(' *')
$out.Add(' * 表与字段登记、菜单模块、通用查询列与条件、报表与表单版式、流程定义。')
$out.Add(' * 系统的界面完全由这些元数据驱动；不含任何业务数据。')
$out.Add(' *')
$out.Add(' * 幂等：每张表先清空再写入，可重复执行。')
$out.Add(' */')
$out.Add('')
$out.Add('SET NOCOUNT ON;')
$out.Add('GO')

try {
    foreach ($seg in $segments) {
        $table = $seg.Name

        $colsCmd = $conn.CreateCommand()
        $colsCmd.CommandText = @"
SELECT c.name, t.name AS TypeName, c.column_id, c.is_identity
FROM sys.columns c JOIN sys.types t ON c.user_type_id = t.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.$table') ORDER BY c.column_id
"@
        $cols = @()
        $hasIdentity = $false
        $r = $colsCmd.ExecuteReader()
        while ($r.Read()) {
            $cols += [pscustomobject]@{ Name = $r.GetString(0); Type = $r.GetString(1) }
            if ($r.GetBoolean(3)) { $hasIdentity = $true }
        }
        $r.Close()
        if ($cols.Count -eq 0) { throw "库内找不到表 dbo.$table（段清单与库结构对不上）" }

        $pkCmd = $conn.CreateCommand()
        $pkCmd.CommandText = @"
SELECT c.name FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id = OBJECT_ID(N'dbo.$table') AND i.is_primary_key = 1
ORDER BY ic.key_ordinal
"@
        $pkCols = @()
        $pkReader = $pkCmd.ExecuteReader()
        while ($pkReader.Read()) { $pkCols += $pkReader.GetString(0) }
        $pkReader.Close()
        # 按主键排序：与既有种子逐行可比（按堆序读会让整段 diff 变成重写）
        $orderBy = ''
        if ($pkCols.Count -gt 0) { $orderBy = ' ORDER BY ' + (($pkCols | ForEach-Object { "[$_]" }) -join ', ') }

        $rowsCmd = $conn.CreateCommand()
        $rowsCmd.CommandText = "SELECT * FROM dbo.[$table]$orderBy"
        $reader = $rowsCmd.ExecuteReader()
        $values = New-Object System.Collections.Generic.List[string]
        $skipped = 0
        while ($reader.Read()) {
            # 口径 B：属于某个客户私有的整行不导出（种子是通用产品基线）。
            # REPORT_LAYOUT 的客户抬头（companyName 非空）与含客户名的页尾，换了客户就不该存在；
            # 内置版式是 Git 资产（EOS.API/ReportFormats/），由部署复制，不靠种子携带。
            if ($table -eq 'REPORT_LAYOUT') {
                $kind = if ($reader['KIND'] -is [DBNull]) { '' } else { [string]$reader['KIND'] }
                $body = if ($reader['CONTENT'] -is [DBNull]) { '' } else { [string]$reader['CONTENT'] }
                $isCustomerHeader = ($kind -eq 'HEADER' -and $body -match '"companyName"\s*:\s*"[^"]+')
                if ($isCustomerHeader -or (Test-ContainsSensitive $body)) { $skipped++; continue }
            }

            $cells = New-Object System.Collections.Generic.List[string]
            for ($ci = 0; $ci -lt $cols.Count; $ci++) {
                $val = $reader.GetValue($ci)
                # 口径 B：抹掉指向旧系统客户页面（~/CLIENT/<客户>/…）的 URL。这些页面只在旧系统存在，
                # 新系统走统一选择器与工作台；留着只会把客户名或客户专属路径带进仓库。
                # 该路径出现在 BROWSE_URL / HELP_URL 等多处，故按值判定而非按列判定。
                if ($val -isnot [DBNull] -and $val -is [string] -and ([string]$val) -match '/CLIENT/') { $val = '' }
                $cells.Add((Format-SqlValue -Value $val -Type $cols[$ci].Type))
            }
            $values.Add('    (' + ($cells -join ', ') + ')')
        }
        $reader.Close()

        $out.Add('')
        $out.Add('------------------------------------------------------------------------------')
        $out.Add(('-- {0}（总 {1} 行，剔除 {2}）' -f $table, $values.Count, $seg.Dropped))
        $out.Add('------------------------------------------------------------------------------')
        $out.Add("DELETE FROM dbo.[$table];")
        if ($values.Count -gt 0) {
            $colsStr = ($cols | ForEach-Object { "[$($_.Name)]" }) -join ', '
            # 带 IDENTITY 列的表（FIELD_DATASOURCE.ID、REPORT_FORM_LAYOUT.LAYOUT_ID）必须显式
            # 插入这些值：编号被别的表按值引用，交给库自行重排会与引用错位。
            if ($hasIdentity) { $out.Add("SET IDENTITY_INSERT dbo.[$table] ON;") }
            # SQL Server 的 INSERT ... VALUES 单条上限 1000 行，按 200 行分块（与既有种子的块大小一致）
            $chunk = 200
            for ($start = 0; $start -lt $values.Count; $start += $chunk) {
                $end = [Math]::Min($start + $chunk, $values.Count)
                $out.Add("INSERT dbo.[$table] ($colsStr) VALUES")
                for ($vi = $start; $vi -lt $end; $vi++) {
                    if ($vi -lt $end - 1) { $out.Add($values[$vi] + ',') } else { $out.Add($values[$vi]) }
                }
                $out.Add(';')
            }
            if ($hasIdentity) { $out.Add("SET IDENTITY_INSERT dbo.[$table] OFF;") }
        }
        $out.Add('GO')

        $report += [pscustomobject]@{
            段      = $table
            原行数  = $seg.OldRows
            新行数  = $values.Count
            差      = $values.Count - $seg.OldRows
            排除    = $skipped
            列数    = $cols.Count
        }
    }
}
finally {
    $conn.Close()
    $conn.Dispose()
}

$out.Add('')

# ---------------------------------------------------------------- 报告
Write-Output ''
Write-Output '=== 段行数对比（原 → 新）==='
$report | ForEach-Object {
    $mark = if ($_.差 -eq 0) { '  ' } else { '* ' }
    Write-Output ("  {0}{1,-30} {2,7} → {3,7}  ({4:+#;-#;0})  {5} 列" -f $mark, $_.段, $_.原行数, $_.新行数, $_.差, $_.列数)
}

Write-Output ''
Write-Output ("=== 脱敏 ===  替换命中 {0} 次" -f $hitCount)
if ($unmapped.Count -gt 0) {
    Write-Output ("未覆盖的敏感词（必须在 {0} 里补映射，否则不写文件）：" -f $MapFile)
    $unmapped | Sort-Object | ForEach-Object { Write-Output ("  " + $_) }
}

if ($DryRun) {
    Write-Output ''
    Write-Output '（-DryRun：未写文件）'
    if ($unmapped.Count -gt 0) { exit 1 }
    exit 0
}

if ($unmapped.Count -gt 0) {
    Write-Output ''
    Write-Output '中止：存在未映射的敏感词，补好脱敏映射后重跑。'
    exit 1
}

$enc = New-Object System.Text.UTF8Encoding($false)
$lf = [string][char]10
[IO.File]::WriteAllText($OutFile, ($out -join $lf) + $lf, $enc)
Write-Output ''
Write-Output ("已写入 {0}（{1} 行）" -f $OutFile, $out.Count)
