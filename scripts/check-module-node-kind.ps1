<#
.SYNOPSIS
    菜单节点形态（dbo.V_MODULE_NODE）与形态外配置残留巡检。

.DESCRIPTION
    `MODULES` 一行是**菜单节点**，不是"模块"，实测 357 行分三形态：
    统一工作台模块（有主表，M_URL 留空或 /workbench）、自定义承载页（M_URL 是精确路径）、
    纯目录节点（无主表且无承载页）。三形态能配的东西完全不同，而这个判定此前散落在多处
    SQL 里各自推断，写法已经出现分歧（同一行在不同路径上会被判成"是/不是模块"）。

    迁移 340 把判据收口到唯一一处 SQL 定义 `dbo.V_MODULE_NODE`（代码侧对应
    `ModuleRouteValidator.ResolveKind`，两侧共用取值 WORKBENCH / CUSTOMPAGE / DIRECTORY），
    并清掉了非工作台节点上**没有消费方**的工作台配置残留。本脚本守住那次收口的成果：

      ① 判据存在且取值封闭：`dbo.V_MODULE_NODE` 必须在，NODE_KIND 只允许三个取值；
      ② 能力面一致：统一表单名单（`EOS.API/appsettings.json` 的 UnifiedFormEditor，
         写名单 + 只读名单）里的模块必须全是 WORKBENCH。一旦名单里混进自定义承载页，
         菜单管理按形态隐藏主表 / 默认列 / 版式入口就会误伤一个真能开统一表单的模块；
         名单里出现库里不存在的编号也会让"名单"与库漂移。
      ③ 形态外无残留：非工作台节点上不得有排序字段 / 不可解批 / 自动批核 / 可复制 /
         异常不可保存 / 明细必需字段 / 无明细不可保存 / 效果引擎开关 /
         表单打开方式。它们只有工作台定义与效果引擎会消费。
      ④ 脏标记合法：`WORKBENCH_MODULE_DIRTY` 只允许出现在 WORKBENCH 模块上，且不得指向
         已不存在的编号——编号级联语句不覆盖这张表，保存路径已改为"撤销旧编号的行"。

    刻意不查（都有真实消费方，与形态无关）：`MASTER_TABLE` / `DETAIL_TABLE`（报表数据集、
    搜索中心、统一选择器与助手指标的锚点）、`FILTER`（报表与选择器的**实时**数据范围）、
    `SEARCH_1/2`（搜索中心是独立于承载页的可达面）。

.EXAMPLE
    pwsh scripts/check-module-node-kind.ps1     # exit 0 = 形态判据与配置面一致
#>
[CmdletBinding()]
param(
    [string] $ConnectionString
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

try {
    . (Join-Path $PSScriptRoot 'dev/eos-sql.ps1')

    # ① 判据必须存在：缺它说明迁移 340 没落库，后续所有按形态的判定都会跑偏
    $exists = Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query @'
SELECT CASE WHEN OBJECT_ID(N'dbo.V_MODULE_NODE', N'V') IS NULL THEN N'MISSING' ELSE N'OK' END;
'@

    $unknownKind = Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query @'
SELECT N'UNKNOWN_KIND | ' + n.NODE_KIND
FROM dbo.V_MODULE_NODE n
WHERE n.NODE_KIND NOT IN (N'WORKBENCH', N'CUSTOMPAGE', N'DIRECTORY')
GROUP BY n.NODE_KIND;
'@

    # ② 统一表单名单必须全落在 WORKBENCH 上（名单是"运行期有统一表单"的准入面）
    $settingsPath = Join-Path $PSScriptRoot '..\EOS.API\appsettings.json'
    if (-not (Test-Path -LiteralPath $settingsPath)) {
        Write-Output "FAIL module node kind probe error: 找不到 $settingsPath"
        exit 2
    }
    $settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding utf8 | ConvertFrom-Json
    $whitelist = @($settings.UnifiedFormEditor.EnabledModuleIds) + @($settings.UnifiedFormEditor.ReadOnlyModuleIds) |
        Where-Object { $_ -ne $null } | Sort-Object -Unique
    if ($whitelist.Count -eq 0) {
        Write-Output 'FAIL module node kind probe error: UnifiedFormEditor 名单为空，无法核对形态一致性。'
        exit 2
    }
    $values = ($whitelist | ForEach-Object { '(' + [int]$_ + ')' }) -join ','

    $whitelistDrift = Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query @"
SELECT N'NOT_WORKBENCH | ' + CONVERT(nvarchar(20), m.M_IDX) + N' ' + LTRIM(RTRIM(ISNULL(m.M_DESC, N'')))
       + N' 形态=' + n.NODE_KIND
FROM dbo.MODULES m
INNER JOIN dbo.V_MODULE_NODE n ON n.M_IDX = m.M_IDX
WHERE m.M_IDX IN ($values) AND n.NODE_KIND <> N'WORKBENCH'
UNION ALL
SELECT N'MISSING_MODULE | ' + CONVERT(nvarchar(20), v.M_IDX)
FROM (VALUES $values) AS v(M_IDX)
WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = v.M_IDX);
"@

    # ③ 形态外残留：判据用与保存路径相同的归一化口径（空白串按"没填"）
    $residue = Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query @'
SELECT N'RESIDUE | ' + CONVERT(nvarchar(20), m.M_IDX) + N' ' + LTRIM(RTRIM(ISNULL(m.M_DESC, N'')))
       + N' (' + n.NODE_KIND + N')'
FROM dbo.MODULES m
INNER JOIN dbo.V_MODULE_NODE n ON n.M_IDX = m.M_IDX
WHERE n.NODE_KIND <> N'WORKBENCH'
  AND (NULLIF(LTRIM(RTRIM(ISNULL(m.SORT_FIELDS, N''))), N'') IS NOT NULL
    OR NULLIF(LTRIM(RTRIM(ISNULL(m.NOT_BACK_FIELDS, N''))), N'') IS NOT NULL
    OR NULLIF(LTRIM(RTRIM(ISNULL(m.NOT_BACK_FIELDS_M, N''))), N'') IS NOT NULL
    OR NULLIF(LTRIM(RTRIM(ISNULL(m.DETAIL_NO_FIELDS, N''))), N'') IS NOT NULL
    OR NULLIF(LTRIM(RTRIM(ISNULL(m.FORM_OPEN_MODE, N''))), N'') IS NOT NULL
    OR m.FORM_DIALOG_WIDTH IS NOT NULL
    OR m.FORM_DIALOG_HEIGHT IS NOT NULL
    OR ISNULL(m.AUTO_APPROVE, 0) = 1
    OR ISNULL(m.IF_COPY, 0) = 1
    OR ISNULL(m.ERROR_NO_SAVE, 0) = 1
    OR ISNULL(m.DETAIL_NO_SAVE, 0) = 1
    OR ISNULL(m.EFFECT_ENGINE_TAG, 0) = 1);
'@

    # ④ 脏标记只属于 WORKBENCH，且不得指向已不存在的编号
    $dirtyDrift = Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query @'
SELECT N'DIRTY | ' + CONVERT(nvarchar(20), d.M_IDX) + N' 形态=' + ISNULL(n.NODE_KIND, N'(编号不存在)')
FROM dbo.WORKBENCH_MODULE_DIRTY d
LEFT JOIN dbo.V_MODULE_NODE n ON n.M_IDX = d.M_IDX
WHERE n.NODE_KIND IS NULL OR n.NODE_KIND <> N'WORKBENCH';
'@

    # 覆盖范围写入结论（形态分布是这条门禁的"分母"，读的人要一眼看得出查了什么）
    $shape = Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query @'
SELECT N'SHAPE | ' + n.NODE_KIND + N'=' + CONVERT(nvarchar(10), COUNT(*))
FROM dbo.V_MODULE_NODE n
GROUP BY n.NODE_KIND
ORDER BY n.NODE_KIND;
'@
}
catch {
    Write-Output "FAIL module node kind probe error: $($_.Exception.Message)"
    exit 2
}

$exists = @($exists | Where-Object { $_ -and $_.Trim() -ne '' })
$unknownKind = @($unknownKind | Where-Object { $_ -and $_.Trim() -ne '' })
$whitelistDrift = @($whitelistDrift | Where-Object { $_ -and $_.Trim() -ne '' })
$residue = @($residue | Where-Object { $_ -and $_.Trim() -ne '' })
$dirtyDrift = @($dirtyDrift | Where-Object { $_ -and $_.Trim() -ne '' })
$shape = @($shape | Where-Object { $_ -and $_.Trim() -ne '' })

if ($exists.Count -eq 0 -or $exists[0].Trim() -ne 'OK') {
    Write-Output 'FAIL dbo.V_MODULE_NODE is missing (migration 340 has not been applied): every shape-based judgement depends on it.'
    exit 1
}

if ($unknownKind.Count -gt 0) {
    Write-Output 'FAIL dbo.V_MODULE_NODE returns a kind outside the closed set WORKBENCH / CUSTOMPAGE / DIRECTORY:'
    $unknownKind | ForEach-Object { Write-Output "  $_" }
    exit 1
}

if ($whitelistDrift.Count -gt 0) {
    Write-Output 'FAIL unified-form whitelist and node shape disagree (a whitelisted module must be a WORKBENCH node; the whitelist is the only source of "has a unified form at runtime"):'
    $whitelistDrift | ForEach-Object { Write-Output "  $_" }
    exit 1
}

if ($residue.Count -gt 0) {
    Write-Output 'FAIL non-workbench nodes still carry workbench-only configuration (it has no consumer there: the definition is never assembled and the effect engine never runs):'
    $residue | ForEach-Object { Write-Output "  $_" }
    exit 1
}

if ($dirtyDrift.Count -gt 0) {
    Write-Output 'FAIL WORKBENCH_MODULE_DIRTY holds rows for non-workbench or non-existent modules (they would sit in the "pending publish" list forever):'
    $dirtyDrift | ForEach-Object { Write-Output "  $_" }
    exit 1
}

$shapeText = ($shape | ForEach-Object { $_.Trim() -replace '^SHAPE \| ', '' }) -join ', '
Write-Output "PASS module node kind is judged in one place (dbo.V_MODULE_NODE) and the configuration surface follows it ($($whitelist.Count) whitelisted modules checked; shapes: $shapeText)."
exit 0
