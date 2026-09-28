param(
    [string]$ManifestPath = (Join-Path $PSScriptRoot 'seed-data/business-action-seed.json'),
    [string]$Server = 'localhost',
    [string]$Database = 'EOS.ERP',
    [switch]$Force
)

<#
    业务动作/校验配置一次性导入器（幂等，可重复执行）。
    - 读取 business-action-seed.json（模块 × 业务动作/校验全量清单）；
    - 生成幂等 T-SQL（按 模块+事件+SEQ 与 模块+阶段+SEQ 已存在则跳过），单次 sqlcmd 执行；
    - 对象/值均为服务端白名单外数据，仅写种子表，不含任何动态 SQL 风险。
#>

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

if (-not (Test-Path -LiteralPath $ManifestPath)) { throw "清单不存在：$ManifestPath" }
$manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($null -eq $manifest.modules) { throw '清单缺少 modules 数组' }

# spChains 支持：一份 SP 动作链按多个 M_IDX 自动展开。
if ($null -ne $manifest.spChains) {
    $expanded = @($manifest.modules)
    foreach ($chain in $manifest.spChains.PSObject.Properties) {
        $def = $chain.Value
        foreach ($moduleId in @($def.modules)) {
            $expanded += [pscustomobject]@{
                moduleId    = [int]$moduleId
                actions     = $def.actions
                validations = $def.validations
            }
        }
    }
    $manifest.modules = @($expanded)
}

function Quote([AllowNull()][string]$value) {
    if ($null -eq $value) { return 'NULL' }
    return "N'" + ($value.Replace("'", "''")) + "'"
}

function Number([AllowNull()][int]$value) {
    if ($null -eq $value) { return 'NULL' }
    return [string]$value
}

# The manifest is a translation-time snapshot. The importer rewrites every action it covers and
# deletes that action's formula rows before re-inserting them, so importing an outdated manifest
# silently drops configuration that only lives in the database (later fixes, effect-key
# convergence, expanded completion-close rows). Refuse unless -Force is given.
# Without -Force this script is a read-only guard: it checks actions/ops AND validation rules
# for drift and exits without writing anything (a past version unconditionally executed the
# upsert and, by numbering rules positionally instead of by their true SEQ, inserted phantom
# validation rows into the database on every guard run).
if (-not $Force) {
    $moduleIds = @($manifest.modules | ForEach-Object { [int]$_.moduleId } | Sort-Object -Unique)
    if ($moduleIds.Count -gt 0) {
        $idList = ($moduleIds -join ',')
        $checkSql = "SELECT A.M_IDX, A.EVENT_CODE, A.SEQ, A.EFFECT_KEY, (SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION_OP O WHERE O.ACTION_ID = A.ACTION_ID) FROM dbo.MODULE_BUSINESS_ACTION A WHERE A.M_IDX IN ($idList) ORDER BY A.M_IDX, A.EVENT_CODE, A.SEQ;"
        $checkFile = Join-Path $env:TEMP 'opencode/seed-drift-check.txt'
        New-Item -ItemType Directory -Path (Split-Path $checkFile) -Force | Out-Null
        & sqlcmd -S $Server -d $Database -E -C -f 65001 -h -1 -W -w 200 -s "~" -Q $checkSql | Out-File -FilePath $checkFile -Encoding utf8
        $dbIndex = @{}
        foreach ($line in (Get-Content -LiteralPath $checkFile)) {
            if ($line.Trim() -eq '' -or $line.Trim() -like '(* rows affected)') { continue }
            $cols = $line -split '~'
            if ($cols.Count -lt 5) { continue }
            $dbIndex["$($cols[0].Trim())|$($cols[1].Trim())|$($cols[2].Trim())"] = @{ effectKey = $cols[3].Trim(); ops = [int] $cols[4].Trim() }
        }
        $seedIndex = @{}
        foreach ($module in $manifest.modules) {
            foreach ($action in @($module.actions)) {
                $event = if ($action.event) { [string]$action.event } else { 'APPROVE_EFFECT' }
                $seedIndex["$([int]$module.moduleId)|$event|$([int]$action.seq)"] = @{ effectKey = [string] $action.effectKey; ops = @($action.ops).Count }
            }
        }
        $drift = @()
        foreach ($key in $dbIndex.Keys) {
            if (-not $seedIndex.ContainsKey($key)) { $drift += "missing-in-seed $key [$($dbIndex[$key].effectKey)]"; continue }
            if ($dbIndex[$key].ops -gt $seedIndex[$key].ops) {
                $drift += "op-loss $key db=$($dbIndex[$key].ops) seed=$($seedIndex[$key].ops)"
            }
            if ($dbIndex[$key].effectKey -ne $seedIndex[$key].effectKey) {
                $drift += "effect-key $key db=$($dbIndex[$key].effectKey) seed=$($seedIndex[$key].effectKey)"
            }
        }
        $ruleCheckSql = "SELECT M_IDX, STAGE, SEQ, VALIDATION_KEY FROM dbo.MODULE_VALIDATION_RULE WHERE M_IDX IN ($idList) ORDER BY M_IDX, STAGE, SEQ;"
        $ruleCheckFile = Join-Path $env:TEMP 'opencode/seed-drift-check-rules.txt'
        & sqlcmd -S $Server -d $Database -E -C -f 65001 -h -1 -W -w 200 -s "~" -Q $ruleCheckSql | Out-File -FilePath $ruleCheckFile -Encoding utf8
        $dbRuleIndex = @{}
        foreach ($line in (Get-Content -LiteralPath $ruleCheckFile)) {
            if ($line.Trim() -eq '' -or $line.Trim() -like '(* rows affected)') { continue }
            $cols = $line -split '~'
            if ($cols.Count -lt 4) { continue }
            $dbRuleIndex["$($cols[0].Trim())|$($cols[1].Trim())|$($cols[2].Trim())"] = $cols[3].Trim()
        }
        $seedRuleIndex = @{}
        foreach ($module in $manifest.modules) {
            foreach ($rule in @($module.validations | Where-Object { $_ -ne $null })) {
                $stage = if ($rule.stage) { [string]$rule.stage } else { 'APPROVE' }
                if ($null -eq $rule.seq) { $drift += "rule-no-seq $($module.moduleId)|$stage （清单校验规则缺 seq，请重跑导出器）"; continue }
                $seedRuleIndex["$([int]$module.moduleId)|$stage|$([int]$rule.seq)"] = [string]$rule.validationKey
            }
        }
        foreach ($key in $dbRuleIndex.Keys) {
            if (-not $seedRuleIndex.ContainsKey($key)) { $drift += "rule-missing-in-seed $key [$($dbRuleIndex[$key])]"; continue }
            if ($dbRuleIndex[$key] -ne $seedRuleIndex[$key]) {
                $drift += "rule-key $key db=$($dbRuleIndex[$key]) seed=$($seedRuleIndex[$key])"
            }
        }
        if ($drift.Count -gt 0) {
            Write-Output ('FAIL: 清单落后于库内配置，导入会删库内公式行或回退效果键（共 ' + $drift.Count + ' 处）：')
            $drift | Select-Object -First 40 | ForEach-Object { Write-Output "  $_" }
            Write-Output '处理：先按库内当前配置重建清单（或确认无损失后加 -Force 执行）。'
            exit 3
        }
    }
    Write-Output 'PASS: 种子与库内配置一致，守卫通过（未写库）。'
    exit 0
}

$sql = New-Object System.Text.StringBuilder
[void]$sql.AppendLine('SET NOCOUNT ON;')
[void]$sql.AppendLine('DECLARE @A BIGINT;')

foreach ($module in $manifest.modules) {
    $mid = [int]$module.moduleId
    foreach ($action in @($module.actions)) {
        $event = if ($action.event) { [string]$action.event } else { 'APPROVE_EFFECT' }
        $seq = [int]$action.seq
        $where = "M_IDX = $mid AND EVENT_CODE = " + (Quote $event) + " AND SEQ = $seq"
        [void]$sql.AppendLine("IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION WHERE $where)")
        [void]$sql.AppendLine('BEGIN')
        [void]$sql.AppendLine('    UPDATE dbo.MODULE_BUSINESS_ACTION SET EFFECT_KEY = ' + (Quote $action.effectKey) + ', EFFECT_NAME = ' + (Quote $action.name) + ', ENABLED = 1, FAIL_MODE = ' + (Quote $action.failMode) + ', CONDITION_STRUCT = ' + (Quote $action.condition) + ', PARAM_STRUCT = ' + (Quote $action.params) + ', REVERSE_STRUCT = ' + (Quote $action.reverse) + ', REMARK = ' + (Quote $action.remark) + ', SOURCE_REF = ' + (Quote $action.sourceRef) + ", LAST_UPDATE_BY = N'seed-agent', LAST_UPDATE_DATE = SYSDATETIME() WHERE $where;")
        [void]$sql.AppendLine("    SELECT @A = ACTION_ID FROM dbo.MODULE_BUSINESS_ACTION WHERE $where;")
        [void]$sql.AppendLine('    DELETE FROM dbo.MODULE_BUSINESS_ACTION_OP WHERE ACTION_ID = @A;')
        [void]$sql.AppendLine('END')
        [void]$sql.AppendLine('ELSE')
        [void]$sql.AppendLine('BEGIN')
        [void]$sql.AppendLine('    INSERT dbo.MODULE_BUSINESS_ACTION (M_IDX, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT, PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)')
        [void]$sql.AppendLine('    VALUES (' + $mid + ', ' + (Quote $event) + ', ' + $seq + ', ' + (Quote $action.effectKey) + ', ' + (Quote $action.name) + ', 1, ' + (Quote $action.failMode) + ', ' + (Quote $action.condition) + ', ' + (Quote $action.params) + ', ' + (Quote $action.reverse) + ', ' + (Quote $action.remark) + ', ' + (Quote $action.sourceRef) + ", N'seed-agent', SYSDATETIME());")
        [void]$sql.AppendLine('    SET @A = SCOPE_IDENTITY();')
        [void]$sql.AppendLine('END')
        $opSeq = 1
        foreach ($op in @($action.ops)) {
            [void]$sql.AppendLine('    INSERT dbo.MODULE_BUSINESS_ACTION_OP (ACTION_ID, OP_SEQ, TARGET_TABLE, TARGET_FIELD, OP_CODE, SOURCE_SCOPE, SOURCE_TABLE, SOURCE_FIELD, SOURCE_AGG, SOURCE_CONSTANT, SOURCE_TERMS_STRUCT, MATCH_STRUCT, CONDITION_STRUCT, REMARK)')
            [void]$sql.AppendLine('    VALUES (@A, ' + $opSeq + ', ' + (Quote $op.targetTable) + ', ' + (Quote $op.targetField) + ', ' + (Quote $op.opCode) + ', ' + (Quote $op.sourceScope) + ', ' + (Quote $op.sourceTable) + ', ' + (Quote $op.sourceField) + ', ' + (Quote $op.sourceAgg) + ', ' + (Quote $op.sourceConstant) + ', ' + (Quote $op.sourceTerms) + ', ' + (Quote $op.match) + ', ' + (Quote $op.condition) + ', ' + (Quote $op.remark) + ');')
            $opSeq++
        }
    }

    foreach ($rule in @($module.validations | Where-Object { $_ -ne $null })) {
        $stage = if ($rule.stage) { [string]$rule.stage } else { 'APPROVE' }
        if ($null -eq $rule.seq) { throw "清单模块 $mid 的校验规则缺 seq（请重跑导出器），拒绝导入。" }
        $seq = [int]$rule.seq
        $ruleWhere = "M_IDX = $mid AND STAGE = " + (Quote $stage) + " AND SEQ = $seq"
        [void]$sql.AppendLine("IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE WHERE $ruleWhere)")
        [void]$sql.AppendLine('BEGIN')
        [void]$sql.AppendLine('    UPDATE dbo.MODULE_VALIDATION_RULE SET VALIDATION_KEY = ' + (Quote $rule.validationKey) + ', ENABLED = 1, PARAM_STRUCT = ' + (Quote $rule.params) + ', MESSAGE = ' + (Quote $rule.message) + ', REMARK = ' + (Quote $rule.remark) + ', SOURCE_REF = ' + (Quote $rule.sourceRef) + ", LAST_UPDATE_BY = N'seed-agent', LAST_UPDATE_DATE = SYSDATETIME() WHERE $ruleWhere;")
        [void]$sql.AppendLine('END')
        [void]$sql.AppendLine('ELSE')
        [void]$sql.AppendLine('BEGIN')
        [void]$sql.AppendLine('    INSERT dbo.MODULE_VALIDATION_RULE (M_IDX, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)')
        [void]$sql.AppendLine('    VALUES (' + $mid + ', ' + (Quote $stage) + ', ' + $seq + ', ' + (Quote $rule.validationKey) + ', 1, ' + (Quote $rule.params) + ', ' + (Quote $rule.message) + ', ' + (Quote $rule.remark) + ', ' + (Quote $rule.sourceRef) + ", N'seed-agent', SYSDATETIME());")
        [void]$sql.AppendLine('END')
    }
}

$tempDir = Join-Path $env:TEMP 'opencode'
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
$sqlFile = Join-Path $tempDir 'seed-business-actions.sql'
[IO.File]::WriteAllText($sqlFile, $sql.ToString(), [Text.Encoding]::UTF8)

Write-Output ("执行种子导入（模块数 " + @($manifest.modules).Count + "）…")
& sqlcmd -S $Server -d $Database -E -b -f 65001 -i $sqlFile
if ($LASTEXITCODE -ne 0) { throw "sqlcmd 执行失败 rc=$LASTEXITCODE" }
Write-Output 'PASS: 种子导入完成（幂等，已存在则跳过）'
