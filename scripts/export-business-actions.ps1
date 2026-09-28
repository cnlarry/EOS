<#
.SYNOPSIS
    Exports the effect-engine configuration currently stored in EOS.ERP into the seed manifest.

.DESCRIPTION
    The database (plus DbUp migrations) is the single source of truth for business actions,
    formula rows and validation rules. The seed manifest is only a translation-time artifact,
    so keeping it in sync by hand is not sustainable: this script regenerates it in the exact
    shape scripts/seed-business-actions.ps1 consumes.

    Rows are read through FOR JSON so that embedded newlines inside JSON columns cannot break
    line-based parsing, and -y 0 prevents sqlcmd from truncating long JSON at 256 characters.

.PARAMETER Server / Database
    Connection target.

.PARAMETER OutPath
    Output manifest path. Defaults to scripts/seed-data/business-action-seed.json.

.PARAMETER WhatIf
    Writes to <OutPath>.generated instead of overwriting the manifest.
#>
[CmdletBinding()]
param(
    [string] $Server = 'localhost',
    [string] $Database = 'EOS.ERP',
    [string] $OutPath = (Join-Path $PSScriptRoot 'seed-data/business-action-seed.json'),
    [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'

# sqlcmd -f 65001 emits UTF-8; without this a GB2312 console mis-decodes multi-byte
# characters and can swallow a '~' field separator (e.g. in '§'-style SOURCE_REFs),
# silently dropping rows from the delimited parse below.
[Console]::OutputEncoding = [Text.Encoding]::UTF8

function Invoke-DelimitedRows {
    param([string] $Sql, [int] $ExpectedColumns)

    # -h and -W are mutually exclusive with -y 0, so the header block is skipped here instead.
    # Values are read with CR/LF stripped: a raw newline inside a JSON column would otherwise
    # split one row across several lines.
    $raw = & sqlcmd -S $Server -d $Database -E -C -f 65001 -w 65535 -y 0 -s "~" -Q ('SET NOCOUNT ON; ' + $Sql) 2>&1
    if ($LASTEXITCODE -ne 0) { throw "SQLCMD_FAIL exit=$LASTEXITCODE" }
    $lines = @($raw | Where-Object { $_ -is [string] })
    $start = 0
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].TrimStart() -match '^[-=]') { $start = $i + 1; break }
    }
    # A row whose JSON column contains a raw newline arrives as several lines: keep buffering
    # until the accumulated text has the expected number of fields.
    $rows = @()
    $buffer = ''
    for ($i = $start; $i -lt $lines.Count; $i++) {
        $line = $lines[$i].TrimEnd()
        if ($line.Trim() -eq '' -or $line.Trim() -like '(* rows affected)') { continue }
        $candidate = if ($buffer -eq '') { $line } else { $buffer + ' ' + $line }
        $columns = $candidate -split '~'
        if ($columns.Count -lt $ExpectedColumns) { $buffer = $candidate; continue }
        $rows += , $columns
        $buffer = ''
    }
    return $rows
}

function Invoke-Scalar {
    param([string] $Sql)

    $raw = & sqlcmd -S $Server -d $Database -E -C -f 65001 -h -1 -W -Q ('SET NOCOUNT ON; ' + $Sql) 2>&1
    if ($LASTEXITCODE -ne 0) { throw "SQLCMD_FAIL exit=$LASTEXITCODE" }
    $value = @($raw | Where-Object { $_ -is [string] -and $_.Trim() -ne '' -and $_.Trim() -notlike '(* rows affected)' }) | Select-Object -First 1
    return [int] ($value.Trim())
}

function Convert-Null {
    param([string] $Value)

    $trimmed = if ($null -eq $Value) { '' } else { $Value.Trim() }
    if ($trimmed -eq '') { return $null }
    return $trimmed
}

$flat = { param([string] $column) "REPLACE(REPLACE(ISNULL($column, ''), CHAR(13), ''), CHAR(10), '')" }

try {
    $expectedActions = Invoke-Scalar 'SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION;'
    $expectedOps = Invoke-Scalar 'SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION_OP;'
    $expectedRules = Invoke-Scalar 'SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE;'

    $actionRows = Invoke-DelimitedRows "SELECT A.M_IDX, A.EVENT_CODE, A.SEQ, A.ACTION_ID, A.EFFECT_KEY, $(& $flat 'A.EFFECT_NAME'), A.FAIL_MODE, $(& $flat 'A.CONDITION_STRUCT'), $(& $flat 'A.PARAM_STRUCT'), $(& $flat 'A.REVERSE_STRUCT'), $(& $flat 'A.REMARK'), $(& $flat 'A.SOURCE_REF') FROM dbo.MODULE_BUSINESS_ACTION A ORDER BY A.M_IDX, A.EVENT_CODE, A.SEQ;" 12
    $opRows = Invoke-DelimitedRows "SELECT O.ACTION_ID, O.OP_SEQ, O.TARGET_TABLE, O.TARGET_FIELD, O.OP_CODE, O.SOURCE_SCOPE, $(& $flat 'O.SOURCE_FIELD'), $(& $flat 'O.SOURCE_TABLE'), $(& $flat 'O.SOURCE_AGG'), $(& $flat 'O.SOURCE_CONSTANT'), $(& $flat 'O.SOURCE_TERMS_STRUCT'), $(& $flat 'O.MATCH_STRUCT'), $(& $flat 'O.CONDITION_STRUCT'), $(& $flat 'O.REMARK') FROM dbo.MODULE_BUSINESS_ACTION_OP O ORDER BY O.ACTION_ID, O.OP_SEQ;" 14
    $ruleRows = Invoke-DelimitedRows "SELECT R.M_IDX, R.STAGE, R.SEQ, R.VALIDATION_KEY, $(& $flat 'R.PARAM_STRUCT'), $(& $flat 'R.MESSAGE'), $(& $flat 'R.REMARK'), $(& $flat 'R.SOURCE_REF') FROM dbo.MODULE_VALIDATION_RULE R ORDER BY R.M_IDX, R.STAGE, R.SEQ;" 8

    if ($actionRows.Count -ne $expectedActions -or $opRows.Count -ne $expectedOps -or $ruleRows.Count -ne $expectedRules) {
        Write-Output ('EXPORT_FAIL: row count mismatch actions={0}/{1} ops={2}/{3} rules={4}/{5}' -f $actionRows.Count, $expectedActions, $opRows.Count, $expectedOps, $ruleRows.Count, $expectedRules)
        exit 3
    }
} catch {
    Write-Output "EXPORT_FAIL: $($_.Exception.Message)"
    Write-Output "TRACE: $($_.ScriptStackTrace)"
    exit 2
}

$opsByAction = @{}
foreach ($op in $opRows) {
    $id = $op[0].Trim()
    if (-not $opsByAction.ContainsKey($id)) { $opsByAction[$id] = @() }
    $opsByAction[$id] += [ordered]@{
        targetTable    = $op[2].Trim()
        targetField    = $op[3].Trim()
        opCode         = $op[4].Trim()
        sourceScope    = $op[5].Trim()
        sourceField    = (Convert-Null $op[6])
        sourceTable    = (Convert-Null $op[7])
        sourceAgg      = (Convert-Null $op[8])
        sourceConstant = (Convert-Null $op[9])
        sourceTerms    = (Convert-Null $op[10])
        match          = (Convert-Null $op[11])
        condition      = (Convert-Null $op[12])
        remark         = (Convert-Null $op[13])
    }
}

$rulesByModule = @{}
foreach ($rule in $ruleRows) {
    $mid = [int] $rule[0].Trim()
    if (-not $rulesByModule.ContainsKey($mid)) { $rulesByModule[$mid] = @() }
    $rulesByModule[$mid] += [ordered]@{
        stage         = $rule[1].Trim()
        seq           = [int]$rule[2].Trim()
        validationKey = $rule[3].Trim()
        params        = (Convert-Null $rule[4])
        message       = (Convert-Null $rule[5])
        remark        = (Convert-Null $rule[6])
        sourceRef     = (Convert-Null $rule[7])
    }
}

$moduleIds = New-Object System.Collections.Generic.List[int]
$actionsByModule = @{}
foreach ($action in $actionRows) {
    $mid = [int] $action[0].Trim()
    if (-not $actionsByModule.ContainsKey($mid)) {
        $actionsByModule[$mid] = @()
        $moduleIds.Add($mid)
    }
    $id = $action[3].Trim()
    $ops = if ($opsByAction.ContainsKey($id)) { @($opsByAction[$id]) } else { @() }
    $actionsByModule[$mid] += [ordered]@{
        event     = $action[1].Trim()
        seq       = [int] $action[2].Trim()
        effectKey = $action[4].Trim()
        name      = (Convert-Null $action[5])
        failMode  = $action[6].Trim()
        condition = (Convert-Null $action[7])
        params    = (Convert-Null $action[8])
        reverse   = (Convert-Null $action[9])
        remark    = (Convert-Null $action[10])
        sourceRef = (Convert-Null $action[11])
        ops       = $ops
    }
}

$modules = @()
foreach ($mid in ($moduleIds | Sort-Object)) {
    $modules += [ordered]@{
        moduleId    = $mid
        actions     = @($actionsByModule[$mid])
        validations = if ($rulesByModule.ContainsKey($mid)) { @($rulesByModule[$mid]) } else { @() }
    }
}

$manifest = [ordered]@{
    version = 1
    comment = '本文件由 scripts/export-business-actions.ps1 从数据库导出生成，请勿手工编辑；库（含 DbUp 迁移）是唯一事实源。'
    modules = $modules
}

$json = $manifest | ConvertTo-Json -Depth 12
$target = if ($WhatIf) { $OutPath + '.generated' } else { $OutPath }
[IO.File]::WriteAllText($target, $json, (New-Object System.Text.UTF8Encoding($false)))

$opCount = 0
foreach ($key in $opsByAction.Keys) { $opCount += $opsByAction[$key].Count }
Write-Output ('EXPORT_OK modules={0} actions={1} ops={2} rules={3} -> {4}' -f $modules.Count, @($actionRows).Count, $opCount, @($ruleRows).Count, $target)
exit 0
