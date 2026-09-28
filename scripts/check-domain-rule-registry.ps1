<#
.SYNOPSIS
    保存后行为登记一致性检查：「目录承接」模块必须真的配了校验规则，旧快照不得残留已拆除的领域规则族名。

.DESCRIPTION
    背景：保存侧行为已全部由**校验目录**（`MODULE_VALIDATION_RULE`）与**效果目录**
    （`MODULE_BUSINESS_ACTION`）承载——原先的 C# 领域规则族（`DomainRuleMap` +
    `DomainRuleService` 分派 + 快照里的 `BusinessRule.DomainRule`）已经整体拆除。
    本脚本因此检查三件事：

      1. **目录承接模块必须有启用的 SAVE 期校验规则**：`CatalogAfterSaveMap` 里的模块不再有
         C# 领域规则、也（按设计）不调用遗留钩子，没有校验规则就意味着保存后行为静默消失；
      2. **当前快照不得残留 `BusinessRule.DomainRule` 族名**：字段已从定义模型里删除，残留只说明
         "该模块自退役后从未重新发布"，属配置面卫生问题（运行期不再解析该字段，但要求清零以便审计）；
      3. **`reference-exists` 断言的引用列必须是物理列**：写错会在保存时抛 Msg 207 让整单 500，
         编译、单测都发现不了，必须按物理列拦下。

.PARAMETER SkipDatabase
    只做源码内部一致性检查（目录承接表可解析），不查快照与工作区配置。

.EXAMPLE
    pwsh scripts/check-domain-rule-registry.ps1
#>
[CmdletBinding()]
param(
    [switch] $SkipDatabase
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$failures = @()

# ---- 源码侧：目录承接模块登记表 ----
$mapSource = Get-Content (Join-Path $root 'EOS.API\Data\ModuleBusinessMap.cs') -Raw
$catalogPorted = [regex]::Match($mapSource, 'class CatalogAfterSaveMap(?s).*?Modules = new HashSet<int>\s*\{(.*?)\}', 'Singleline')
if (-not $catalogPorted.Success) { $failures += 'CatalogAfterSaveMap 未找到（源码结构变化，请同步本脚本）' }
$catalogModules = @()
if ($catalogPorted.Success) {
    $catalogModules = @([regex]::Matches($catalogPorted.Groups[1].Value, '\d+') | ForEach-Object { [int]$_.Value })
}

# 过渡桥必须确已拆除（源码里不得再出现领域规则注册表/分派服务）
foreach ($gone in @('EOS.API\Data\DomainRuleService.cs')) {
    if (Test-Path (Join-Path $root $gone)) { $failures += "$gone 仍存在：领域规则过渡桥未拆净" }
}
if ($mapSource -match 'class DomainRuleMap') { $failures += 'ModuleBusinessMap 仍含 DomainRuleMap：领域规则过渡桥未拆净' }

Write-Output ("领域规则过渡桥 = 已拆除；目录承接模块 = {0}" -f $catalogModules.Count)

if ($SkipDatabase) {
    if ($failures.Count -gt 0) { $failures | ForEach-Object { Write-Output "FAIL $_" }; exit 1 }
    Write-Output 'PASS 源码侧一致（未查库）'
    exit 0
}

. (Join-Path $PSScriptRoot 'dev\eos-sql.ps1')

# ---- 库侧：当前快照不得残留已拆除的族名字段 ----
$snapshots = Invoke-EosSqlQuery @"
SET NOCOUNT ON;
SELECT CONCAT(M_IDX, '|', ISNULL(JSON_VALUE(DEFINITION_JSON, '`$.BusinessRule.DomainRule'), ''), '|', VERSION)
FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT
WHERE IS_CURRENT = 1
ORDER BY M_IDX;
"@

$stale = 0
foreach ($line in @($snapshots)) {
    $parts = (($line -join '')) -split '\|'
    if ($parts.Count -lt 3) { continue }
    $moduleId = $parts[0]; $rule = $parts[1]; $version = $parts[2]
    if ([string]::IsNullOrWhiteSpace($rule)) { continue }
    $failures += "快照 module=$moduleId v$version 仍残留已拆除的领域规则族名 '$rule'：该模块自退役后从未重新发布，请重发布一次再验收"
    $stale++
}

# ---- 库侧：目录承接模块必须有启用的 SAVE 期校验规则 ----
foreach ($moduleId in $catalogModules) {
    $enabled = (Invoke-EosSqlQuery "SET NOCOUNT ON; SELECT CONVERT(nvarchar(10), COUNT(*)) FROM dbo.MODULE_VALIDATION_RULE WHERE M_IDX=$moduleId AND STAGE=N'SAVE' AND ENABLED=1;" | ForEach-Object { $_ -join '' })
    if ([int]$enabled -le 0) {
        $failures += "module=$moduleId 登记为'目录承接'，但没有启用的 SAVE 期校验规则：保存后行为会静默消失"
    }
}

# ---- 库侧：reference-exists 断言的列必须真实存在 ----
# 断言左列写错（如把 DEPOT 主档写成不存在 IN_DEPOT_ID）会在保存时抛 Msg 207 让整单 500，
# 这类配置错误编译、单测都发现不了，必须在发布前按物理列拦下。
# 集合式判定：一次查询只返回"有问题的引用列"，避免逐列起 sqlcmd。
$refColumns = Invoke-EosSqlQuery @"
SET NOCOUNT ON;
SELECT CONCAT(x.M_IDX, '|', x.STAGE, '|', x.REF, '|', x.COL)
FROM (
    SELECT r.M_IDX, r.STAGE,
           JSON_VALUE(r.PARAM_STRUCT, '`$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].refTable') COLLATE DATABASE_DEFAULT AS REF,
           JSON_VALUE(r.PARAM_STRUCT, '`$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].refKey.field') COLLATE DATABASE_DEFAULT AS COL
    FROM dbo.MODULE_VALIDATION_RULE r
    CROSS APPLY OPENJSON(r.PARAM_STRUCT, '`$.checks') c
    WHERE r.VALIDATION_KEY = N'reference-exists'
    UNION ALL
    SELECT r.M_IDX, r.STAGE,
           JSON_VALUE(r.PARAM_STRUCT, '`$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].targets[' + CONVERT(nvarchar(3), t.[key]) + '].refTable') COLLATE DATABASE_DEFAULT,
           JSON_VALUE(r.PARAM_STRUCT, '`$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].targets[' + CONVERT(nvarchar(3), t.[key]) + '].refKey.field') COLLATE DATABASE_DEFAULT
    FROM dbo.MODULE_VALIDATION_RULE r
    CROSS APPLY OPENJSON(r.PARAM_STRUCT, '`$.checks') c
    CROSS APPLY OPENJSON(r.PARAM_STRUCT, '`$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].targets') t
    WHERE r.VALIDATION_KEY = N'reference-exists'
    UNION ALL
    SELECT r.M_IDX, r.STAGE,
           JSON_VALUE(r.PARAM_STRUCT, '`$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].refTable') COLLATE DATABASE_DEFAULT,
           JSON_VALUE(r.PARAM_STRUCT, '`$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].join[' + CONVERT(nvarchar(3), j.[key]) + '].target') COLLATE DATABASE_DEFAULT
    FROM dbo.MODULE_VALIDATION_RULE r
    CROSS APPLY OPENJSON(r.PARAM_STRUCT, '`$.checks') c
    CROSS APPLY OPENJSON(r.PARAM_STRUCT, '`$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].join') j
    WHERE r.VALIDATION_KEY = N'reference-exists'
    UNION ALL
    SELECT r.M_IDX, r.STAGE,
           JSON_VALUE(r.PARAM_STRUCT, '`$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].targets[' + CONVERT(nvarchar(3), t.[key]) + '].refTable') COLLATE DATABASE_DEFAULT,
           JSON_VALUE(r.PARAM_STRUCT, '`$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].targets[' + CONVERT(nvarchar(3), t.[key]) + '].join[' + CONVERT(nvarchar(3), j.[key]) + '].target') COLLATE DATABASE_DEFAULT
    FROM dbo.MODULE_VALIDATION_RULE r
    CROSS APPLY OPENJSON(r.PARAM_STRUCT, '`$.checks') c
    CROSS APPLY OPENJSON(r.PARAM_STRUCT, '`$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].targets') t
    CROSS APPLY OPENJSON(r.PARAM_STRUCT, '`$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].targets[' + CONVERT(nvarchar(3), t.[key]) + '].join') j
    WHERE r.VALIDATION_KEY = N'reference-exists'
    UNION ALL
    SELECT r.M_IDX, r.STAGE,
           JSON_VALUE(r.PARAM_STRUCT, '`$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].refTable') COLLATE DATABASE_DEFAULT,
           JSON_VALUE(r.PARAM_STRUCT, '`$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].mismatch.target') COLLATE DATABASE_DEFAULT
    FROM dbo.MODULE_VALIDATION_RULE r
    CROSS APPLY OPENJSON(r.PARAM_STRUCT, '`$.checks') c
    WHERE r.VALIDATION_KEY = N'reference-exists'
) x
WHERE x.REF IS NOT NULL AND x.COL IS NOT NULL AND x.COL <> N'-'
  AND (x.REF LIKE N'%[^A-Za-z0-9_]%' OR x.COL LIKE N'%[^A-Za-z0-9_]%'
       OR OBJECT_ID('dbo.' + x.REF) IS NULL
       OR NOT EXISTS (SELECT 1 FROM sys.columns sc
                      WHERE sc.object_id = OBJECT_ID('dbo.' + x.REF) AND sc.name = x.COL));
"@

$badColumns = 0
foreach ($line in @($refColumns)) {
    $parts = (($line -join '')) -split '\|'
    if ($parts.Count -lt 4) { continue }
    $failures += "module=$($parts[0]) $($parts[1]) reference-exists 的引用列不可用：$($parts[2]).$($parts[3])（保存时会 Msg 207 整单 500）"
    $badColumns++
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output "FAIL $_" }
    Write-Output ("FAIL 保存后行为登记一致性：$($failures.Count) 项（残留族名快照 $stale 个模块，引用列非法 $badColumns 处）")
    exit 1
}

Write-Output ('PASS 保存后行为登记一致（过渡桥已拆除 + 快照无残留族名 + 目录承接模块均有启用规则 + 引用列均为物理列）')
exit 0
