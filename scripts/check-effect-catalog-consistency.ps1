<#
.SYNOPSIS
    ADR-012 three-way catalog consistency gate (ADR §18 一致性验收).

.DESCRIPTION
    Asserts that the effect/validation key sets are aligned across the three
    authoritative sources:
      A) the ADR catalog (docs/decisions/ADR-012 §18 effect table, §14.2 validation table)
      B) the 2301 frontend catalog (EOS.API/Data/BusinessActionCatalog.cs)
      C) the code registries (EOS.API/Data/Effects/EffectRegistry.cs,
         EOS.API/Data/ValidationRules/ValidationRuleRegistry.cs)

    Documented exemptions kept in sync with the ADR:
      - effect keys retired from the code but still listed in ADR §18 history:
        return-writeback, sample-stock-adjust, mould-balance-adjust
      - reserved effect keys registered in code/catalog with no config instances:
        meta-link, flow-trigger, job-enqueue (listed in ADR prose)
      - §14.2 concept templates that are catalogued but not yet implemented as
        MODULE_VALIDATION_RULE keys (stock-sufficient, balance-sufficient,
        period-valid, state-guard, cross-consistency, custom-validation)

    The gate fails on any real drift (a code key missing from the ADR, an ADR key
    neither implemented nor retired, or the two code catalogs disagreeing).

.EXAMPLE
    pwsh scripts/check-effect-catalog-consistency.ps1   # exit 0 = consistent
#>
[CmdletBinding()]
param(
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [switch] $SkipDatabase
)

$ErrorActionPreference = 'Stop'

$adr = Get-Content (Join-Path $Root 'docs\decisions\ADR-012-单据行为效果配置化.md') -Raw
$catalog = Get-Content (Join-Path $Root 'EOS.API\Data\BusinessActionCatalog.cs') -Raw
$registry = Get-Content (Join-Path $Root 'EOS.API\Data\Effects\EffectRegistry.cs') -Raw
$vregistry = Get-Content (Join-Path $Root 'EOS.API\Data\ValidationRules\ValidationRuleRegistry.cs') -Raw

$retiredEffects = @('return-writeback', 'sample-stock-adjust', 'mould-balance-adjust')
$reservedEffects = @('meta-link', 'flow-trigger', 'job-enqueue')
$conceptTemplates = @('stock-sufficient', 'balance-sufficient', 'period-valid', 'state-guard', 'cross-consistency', 'custom-validation')

function Get-AdrEffectKeys {
    $section = [regex]::Match($adr, '(?s)## 18\..*$').Value
    @([regex]::Matches($section, '(?m)^\|\s*([a-z][a-z0-9-]*)\s*\|') |
        ForEach-Object { $_.Groups[1].Value } |
        Where-Object { $_ -ne 'Key' } | Sort-Object -Unique)
}

function Get-AdrValidationKeys {
    $section = [regex]::Match($adr, '(?s)### 14\.2.*?(?=\n### 14\.3)').Value
    @([regex]::Matches($section, '(?m)^\|\s*([a-z][a-z0-9-]*)\s') |
        ForEach-Object { $_.Groups[1].Value } |
        Where-Object { $_ -ne '校验模板' } | Sort-Object -Unique)
}

function Get-CSharpKeys {
    param([string] $Source, [string] $Block)
    $blockMatch = [regex]::Match($Source, '(?s)' + $Block + '\s*=\s*new\s*(?:HashSet<string>)?[^\{]*\{([^}]*)\}')
    if (-not $blockMatch.Success) { throw "C# block not found: $Block" }
    @([regex]::Matches($blockMatch.Groups[1].Value, '"([a-z][a-z0-9-]*)"') | ForEach-Object { $_.Groups[1].Value })
}

$adrEffects = Get-AdrEffectKeys
$adrValidations = Get-AdrValidationKeys
$catalogEffects = Get-CSharpKeys -Source $catalog -Block 'EffectKeys'
$catalogValidations = Get-CSharpKeys -Source $catalog -Block 'ValidationKeys'
$registryEffects = @([regex]::Matches($registry, '"([a-z][a-z0-9-]*)"\]\s*=\s*Status\.') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$registryValidations = Get-CSharpKeys -Source $vregistry -Block 'KnownKeys'

$failures = @()
$warnings = @()

# 0) reverse-kind three-way consistency (ADR §13.6 table vs EffectStructSchemas.ReverseKinds vs DB usage)
$schemas = Get-Content (Join-Path $Root 'EOS.API\Data\EffectStructSchemas.cs') -Raw
$codeReverseKinds = @(Get-CSharpKeys -Source $schemas -Block 'ReverseKinds' | Sort-Object -Unique)
$adrReverseKinds = @([regex]::Matches(
        [regex]::Match($adr, '(?s)### 13\.6.*?(?=\n### 13\.7|\n## 14\.)').Value,
        '(?m)^\|\s*([a-z][a-z0-9-]*)\s*\|') |
    ForEach-Object { $_.Groups[1].Value } |
    Where-Object { $_ -ne 'kind' } | Sort-Object -Unique)

$missingInAdr = @($codeReverseKinds | Where-Object { $_ -notin $adrReverseKinds })
if ($missingInAdr.Count -gt 0) { $failures += "reverse kinds missing from ADR §13.6 table: $($missingInAdr -join ', ')" }
$missingInCode = @($adrReverseKinds | Where-Object { $_ -notin $codeReverseKinds })
if ($missingInCode.Count -gt 0) { $failures += "reverse kinds in ADR §13.6 but not in EffectStructSchemas.ReverseKinds: $($missingInCode -join ', ')" }

# DB side (optional): every kind actually configured must be inside the code enum; also report tolerances.
if (-not $SkipDatabase) {
    try {
        . (Join-Path $Root 'scripts\dev\eos-sql.ps1')
        $dbKinds = @(Invoke-EosSqlQuery -Query "SELECT DISTINCT JSON_VALUE(REVERSE_STRUCT,'`$.kind') FROM dbo.MODULE_BUSINESS_ACTION WHERE REVERSE_STRUCT IS NOT NULL AND LTRIM(RTRIM(REVERSE_STRUCT))<>'';" |
            Where-Object { $_ -and $_.Trim() -ne '' } | ForEach-Object { $_.Trim() } | Sort-Object -Unique)
        $unknown = @($dbKinds | Where-Object { $_ -notin $codeReverseKinds })
        if ($unknown.Count -gt 0) { $failures += "reverse kinds used in DB but not in the code enum: $($unknown -join ', ')" }

        # 单位置标量：Invoke-EosSqlQuery 对单行结果会退化为字符串，不能用 [0]（那会取到首个字符）
        $emptyReverse = ((Invoke-EosSqlQuery -Query "SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION WHERE REVERSE_STRUCT IS NULL OR LTRIM(RTRIM(REVERSE_STRUCT))='';") -join '').Trim()
        $placeholderOps = ((Invoke-EosSqlQuery -Query "SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION_OP WHERE ISNULL(TARGET_TABLE,'')='' AND ISNULL(TARGET_FIELD,'')='' AND ISNULL(OP_CODE,'')='';") -join '').Trim()
        $warnings += "tolerated stock (ADR §13.6): empty REVERSE_STRUCT = $emptyReverse, all-empty placeholder OP rows = $placeholderOps (clean up in the next republish batch)"
        $warnings += "reverse kinds in DB = $($dbKinds.Count): $($dbKinds -join ', ')"

        # 自定义按钮"配了却没人能按"：MANUAL 行的按钮授权是 **fail-closed** 的（配了、发布了，
        # 不等于有人能按），组与个人两侧都没有 ALLOW_TAG=1 的行时，这条人工入口**等于不存在**。
        # 2026-09-26 实测撞上过：1304 的「生成快照」长期无人被授权，直到端到端复核拿到 403 才发现。
        # 报成**告警**而不是失败：按钮可以先配好、上线时再发授权（那是正常节奏）；
        # 但它不该是静默状态——这就是这条断言存在的理由。
        $unreachable = @(Invoke-EosSqlQuery -Query @'
SELECT CONCAT(N'module=', a.M_IDX, N' key=', RTRIM(a.EFFECT_KEY))
  FROM dbo.MODULE_BUSINESS_ACTION a
 WHERE RTRIM(a.EVENT_CODE) = N'MANUAL' AND a.ENABLED = 1
   AND NOT EXISTS (SELECT 1 FROM dbo.SYSDH_BUTTON g
                    WHERE g.M_IDX = a.M_IDX AND RTRIM(g.BUTTON_KEY) = RTRIM(a.EFFECT_KEY) AND g.ALLOW_TAG = 1)
   AND NOT EXISTS (SELECT 1 FROM dbo.SYSDD_BUTTON p
                    WHERE p.M_IDX = a.M_IDX AND RTRIM(p.BUTTON_KEY) = RTRIM(a.EFFECT_KEY) AND p.ALLOW_TAG = 1)
 ORDER BY a.M_IDX;
'@ | Where-Object { $_ -and $_.Trim() -ne '' } | ForEach-Object { $_.Trim() })
        if ($unreachable.Count -gt 0) {
            $warnings += "自定义按钮已配置但无人被授权（fail-closed 授权未发放，按钮等于不存在）：$($unreachable -join '; ')"
        }

        # MATCH_STRUCT ↔ 效果关系边（FIELD_RELATION, RELATION_KIND=EFFECT）：口径与保存期校验/登记器一致。
        # MASTER/DETAIL 直接取主/明细表；TABLE 域优先 JSON 内联 table，其次取公式行 SOURCE_TABLE 列。
        # 判定按"关系对"（from 表.列 → to 表.列）——边表主键就是这四列（同一列表对只存一行），
        # 来源域只记录最近一次登记口径，故不再要求 scope 相等（否则同一关系既作明细又作显式上下文表时必有一侧误报）。
        $driftSql = @'
SET NOCOUNT ON;
;WITH m AS (
    SELECT a.M_IDX, o.TARGET_TABLE, o.MATCH_STRUCT, o.SOURCE_TABLE AS OP_SOURCE_TABLE, MD.MASTER_TABLE, MD.DETAIL_TABLE
    FROM dbo.MODULE_BUSINESS_ACTION_OP o
    JOIN dbo.MODULE_BUSINESS_ACTION a ON a.ACTION_ID = o.ACTION_ID
    JOIN dbo.MODULES MD ON MD.M_IDX = a.M_IDX
    WHERE o.MATCH_STRUCT IS NOT NULL AND LTRIM(RTRIM(o.MATCH_STRUCT)) <> ''
), items AS (
    SELECT m.M_IDX, m.TARGET_TABLE,
           JSON_VALUE(j.value, '$.target') AS TO_COLUMN,
           JSON_VALUE(j.value, '$.source.scope') AS SCOPE,
           JSON_VALUE(j.value, '$.source.field') AS FROM_COLUMN,
           CASE JSON_VALUE(j.value, '$.source.scope')
                WHEN 'MASTER' THEN m.MASTER_TABLE
                WHEN 'DETAIL' THEN m.DETAIL_TABLE
                WHEN 'TABLE'  THEN COALESCE(JSON_VALUE(j.value, '$.source.table'), m.OP_SOURCE_TABLE)
                ELSE COALESCE(JSON_VALUE(j.value, '$.source.table'), m.MASTER_TABLE)
           END AS FROM_TABLE
    FROM m CROSS APPLY OPENJSON(m.MATCH_STRUCT) j
)
SELECT CAST(COUNT(*) AS varchar(10)) + '|' + CAST(COUNT(DISTINCT i.M_IDX) AS varchar(10))
FROM items i
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.FIELD_RELATION fr
    WHERE fr.RELATION_KIND = 'EFFECT' AND fr.TO_TABLE = i.TARGET_TABLE AND fr.TO_COLUMN = i.TO_COLUMN
      AND fr.FROM_COLUMN = i.FROM_COLUMN AND fr.FROM_TABLE = i.FROM_TABLE);
'@
        $driftRow = ((Invoke-EosSqlQuery -Query $driftSql) -join '').Trim()
        $driftCount = [int](($driftRow -split '\|')[0])
        if ($driftCount -gt 0) {
            $failures += "MATCH_STRUCT positional keys not covered by the effect-edge catalog: $driftCount tuples (run scripts/register-field-relation-edges.ps1)"
        }
        $warnings += "match-struct tuples not covered by effect edges = $driftCount"
    }
    catch {
        $warnings += "DB side skipped: $(($_.Exception.Message -replace "`r?`n", ' ').Trim())"
    }
}

# 1) two code catalogs must agree on effect keys
if ((Compare-Object $catalogEffects $registryEffects).Count -ne 0) {
    $failures += "effect keys: BusinessActionCatalog differs from EffectRegistry"
    $failures += "  catalog-only: $((Compare-Object $catalogEffects $registryEffects | Where-Object SideIndicator -eq '<=' | ForEach-Object InputObject) -join ', ')"
    $failures += "  registry-only: $((Compare-Object $catalogEffects $registryEffects | Where-Object SideIndicator -eq '=>' | ForEach-Object InputObject) -join ', ')"
}

# 2) ADR §18 must cover every non-reserved, non-retired code effect key
$expectedAdr = @($catalogEffects | Where-Object { $_ -notin $reservedEffects })
$missingFromAdr = @($expectedAdr | Where-Object { $_ -notin $adrEffects })
if ($missingFromAdr.Count -gt 0) {
    $failures += "effect keys missing from ADR §18: $($missingFromAdr -join ', ')"
}
$undocumentedInCode = @($adrEffects | Where-Object { $_ -notin $catalogEffects -and $_ -notin $retiredEffects })
if ($undocumentedInCode.Count -gt 0) {
    $failures += "effect keys in ADR §18 but neither implemented nor retired: $($undocumentedInCode -join ', ')"
}

# 3) two code catalogs must agree on validation keys
if ((Compare-Object $catalogValidations $registryValidations).Count -ne 0) {
    $failures += "validation keys: BusinessActionCatalog differs from ValidationRuleRegistry"
    $failures += "  catalog-only: $((Compare-Object $catalogValidations $registryValidations | Where-Object SideIndicator -eq '<=' | ForEach-Object InputObject) -join ', ')"
    $failures += "  registry-only: $((Compare-Object $catalogValidations $registryValidations | Where-Object SideIndicator -eq '=>' | ForEach-Object InputObject) -join ', ')"
}

# 4) every implemented validation key must be documented in ADR §14.2
$missingValidationFromAdr = @($registryValidations | Where-Object { $_ -notin $adrValidations })
if ($missingValidationFromAdr.Count -gt 0) {
    $failures += "validation keys missing from ADR §14.2: $($missingValidationFromAdr -join ', ')"
}

if ($failures.Count -gt 0) {
    Write-Output "FAIL ADR-012 three-way catalog consistency:"
    $failures | ForEach-Object { Write-Output "  $_" }
    $warnings | ForEach-Object { Write-Output "  INFO $_" }
    exit 1
}

foreach ($warning in $warnings) { Write-Output "INFO $warning" }
Write-Output ("PASS ADR-012 three-way catalog consistent (effects {0}, validations {1}, reverse kinds {2})." -f `
        $catalogEffects.Count, $registryValidations.Count, $codeReverseKinds.Count)
exit 0
