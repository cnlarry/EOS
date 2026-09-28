param(
    [string]$Server = 'localhost',
    [string]$Database = 'EOS.ERP'
)

<#
    全量核验：MODULE_BUSINESS_ACTION_OP 的定位键是否都有已登记 EFFECT 边。
    与保存 lint 同口径；全部通过返回 0，否则列出未登记项并返回 1。
#>

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$opRows = & sqlcmd -S $Server -d $Database -E -b -y 0 -Q @'
SET NOCOUNT ON;
SELECT m.M_IDX, LTRIM(RTRIM(ISNULL(m.MASTER_TABLE,''))), LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE,''))),
       o.TARGET_TABLE, LTRIM(RTRIM(ISNULL(o.SOURCE_TABLE,''))), o.MATCH_STRUCT
FROM dbo.MODULE_BUSINESS_ACTION_OP o
INNER JOIN dbo.MODULE_BUSINESS_ACTION a ON a.ACTION_ID = o.ACTION_ID
INNER JOIN dbo.MODULES m ON m.M_IDX = a.M_IDX
WHERE LTRIM(RTRIM(ISNULL(o.MATCH_STRUCT,''))) <> ''
ORDER BY a.M_IDX, a.EVENT_CODE, a.SEQ, o.OP_SEQ;
'@
if ($LASTEXITCODE -ne 0) { throw "读取定位键失败 rc=$LASTEXITCODE" }

$edgeRows = & sqlcmd -S $Server -d $Database -E -b -y 0 -Q @'
SET NOCOUNT ON;
SELECT RELATION_ID, KEY_ORDINAL, FROM_TABLE, FROM_COLUMN, TO_TABLE, TO_COLUMN, ISNULL(SOURCE_SCOPE,'')
FROM dbo.FIELD_RELATION
WHERE RELATION_KIND = N'EFFECT'
ORDER BY RELATION_ID, KEY_ORDINAL;
'@
if ($LASTEXITCODE -ne 0) { throw "读取 EFFECT 边失败 rc=$LASTEXITCODE" }

$edgeGroups = @{}
foreach ($line in $edgeRows) {
    $p = $line -split '\s+'
    if ($p.Count -lt 7) { continue }
    $rid = [long]$p[0]
    if (-not $edgeGroups.ContainsKey($rid)) { $edgeGroups[$rid] = New-Object System.Collections.Generic.List[string] }
    $edgeGroups[$rid].Add("$($p[2])|$($p[3])|$($p[4])|$($p[5])|$($p[6])")
}

function Test-Signature([string[]]$tuples) {
    foreach ($list in $edgeGroups.Values) {
        if ($list.Count -ne $tuples.Count) { continue }
        $same = $true
        for ($i = 0; $i -lt $list.Count; $i++) {
            if ($list[$i] -ne $tuples[$i]) { $same = $false; break }
        }
        if ($same) { return $true }
    }
    return $false
}

$checked = 0
$missing = New-Object System.Collections.Generic.List[string]
foreach ($line in $opRows) {
    $jsonStart = $line.IndexOf('[')
    if ($jsonStart -lt 0) { continue }
    $prefix = ($line.Substring(0, $jsonStart)).Trim() -split '\s+'
    if ($prefix.Count -lt 4) { continue }
    $moduleId = [int]$prefix[0]
    $masterTable = $prefix[1]
    $detailTable = $prefix[2]
    $targetTable = $prefix[3].ToUpperInvariant()
    $opSourceTable = if ($prefix.Count -ge 5) { $prefix[4].ToUpperInvariant() } else { '' }
    $match = $null
    try { $match = ($line.Substring($jsonStart)) | ConvertFrom-Json } catch { $missing.Add("module=$moduleId target=$targetTable 定位键 JSON 无法解析"); continue }
    if ($null -eq $match) { continue }
    $matchItems = if ($match -is [System.Array]) { @($match) } else { @($match) }

    $tuples = New-Object System.Collections.Generic.List[string]
    foreach ($item in $matchItems) {
        $scope = ([string]$item.source.scope).Trim().ToUpperInvariant()
        $fromTable = switch ($scope) {
            'MASTER' { $masterTable }
            'DETAIL' { $detailTable }
            'TABLE'  { if ($item.source.table) { ([string]$item.source.table).Trim().ToUpperInvariant() } elseif ($opSourceTable) { $opSourceTable } else { '' } }
            default { '' }
        }
        if ($fromTable -eq '') { break }
        $tuples.Add("$fromTable|$([string]$item.source.field)|$targetTable|$([string]$item.target)|$scope")
    }
    if ($tuples.Count -eq 0) { continue }
    $checked++
    if (-not (Test-Signature @($tuples))) {
        $missing.Add("module=$moduleId target=$targetTable 定位键未登记：" + ($tuples -join ';'))
    }
}

Write-Output ("核验定位键公式行 " + $checked + " 条")
if ($missing.Count -eq 0) {
    Write-Output 'PASS: 全部定位键与 EFFECT 边一致。'
    exit 0
}
Write-Output ('FAIL: 未登记/不一致 ' + $missing.Count + ' 处：')
$missing | Select-Object -First 20 | ForEach-Object { Write-Output "  $_" }
exit 1
