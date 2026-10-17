# 一次性｜锚点：ADR-038（各批类型迁移前的溢出预扫描）
<#
.SYNOPSIS
    一次性脚本：指定业务域的 float 列溢出预扫描，只读不写。

.DESCRIPTION
    按 FIELDS.NUMERIC_ROLE 算出每个 float 列的目标 decimal(p,s)，
    逐列取 MAX(ABS(col)) 与整数位上界比较，输出三档清单：
      安全      —— 值域落在目标类型的整数位内，可直接 ALTER COLUMN
      超限      —— 实际最大值超出整数位，需单独定精度或先降值
      未登记    —— 没有 NUMERIC_ROLE，不参与
    语义取自已生成的清单文件（而非库内登记值），因此语义修正迁移尚未执行时也能算对。
    扫描通过后用 gen-numeric-batch-migration.ps1 生成该批的分片迁移。

.PARAMETER Prefixes
    表名前缀，如 COP_,PUR_（批 A）或 MOC_,INV_,MOU_,SFC_,BOM_（批 B）。

.PARAMETER OutFile
    输出 CSV 路径（相对仓库根）。
#>
param(
    [string[]] $Prefixes = @('COP_', 'PUR_'),
    [string] $OutFile = 'docs/plans/numeric-prescan.csv'
)

$ErrorActionPreference = 'Stop'
try {
    [Console]::OutputEncoding = [Text.Encoding]::UTF8
    $root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    . (Join-Path $root 'scripts/dev/eos-sql.ps1')
    $outPath = Join-Path $root $OutFile

    # pwsh -File 不会把 "A,B" 解析成数组，这里兼容逗号分隔的单个参数
    $Prefixes = @($Prefixes | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

    $specs = @{
        'AMOUNT'  = @(18, 4)
        'QTY'     = @(18, 6)
        'PRICE'   = @(18, 6)
        'RATE'    = @(18, 10)
        'PERCENT' = @(9, 6)
    }

    $conditions = ($Prefixes | ForEach-Object { "LEFT(o.name,$($_.Length))=N'$($_)'" }) -join ' OR '
    $scope = "($conditions)"

    $roleMap = @{}
    foreach ($r in (Import-Csv (Join-Path $root 'docs\plans\numeric-role-full.csv') -Encoding utf8)) {
        $roleMap["$($r.Table)|$($r.Column)"] = $r.Role
    }

    $raw = Invoke-EosSqlQuery -Query @"
SELECT o.name + N'|' + c.name
FROM sys.columns c
JOIN sys.objects o ON c.object_id = o.object_id AND o.type = 'U'
JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE s.name = N'dbo' AND TYPE_NAME(c.user_type_id) = 'float' AND $scope
ORDER BY o.name, c.name;
"@

    $items = @($raw | Where-Object { ([string]$_).Trim() } | ForEach-Object {
            $p = ([string]$_).Trim() -split '\|'
            [pscustomobject]@{ Table = $p[0]; Column = $p[1]; Role = $roleMap["$($p[0])|$($p[1])"] }
        })
    "FLOAT_COLS=$($items.Count)  范围=$($Prefixes -join ',')"

    $work = foreach ($it in $items) {
        $spec = $specs[$it.Role]
        [pscustomobject]@{
            Table     = $it.Table
            Column    = $it.Column
            Role      = $it.Role
            Target    = if ($spec) { "decimal($($spec[0]),$($spec[1]))" } else { '' }
            IntDigits = if ($spec) { $spec[0] - $spec[1] } else { $null }
            MaxAbs    = $null
            Verdict   = ''
        }
    }

    $candidates = @($work | Where-Object { $_.Target })
    "SCANNABLE=$($candidates.Count)"
    $batchSize = 60
    for ($i = 0; $i -lt $candidates.Count; $i += $batchSize) {
        $slice = $candidates[$i..([Math]::Min($i + $batchSize - 1, $candidates.Count - 1))]
        $parts = foreach ($c in $slice) {
            "SELECT N'$($c.Table)|$($c.Column)|' + ISNULL(CAST(MAX(ABS([$($c.Column)])) AS varchar(60)), N'NULL') FROM [dbo].[$($c.Table)]"
        }
        $res = Invoke-EosSqlQuery -Query ($parts -join "`r`nUNION ALL`r`n")
        foreach ($line in @($res)) {
            $t = ([string]$line).Trim()
            if (-not $t) { continue }
            $p = $t -split '\|'
            $hit = $candidates | Where-Object { $_.Table -eq $p[0] -and $_.Column -eq $p[1] } | Select-Object -First 1
            # 用 double 解析：float 列可能存着 1e+072 这类越界值，decimal 会直接抛错
            if ($hit -and $p[2] -ne 'NULL') {
                $hit.MaxAbs = [double]::Parse($p[2], [Globalization.CultureInfo]::InvariantCulture)
            }
        }
        "  scanned $([Math]::Min($i + $batchSize, $candidates.Count)) / $($candidates.Count)"
    }

    foreach ($w in $work) {
        if (-not $w.Target) { $w.Verdict = '未登记'; continue }
        if ($null -eq $w.MaxAbs) { $w.Verdict = '空表或全 NULL'; continue }
        $limit = [math]::Pow(10, $w.IntDigits)
        $w.Verdict = if ($w.MaxAbs -ge $limit) { '超限' } else { '安全' }
    }

    $work | Sort-Object Verdict, Table, Column | Export-Csv -Path $outPath -NoTypeInformation -Encoding utf8BOM

    "---- 判定分布 ----"
    $work | Group-Object Verdict | Sort-Object Count -Descending | ForEach-Object { "{0,-16} {1,5}" -f $_.Name, $_.Count }
    "---- 超限明细 ----"
    @($work | Where-Object { $_.Verdict -eq '超限' }) | ForEach-Object {
        "  $($_.Table).$($_.Column) [$($_.Role) -> $($_.Target)] max=$($_.MaxAbs)"
    }
    "CSV=$outPath"
    exit 0
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
