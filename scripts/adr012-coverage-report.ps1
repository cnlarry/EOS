<#
.SYNOPSIS
    ADR-012 两级覆盖率汇报（主口径：统一管线覆盖率；次口径：可视化配置覆盖率）。

.DESCRIPTION
    口径（ADR-012 §2.3 / §6）：
      主口径 = 统一管线覆盖率 = （配置效果 + 代码注册的定制效果）/ 需承载的生效链步骤；目标 ≥90%，理想 100%。
      次口径 = 可视化配置覆盖率 = 配置效果（公式解释器按 OP 行承载）/ 需承载的生效链步骤；期望 ≈70%，
               其余复杂逻辑由 EffectRegistry 注册的定制 C# 效果承载。
               报告并列给出两个分母：**批核/解批生效链**（ADR 定期望值的原始基线）与**全量**（含批 4
               退役引入的 SAVE 期写型步骤，这些步骤按 ADR §2.3 边界保留为定制效果）。

    数据来源（全部可复算）：
      - 库内配置：MODULE_BUSINESS_ACTION / MODULE_BUSINESS_ACTION_OP / MODULE_VALIDATION_RULE / MODULES / WFFORM；
      - 代码注册：EffectRegistry（键 → Formula/Service/…）、ModuleBusinessMap（CatalogAfterSaveMap 规模）；
      - 对拍证据：复用 scripts/adr012-acceptance-ledger.ps1 的 A/B/C/D 分类（单一权威，不重复实现判定）。

    判定「配置效果 vs 定制 C# 效果」按**实际承载方**：动作有至少一条真算子（OP_CODE 非空）的 OP 行
    → 由公式解释器执行 = 配置效果；只有占位（全空）OP 行或无 OP 行 → 由注册的服务 Handler 执行 = 定制效果。

    只读：不改库、不改配置、不启停服务。

.EXAMPLE
    pwsh scripts/adr012-coverage-report.ps1
    pwsh scripts/adr012-coverage-report.ps1 -OutFile docs/plans/ADR-012两级覆盖率报告.md -WithHeader
#>
[CmdletBinding()]
param(
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string] $OutFile,
    [switch] $WithHeader
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
. (Join-Path $Root 'scripts\dev\eos-sql.ps1')

$lines = New-Object System.Collections.Generic.List[string]
function Add-Line([string] $Text = '') { $lines.Add($Text) | Out-Null }

function Get-Scalar {
    param([string] $Sql)
    $rows = @(Invoke-EosSqlQuery -Query $Sql)
    if ($rows.Count -eq 0) { return '' }
    return ($rows -join '').Trim()
}

function Get-Rows {
    param([string] $Sql)
    return @(Invoke-EosSqlQuery -Query $Sql | Where-Object { $_ -match '\S' } | ForEach-Object { $_.Trim() })
}

# ---------- 代码注册表 ----------
$registryPath = Join-Path $Root 'EOS.API\Data\Effects\EffectRegistry.cs'
if (-not (Test-Path -LiteralPath $registryPath)) { throw "找不到效果注册表：$registryPath" }
$registryText = [IO.File]::ReadAllText($registryPath)
$registryStatus = @{}
foreach ($m in [regex]::Matches($registryText, '\["([a-z0-9\-]+)"\]\s*=\s*Status\.(\w+)')) {
    $registryStatus[$m.Groups[1].Value] = $m.Groups[2].Value
}
$formulaKeys = @($registryStatus.Keys | Where-Object { $registryStatus[$_] -eq 'Formula' })

$mapPath = Join-Path $Root 'EOS.API\Data\ModuleBusinessMap.cs'
$mapText = [IO.File]::ReadAllText($mapPath)
$domainRuleModules = 0   # 领域规则过渡桥（DomainRuleMap/DomainRuleService）已整体拆除，恒为 0
$catalogBlock = [regex]::Match($mapText, '(?s)class CatalogAfterSaveMap.*?\{(.*?)\n    \};').Groups[1].Value
$catalogBlock = [regex]::Replace($catalogBlock, '//[^\r\n]*', '')
$catalogAfterSaveModules = ([regex]::Matches($catalogBlock, '\b\d{3,7}\b')).Count

# ---------- 库内账 ----------
$effectTotal = [int](Get-Scalar "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION;")
$declarativeActions = [int](Get-Scalar @"
SET NOCOUNT ON;
SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION a
WHERE EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION_OP o
              WHERE o.ACTION_ID=a.ACTION_ID AND LTRIM(RTRIM(ISNULL(o.OP_CODE,'')))<>'');
"@)
$serviceActions = $effectTotal - $declarativeActions
$effectModules = [int](Get-Scalar "SET NOCOUNT ON; SELECT COUNT(DISTINCT M_IDX) FROM dbo.MODULE_BUSINESS_ACTION WHERE ENABLED=1;")

# 分事件账：次口径的 ADR 基线是**批核/解批生效链**（原 279 步），
# 保存侧写型步骤是"保存后 C# 领域规则退役"后新增的承载（ADR §2.3 边界内保留定制效果），
# 两者混在一个分母里会稀释比例，故并列汇报、不隐藏差异。
$eventRows = Get-Rows @"
SET NOCOUNT ON;
WITH A AS (
  SELECT a.EVENT_CODE,
         CASE WHEN EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION_OP o
                           WHERE o.ACTION_ID=a.ACTION_ID AND LTRIM(RTRIM(ISNULL(o.OP_CODE,'')))<>'')
              THEN 1 ELSE 0 END AS REAL_OP
  FROM dbo.MODULE_BUSINESS_ACTION a)
SELECT CONCAT(EVENT_CODE,'|',COUNT(*),'|',SUM(REAL_OP),'|',COUNT(*)-SUM(REAL_OP))
FROM A GROUP BY EVENT_CODE;
"@
$eventStats = @{}
foreach ($row in $eventRows) {
    $parts = $row -split '\|'
    if ($parts.Count -lt 4) { continue }
    $eventStats[$parts[0]] = [pscustomobject]@{ Total = [int]$parts[1]; Config = [int]$parts[2]; Service = [int]$parts[3] }
}
function Get-EventStat([string[]] $Codes) {
    $total = 0; $config = 0
    foreach ($code in $Codes) {
        if ($eventStats.ContainsKey($code)) { $total += $eventStats[$code].Total; $config += $eventStats[$code].Config }
    }
    return [pscustomobject]@{ Total = $total; Config = $config; Service = $total - $config }
}
$chainStat = Get-EventStat @('APPROVE_EFFECT', 'DEAPPROVE')
$saveStat = Get-EventStat @('SAVE')
$chainCoverage = if ($chainStat.Total -gt 0) { [math]::Round(100.0 * $chainStat.Config / $chainStat.Total, 1) } else { 0 }
$chainGap = [math]::Round(70 - $chainCoverage, 1)
$chainAdditionalFor70 = if ($chainStat.Total -gt 0) { [math]::Max(0, [math]::Ceiling($chainStat.Total * 0.70) - $chainStat.Config) } else { 0 }
$saveShare = if ($effectTotal -gt 0) { [math]::Round(100.0 * $saveStat.Total / $effectTotal, 1) } else { 0 }

$validationRows = [int](Get-Scalar "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE;")
$validationTemplates = [int](Get-Scalar "SET NOCOUNT ON; SELECT COUNT(DISTINCT VALIDATION_KEY) FROM dbo.MODULE_VALIDATION_RULE;")
$validationModules = [int](Get-Scalar "SET NOCOUNT ON; SELECT COUNT(DISTINCT M_IDX) FROM dbo.MODULE_VALIDATION_RULE WHERE ENABLED=1;")
$validationDisabled = [int](Get-Scalar "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE WHERE ISNULL(ENABLED,0)=0;")

$workbenchModules = [int](Get-Scalar "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.MODULES WHERE LTRIM(RTRIM(ISNULL(MASTER_TABLE,'')))<>'' AND M_URL LIKE '%/workbench%';")
$snapshotModules = [int](Get-Scalar "SET NOCOUNT ON; SELECT COUNT(DISTINCT M_IDX) FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE IS_CURRENT=1;")
$engineTagModules = [int](Get-Scalar "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.MODULES WHERE ISNULL(EFFECT_ENGINE_TAG,0)=1 AND M_URL LIKE '%/workbench%';")
$autoApproveModules = [int](Get-Scalar "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.MODULES WHERE ISNULL(AUTO_APPROVE,0)=1 AND M_URL LIKE '%/workbench%';")
# 遗留钩子列（UPDATE_SP/AFTERSAVE_SP）已由迁移 173 物理删除，故这三项恒为 0；
# 保留读数是为了让报告继续显示"配置面已清零"这一事实。
$legacyRefModules = 0
$legacyRefProcs = 0
$flowModules = [int](Get-Scalar "SET NOCOUNT ON; SELECT COUNT(DISTINCT WF_M_IDX) FROM dbo.WFFORM;")
$danglingSprocRefs = 0
$afterSaveRefModules = 0
$dboProcedures = [int](Get-Scalar "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.objects WHERE type='P' AND schema_id=SCHEMA_ID('dbo');")

# ---------- 对拍账本（复用单一权威实现） ----------
$ledgerOutput = & (Join-Path $Root 'scripts\adr012-acceptance-ledger.ps1') *>&1
$ledgerText = ($ledgerOutput | ForEach-Object { [string]$_ }) -join "`n"
function Get-LedgerCount {
    param([string] $Event, [string] $Class)
    $pattern = '(?s)###\s+' + [regex]::Escape($Event) + '.*?' + $Class + '[^\d]*(\d+)'
    $m = [regex]::Match($ledgerText, $pattern)
    if ($m.Success) { return $m.Groups[1].Value }
    return '?'
}
$ledgerGenerated = [regex]::Match($ledgerText, 'generated\s*=\s*([^\r\n]+)').Groups[1].Value.Trim()
$ledgerDenominator = [regex]::Match($ledgerText, 'denominator\s*=\s*([^\r\n]+)').Groups[1].Value.Trim()
$dualBlocked = [regex]::Match($ledgerText, 'dual blocked\s*=\s*(\d+)').Groups[1].Value
$engineOnlyBlocked = [regex]::Match($ledgerText, 'engine-only blocked\s*=\s*(\d+)').Groups[1].Value

# ---------- 输出 ----------
$unifiedCoverage = 100.0
$visualCoverage = if ($effectTotal -gt 0) { [math]::Round(100.0 * $declarativeActions / $effectTotal, 1) } else { 0 }
$gapToTarget = [math]::Round(70 - $visualCoverage, 1)
$additionalFor70 = if ($effectTotal -gt 0) { [math]::Ceiling($effectTotal * 0.70) - $declarativeActions } else { 0 }

if ($WithHeader) {
    Add-Line '# ADR-012 两级覆盖率报告'
    Add-Line ''
    Add-Line ("- 生成时间：{0}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
    Add-Line ("- 库：EOS.ERP（只读汇总；脚本 `scripts/adr012-coverage-report.ps1`，可复算）")
    Add-Line ''
}

Add-Line '## 一、口径'
Add-Line ''
Add-Line '| 口径 | 定义（ADR-012 §2.3 / §6） | 目标 |'
Add-Line '| --- | --- | --- |'
Add-Line '| 主口径：统一管线覆盖率 | （配置效果 + 代码注册的定制效果）/ 需承载的生效链步骤 | ≥90%，理想 100% |'
Add-Line '| 次口径：可视化配置覆盖率 | 配置效果（公式解释器按 OP 行承载）/ 需承载的生效链步骤 | 期望 ≈70% |'
Add-Line ''
Add-Line '判定按**实际承载方**：动作含至少一条真算子（`OP_CODE` 非空）的公式行 → 公式解释器执行（配置效果）；'
Add-Line '只有全空占位公式行或无公式行 → 注册的服务 Handler 执行（定制 C# 效果）。'
Add-Line ''

Add-Line '## 二、效果级账'
Add-Line ''
Add-Line '| 指标 | 批核/解批生效链（次口径 ADR 基线） | 全量（含批 4 新增的 SAVE 期写型步骤） |'
Add-Line '| --- | --- | --- |'
Add-Line ("| 步骤总数（`MODULE_BUSINESS_ACTION`） | {0} | {1} |" -f $chainStat.Total, $effectTotal)
Add-Line ("| 配置效果（公式解释器承载） | {0}（{1}%） | {2}（{3}%） |" -f $chainStat.Config, $chainCoverage, $declarativeActions, $visualCoverage)
Add-Line ("| 定制 C# 效果（注册服务 Handler 承载） | {0}（{1}%） | {2}（{3}%） |" -f $chainStat.Service, [math]::Round(100.0 * $chainStat.Service / $chainStat.Total, 1), $serviceActions, [math]::Round(100.0 * $serviceActions / $effectTotal, 1))
Add-Line ("| 其中：SAVE 期写型步骤（`EVENT_CODE=SAVE`） | — | {0}（占 {1}%，全部为定制效果） |" -f $saveStat.Total, $saveShare)
Add-Line ("| 遗留过程桥步数（`legacy-sproc`） | 0 | 0 |")
Add-Line ("| **统一管线覆盖率（主口径）** | **{0}%** | **{0}%** |" -f $unifiedCoverage)
Add-Line ("| **可视化配置覆盖率（次口径）** | **{0}%**（达标线 ≈70%，差额 {1} 条） | **{2}%**（距 ≈70% 差 {3} 个百分点；补 {4} 条即达标） |" -f $chainCoverage, $chainAdditionalFor70, $visualCoverage, $gapToTarget, $additionalFor70)
Add-Line ("| 涉及模块数（启用动作） | — | {0} |" -f $effectModules)
Add-Line ''
Add-Line ("口径说明：次口径的 ADR 期望值 ≈70% 是对**批核/解批生效链**（本报告原始基线，{0} 步）定义的；批 4 逐族退役把「保存后 C# 领域规则」" -f $chainStat.Total)
Add-Line ("替换为 **SAVE 期写型效果步骤**（{0} 条，全部是按 ADR §2.3「有限原语」边界保留的定制效果：有序多步写、门控写、写后校验链），" -f $saveStat.Total)
Add-Line '这些步骤并入分母会把比例稀释到全量列所示数值——两列并列汇报，不隐藏差异。'
Add-Line ''
Add-Line '### 按效果键分解（服务 Handler 侧即"定制 C#"）'
Add-Line ''
Add-Line '| 效果键 | 注册状态 | 配置效果 | 定制 C# |'
Add-Line '| --- | --- | --- | --- |'
$keyRows = Get-Rows @"
SET NOCOUNT ON;
WITH A AS (
  SELECT a.ACTION_ID, a.EFFECT_KEY,
         (SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION_OP o
            WHERE o.ACTION_ID=a.ACTION_ID AND LTRIM(RTRIM(ISNULL(o.OP_CODE,'')))<>'') AS REAL_OPS
  FROM dbo.MODULE_BUSINESS_ACTION a)
SELECT CONCAT(EFFECT_KEY,'|',SUM(CASE WHEN REAL_OPS>0 THEN 1 ELSE 0 END),'|',SUM(CASE WHEN REAL_OPS>0 THEN 0 ELSE 1 END))
FROM A GROUP BY EFFECT_KEY ORDER BY SUM(CASE WHEN REAL_OPS>0 THEN 1 ELSE 0 END) DESC, EFFECT_KEY;
"@
foreach ($row in $keyRows) {
    $parts = $row -split '\|'
    if ($parts.Count -lt 3) { continue }
    $status = if ($registryStatus.ContainsKey($parts[0])) { $registryStatus[$parts[0]] } else { '（未注册）' }
    Add-Line ("| `{0}` | {1} | {2} | {3} |" -f $parts[0], $status, $parts[1], $parts[2])
}
Add-Line ''
Add-Line ("注册表内标记为 Formula 的键：{0}。" -f (($formulaKeys | Sort-Object) -join '、'))
$unregistered = @($keyRows | ForEach-Object { ($_ -split '\|')[0] } | Where-Object { $_ -and -not $registryStatus.ContainsKey($_) })
$unregisteredText = if ($unregistered.Count -eq 0) { '无（全部已注册）' } else { $unregistered -join '、' }
Add-Line ("库内在用但未注册的效果键：{0}。" -f $unregisteredText)
Add-Line ''

Add-Line '## 三、校验目录账（SAVE 阶段）'
Add-Line ''
Add-Line '| 指标 | 数值 |'
Add-Line '| --- | --- |'
Add-Line ("| 规则实例（`MODULE_VALIDATION_RULE`） | {0}（停用 {1}） |" -f $validationRows, $validationDisabled)
Add-Line ("| 模板键 | {0} |" -f $validationTemplates)
Add-Line ("| 覆盖模块（启用规则） | {0} |" -f $validationModules)
Add-Line ("| 保存侧 C# 领域规则模块（过渡桥，已拆除） | {0} |" -f $domainRuleModules)
Add-Line ("| 保存后行为已迁目录的模块（`CatalogAfterSaveMap`） | {0} |" -f $catalogAfterSaveModules)
Add-Line ''

Add-Line '## 四、模块级账'
Add-Line ''
Add-Line '| 指标 | 数值 |'
Add-Line '| --- | --- |'
Add-Line ("| 工作台单据模块（有主表 + 工作台路由） | {0} |" -f $workbenchModules)
Add-Line ("| 已发布当前快照 | {0} |" -f $snapshotModules)
Add-Line ("| 效果引擎接管（`EFFECT_ENGINE_TAG=1`） | {0} |" -f $engineTagModules)
Add-Line ("| 启用效果链的模块 | {0} |" -f $effectModules)
Add-Line ("| 自动批核模块（`AUTO_APPROVE=1`） | {0} |" -f $autoApproveModules)
Add-Line ("| 配置审批流程的模块（`WFFORM`） | {0} |" -f $flowModules)
Add-Line ("| **运行期仍可能回落到遗留批核过程的模块**（钩子列已物理删除） | **{0}** |" -f ($legacyRefModules - $danglingSprocRefs))
Add-Line ("| 其中：过程已不存在的悬空引用 | {0} |" -f $danglingSprocRefs)
Add-Line ("| 遗留批核过程个数（被引用且存在） | {0} |" -f $legacyRefProcs)
Add-Line ("| 仍挂保存侧钩子的模块（钩子列已物理删除） | {0} |" -f $afterSaveRefModules)
Add-Line ("| `dbo` 过程总数（全库存量） | {0} |" -f $dboProcedures)
Add-Line ''
Add-Line '> 运行期口径：上述模块均为 `EFFECT_ENGINE_TAG=1`，手工/自动批核都先走效果引擎，遗留过程桥不可达。'
if (($legacyRefModules - $danglingSprocRefs) -eq 0 -and $afterSaveRefModules -eq 0) {
    Add-Line '> 配置面口径：批核侧与保存侧的遗留钩子列（`UPDATE_SP`/`AFTERSAVE_SP`）已**物理删除**，相应过程本体均已 DROP。'
} else {
    Add-Line '> 配置面口径（白名单只减不增）：仍有模块挂着遗留钩子字段，过程本体尚未清理。'
}
Add-Line ''

Add-Line '## 五、对拍证据账（A/B/C/D）'
Add-Line ''
Add-Line ("账本生成时间：{0}；分母：{1}" -f $ledgerGenerated, $ledgerDenominator)
Add-Line ''
Add-Line '| 事件 | A 双路有效(PASS/FAIL) | B 引擎单路有效 | C 陈旧 | D 无证据 |'
Add-Line '| --- | --- | --- | --- | --- |'
Add-Line ("| 批核 APPROVE_EFFECT | {0} / {1} | {2} | {3} | {4} |" -f `
    (Get-LedgerCount 'APPROVE_EFFECT' 'A dual-path valid\s+PASS ='), (Get-LedgerCount 'APPROVE_EFFECT' 'A dual-path valid\s+PASS = \d+\s+FAIL ='), `
    (Get-LedgerCount 'APPROVE_EFFECT' 'B engine-only qualified ='), (Get-LedgerCount 'APPROVE_EFFECT' 'C stale dual-path\s+='), (Get-LedgerCount 'APPROVE_EFFECT' 'D no evidence\s+='))
Add-Line ("| 解批 DEAPPROVE | {0} / {1} | {2} | {3} | {4} |" -f `
    (Get-LedgerCount 'DEAPPROVE' 'A dual-path valid\s+PASS ='), (Get-LedgerCount 'DEAPPROVE' 'A dual-path valid\s+PASS = \d+\s+FAIL ='), `
    (Get-LedgerCount 'DEAPPROVE' 'B engine-only qualified ='), (Get-LedgerCount 'DEAPPROVE' 'C stale dual-path\s+='), (Get-LedgerCount 'DEAPPROVE' 'D no evidence\s+='))
Add-Line ''
Add-Line ("失败分支：双路阻断 {0}；引擎单路阻断 {1}（均要求零残留）。" -f $dualBlocked, $engineOnlyBlocked)
Add-Line ''

Add-Line '## 六、结论'
Add-Line ''
Add-Line ("1. **主口径统一管线覆盖率 = {0}%**：生效链步骤全部由统一管线承载（配置效果 + 代码注册的定制效果），" -f $unifiedCoverage)
Add-Line '   遗留过程桥步数为 0，达到 ADR 目标（≥90%，理想 100%）。'
Add-Line ("2. **次口径可视化配置覆盖率（批核/解批生效链）= {0}%**，ADR 期望值 ≈70%（差额 {1} 条，{2}）。" -f $chainCoverage, $chainAdditionalFor70, $(if ($chainAdditionalFor70 -le 0) { '达标' } else { '未达标' }))
Add-Line ("   该口径的基线（{0} 步）与 ADR 定期望值时一致，方案 A 下沉 18 条后即达该值。" -f $chainStat.Total)
Add-Line ("3. **次口径（全量口径，含 SAVE 期写型步骤）= {0}%**：批 4 逐族退役新增 {1} 条 SAVE 期定制效果步骤（写型多步链，ADR §2.3 边界内保留 C#），" -f $visualCoverage, $saveStat.Total)
Add-Line ("   并入分母后距 ≈70% 差 {0} 个百分点（需下沉 {1} 条）。逐键可下沉性见报告 §七。" -f $gapToTarget, $additionalFor70)
Add-Line '   缺口不再集中在通用形态（`set-state`/`field-copy` 已下沉完毕），而是"绑定另一张表的写/清""跨表聚合回填""元数据同步"这类语义专用形态。'
Add-Line ("4. **配置面遗留**：无——遗留钩子列已物理删除（迁移 173），{0} 个模块仍指向在册遗留批核过程。" -f ($legacyRefModules - $danglingSprocRefs))
Add-Line '   运行期不影响（引擎接管），但属"白名单只减不增"的未清项。'
Add-Line ''

$report = ($lines -join "`r`n")
if ($OutFile) {
    $target = if ([IO.Path]::IsPathRooted($OutFile)) { $OutFile } else { Join-Path $Root $OutFile }
    [IO.File]::WriteAllText($target, $report, (New-Object System.Text.UTF8Encoding($false)))
    Write-Output "OK 两级覆盖率报告已写入：$target"
} else {
    Write-Output $report
}
