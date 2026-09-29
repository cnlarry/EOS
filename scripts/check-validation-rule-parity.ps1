<#
.SYNOPSIS
    ADR-016 S0 ① 校验规则口径核对：MODULE_VALIDATION_RULE ↔ 代码闭集 ↔ 当前快照 ↔ 运行时加载器。

.DESCRIPTION
    诊断黄金集的地基是"每条 MODULE_VALIDATION_RULE 天然是一个应当能被诊断出来的样本"。
    这个地基成立的前提是**配置事实与运行时口径一致**——否则黄金集会建在纸面规则上。
    本脚本逐层给出"一致 / 不一致清单"：

      ① 阶段闭集：库内 STAGE 全部落在 BusinessActionCatalog.ValidationStages 内；
      ② 键闭集：库内 VALIDATION_KEY 全部落在 ValidationRuleRegistry.KnownKeys 内；
      ③ 参数结构：PARAM_STRUCT 必须是非空 JSON **对象**文本（运行时把这段文本解析成对象，
         数组/标量/坏 JSON 会在模块加载期抛错，使该模块保存与批核全线失败）；
      ④ 落地一致：每一行都必须出现在所属模块**当前快照**的 validationRules 段里，双向比对——
         "配了但引擎读不到"（表有快照无）与"快照落后于配置"（快照有表无）分别报出；
         同时核对配了参数的行在快照里确实带 params；
      ⑤ 运行时口径（默认执行，-SkipRuntime 跳过）：dotnet test 跑 ValidationRuleParityLiveTests，
         把每个模块的快照段逐条喂进真实 EffectPlanLoader（内部即 ValidationRuleRegistry）。

    另打印语料分布（键|阶段|条数|启用数），供黄金集登记对照。

.EXAMPLE
    pwsh scripts/check-validation-rule-parity.ps1
    pwsh scripts/check-validation-rule-parity.ps1 -SkipRuntime   # 只做代码闭集 + 连库两层（快）
#>
[CmdletBinding()]
param(
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [switch] $SkipRuntime
)

$ErrorActionPreference = 'Stop'

function Get-CSharpStringSet {
    <#
      从 C# 源码里取一个字符串闭集（如 KnownKeys / ValidationStages 的花括号初始化）。
      闭集的真源在代码里，脚本只读取，不另立一份清单。
    #>
    param([string] $Source, [string] $Block)
    $match = [regex]::Match($Source, '(?s)' + [regex]::Escape($Block) + '\s*=\s*new\s*(?:HashSet<string>)?[^\{]*\{([^}]*)\}')
    if (-not $match.Success) { throw "未能在源码中找到闭集：$Block" }
    @([regex]::Matches($match.Groups[1].Value, '"([A-Za-z][A-Za-z0-9-]*)"') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
}

$failures = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()

try {
    $catalogPath = Join-Path $Root 'EOS.API\Data\BusinessActionCatalog.cs'
    $registryPath = Join-Path $Root 'EOS.API\Data\ValidationRules\ValidationRuleRegistry.cs'
    $stages = @(Get-CSharpStringSet -Source (Get-Content $catalogPath -Raw) -Block 'ValidationStages')
    $keys = @(Get-CSharpStringSet -Source (Get-Content $registryPath -Raw) -Block 'KnownKeys')

    # 单行结果会退化为字符串：一律 join 后再解析，不用 [0]。
    function Invoke-Scalar {
        param([string] $Query)
        ((Invoke-EosSqlQuery -Query $Query) -join '').Trim()
    }

    function Invoke-Rows {
        param([string] $Query)
        @(Invoke-EosSqlQuery -Query $Query | Where-Object { $_ -and $_.Trim() -ne '' } | ForEach-Object { $_.Trim() })
    }

    . (Join-Path $Root 'scripts\dev\eos-sql.ps1')

    $total = [int](Invoke-Scalar "SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE WITH (NOLOCK);")

    # ① 阶段闭集
    $dbStages = Invoke-Rows "SELECT DISTINCT RTRIM(STAGE) FROM dbo.MODULE_VALIDATION_RULE WITH (NOLOCK) ORDER BY 1;"
    $badStages = @($dbStages | Where-Object { $_ -notin $stages })
    if ($badStages.Count -gt 0) {
        $failures.Add("库内 STAGE 不在代码闭集（$(($stages -join '/'))）内：$($badStages -join ', ')")
    }

    # ② 键闭集
    $dbKeys = Invoke-Rows "SELECT DISTINCT RTRIM(VALIDATION_KEY) FROM dbo.MODULE_VALIDATION_RULE WITH (NOLOCK) ORDER BY 1;"
    $badKeys = @($dbKeys | Where-Object { $_ -notin $keys })
    if ($badKeys.Count -gt 0) {
        $failures.Add("库内 VALIDATION_KEY 不在代码闭集（$(($keys -join '/'))）内：$($badKeys -join ', ')")
    }

    # ③ 参数结构：非空 JSON 对象
    $badParams = Invoke-Rows @'
SELECT CONCAT('module=', M_IDX, ' stage=', RTRIM(STAGE), ' seq=', SEQ, ' key=', RTRIM(VALIDATION_KEY),
              ' param=', CASE WHEN PARAM_STRUCT IS NULL OR LTRIM(RTRIM(PARAM_STRUCT))='' THEN '<empty>'
                              WHEN ISJSON(PARAM_STRUCT)=0 THEN '<invalid-json>'
                              ELSE '<not-object>' END)
FROM dbo.MODULE_VALIDATION_RULE WITH (NOLOCK)
WHERE PARAM_STRUCT IS NULL OR LTRIM(RTRIM(PARAM_STRUCT))='' OR ISJSON(PARAM_STRUCT)=0
   OR LEFT(LTRIM(PARAM_STRUCT),1)<>'{'
ORDER BY M_IDX, STAGE, SEQ;
'@
    if ($badParams.Count -gt 0) {
        $failures.Add("PARAM_STRUCT 不是非空 JSON 对象（运行时会把这段文本解析成对象）：$($badParams -join '; ')")
    }

    # ④ 落地一致：配置表 ↔ 当前快照（双向）
    $snapshotDefined = [int](Invoke-Scalar @'
SELECT COUNT(*) FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT d WITH (NOLOCK)
CROSS APPLY OPENJSON(d.DEFINITION_JSON, '$.validationRules') j
WHERE d.IS_CURRENT=1;
'@)

    $missingInSnapshot = Invoke-Rows @'
SELECT CONCAT('module=', r.M_IDX, ' stage=', RTRIM(r.STAGE), ' seq=', r.SEQ, ' key=', RTRIM(r.VALIDATION_KEY))
FROM dbo.MODULE_VALIDATION_RULE r WITH (NOLOCK)
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT d WITH (NOLOCK)
    CROSS APPLY OPENJSON(d.DEFINITION_JSON, '$.validationRules') j
    WHERE d.IS_CURRENT=1 AND d.M_IDX=r.M_IDX
      AND RTRIM(JSON_VALUE(j.value,'$.stage'))=RTRIM(r.STAGE)
      AND TRY_CONVERT(int, JSON_VALUE(j.value,'$.seq'))=r.SEQ
      AND RTRIM(JSON_VALUE(j.value,'$.validationKey'))=RTRIM(r.VALIDATION_KEY)
      AND ISNULL(TRY_CONVERT(bit, JSON_VALUE(j.value,'$.enabled')),1)=r.ENABLED)
ORDER BY r.M_IDX, r.STAGE, r.SEQ;
'@
    if ($missingInSnapshot.Count -gt 0) {
        $failures.Add("已配置但当前快照里没有（引擎不会执行）：$($missingInSnapshot.Count) 条 → $($missingInSnapshot -join '; ')")
    }

    $missingInTable = Invoke-Rows @'
SELECT CONCAT('module=', d.M_IDX, ' stage=', RTRIM(JSON_VALUE(j.value,'$.stage')), ' seq=', JSON_VALUE(j.value,'$.seq'),
              ' key=', RTRIM(JSON_VALUE(j.value,'$.validationKey')))
FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT d WITH (NOLOCK)
CROSS APPLY OPENJSON(d.DEFINITION_JSON, '$.validationRules') j
WHERE d.IS_CURRENT=1
  AND NOT EXISTS (
    SELECT 1 FROM dbo.MODULE_VALIDATION_RULE r WITH (NOLOCK)
    WHERE r.M_IDX=d.M_IDX
      AND RTRIM(r.STAGE)=RTRIM(JSON_VALUE(j.value,'$.stage'))
      AND r.SEQ=TRY_CONVERT(int, JSON_VALUE(j.value,'$.seq'))
      AND RTRIM(r.VALIDATION_KEY)=RTRIM(JSON_VALUE(j.value,'$.validationKey')))
ORDER BY d.M_IDX;
'@
    if ($missingInTable.Count -gt 0) {
        $failures.Add("快照里有但配置表已无（快照落后于配置，需重新发布）：$($missingInTable.Count) 条 → $($missingInTable -join '; ')")
    }

    # 参数存在性按"键是否存在"判（不能用 JSON_VALUE 取 params 值：快照里 params 是**字符串**，
    # 且长参数超过 JSON_VALUE 默认 4000 字符上限时会返回 NULL，从而整批误报）。
    $paramsDropped = Invoke-Rows @'
SELECT CONCAT('module=', r.M_IDX, ' stage=', RTRIM(r.STAGE), ' seq=', r.SEQ, ' key=', RTRIM(r.VALIDATION_KEY))
FROM dbo.MODULE_VALIDATION_RULE r WITH (NOLOCK)
WHERE r.PARAM_STRUCT IS NOT NULL AND LTRIM(RTRIM(r.PARAM_STRUCT))<>''
  AND EXISTS (
    SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT d WITH (NOLOCK)
    CROSS APPLY OPENJSON(d.DEFINITION_JSON, '$.validationRules') j
    WHERE d.IS_CURRENT=1 AND d.M_IDX=r.M_IDX
      AND RTRIM(JSON_VALUE(j.value,'$.stage'))=RTRIM(r.STAGE)
      AND TRY_CONVERT(int, JSON_VALUE(j.value,'$.seq'))=r.SEQ
      AND RTRIM(JSON_VALUE(j.value,'$.validationKey'))=RTRIM(r.VALIDATION_KEY))
  AND NOT EXISTS (
    SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT d WITH (NOLOCK)
    CROSS APPLY OPENJSON(d.DEFINITION_JSON, '$.validationRules') j
    CROSS APPLY OPENJSON(j.value) p
    WHERE d.IS_CURRENT=1 AND d.M_IDX=r.M_IDX
      AND RTRIM(JSON_VALUE(j.value,'$.stage'))=RTRIM(r.STAGE)
      AND TRY_CONVERT(int, JSON_VALUE(j.value,'$.seq'))=r.SEQ
      AND RTRIM(JSON_VALUE(j.value,'$.validationKey'))=RTRIM(r.VALIDATION_KEY)
      AND p.[key]=N'params' AND p.value IS NOT NULL AND LTRIM(RTRIM(p.value))<>'')
ORDER BY r.M_IDX, r.STAGE, r.SEQ;
'@
    if ($paramsDropped.Count -gt 0) {
        $failures.Add("配置带参数但快照里丢了 params（运行时按空对象执行，规则会静默放宽）：$($paramsDropped -join '; ')")
    }

    # ⑤ 运行时口径：真实 EffectPlanLoader + ValidationRuleRegistry 逐条
    $runtimeNote = 'SKIP（-SkipRuntime）'
    if (-not $SkipRuntime) {
        $artifacts = Join-Path ([System.IO.Path]::GetTempPath()) 'eos-validation-rule-parity'
        # --artifacts-path：构建输出离开默认 bin，避免与正在运行的 EOS.API 进程争用文件锁
        $output = & dotnet test (Join-Path $Root 'EOS.API.Tests\EOS.API.Tests.csproj') `
            --nologo -v minimal --filter 'FullyQualifiedName~ValidationRuleParityLiveTests' `
            --artifacts-path $artifacts 2>&1
        $runtimeExit = $LASTEXITCODE
        if ($runtimeExit -ne 0) {
            $tail = @($output | Where-Object { $_ -match 'error|失败|Error Message|Assert' } | Select-Object -First 12)
            $failures.Add("运行时口径核对失败（dotnet test exit=$runtimeExit）：$($tail -join ' | ')")
            $runtimeNote = "FAIL"
        } else {
            $runtimeNote = 'PASS（3 项断言）'
        }
    }

    # 语料分布（黄金集登记对照；不参与判定）
    $distribution = Invoke-Rows @'
SELECT CONCAT(RTRIM(VALIDATION_KEY), '|', RTRIM(STAGE), '|', COUNT(*), '|enabled=', SUM(CAST(ENABLED AS int)))
FROM dbo.MODULE_VALIDATION_RULE WITH (NOLOCK)
GROUP BY VALIDATION_KEY, STAGE
ORDER BY VALIDATION_KEY, STAGE;
'@
    $saveCount = [int](Invoke-Scalar "SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE WITH (NOLOCK) WHERE STAGE=N'SAVE';")
    $moduleCount = [int](Invoke-Scalar "SELECT COUNT(DISTINCT M_IDX) FROM dbo.MODULE_VALIDATION_RULE WITH (NOLOCK);")

    Write-Output "校验规则语料：$total 条 / 覆盖 $moduleCount 模块 / STAGE=SAVE $saveCount 条 / 快照内 $snapshotDefined 条"
    $distribution | ForEach-Object { Write-Output "  INFO $_" }
    Write-Output "代码闭集：阶段 $($stages -join '/') ｜ 键 $($keys.Count) 个"
    Write-Output "运行时口径（EffectPlanLoader + ValidationRuleRegistry）：$runtimeNote"
    foreach ($warning in $warnings) { Write-Output "INFO $warning" }

    if ($failures.Count -gt 0) {
        Write-Output "FAIL ADR-016 S0 校验规则口径核对：$($failures.Count) 项不一致"
        $failures | ForEach-Object { Write-Output "  $_" }
        exit 1
    }

    Write-Output "PASS ADR-016 S0 校验规则口径一致（$total 条逐条落在闭集内、全部进入当前快照、已参数结构合法、运行时全部接受）。"
    exit 0
} catch {
    Write-Output "FAIL ADR-016 S0 校验规则口径核对执行失败：$(($_.Exception.Message -replace "`r?`n", ' ').Trim())"
    exit 1
}
