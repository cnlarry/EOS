<#
.SYNOPSIS
    单据生命周期列结构巡检：状态位/建立组必须 NOT NULL DEFAULT，经办人/日期列必须保持可空。

.DESCRIPTION
    空值口径原先只由一次性全库迁移落地，而发布门 lifecycle_columns 只检查"列是否存在"：
    新表若把 CONFIRM_TAG/FINISHED_TAG/CREATE_PERSON/CREATE_DATE 建成可空、或漏掉 DEFAULT
    约束，编译、单测与发布都不会报警，但管线写入与存量读路径依赖这些约束兜底。本脚本按
    dbo 用户表逐列核对两个方向：
      · 收紧组（状态位 + 建立组）：必须 is_nullable=0 且有 DEFAULT 约束，缺失即 FAIL；
      · 可空组（LAST_UPDATE_* / CONFIRM_PERSON,DATE / FINISHED_PERSON,DATE）：必须保持可空
        （NULL = 事件尚未发生，不得收紧），违反仅提示，不阻断。
    计算列不参与（不可 ALTER、无默认值语义）。

    另加一条"能力 → 列"巡检：模块声明了会触达 CONFIRM_TAG 的批核能力（自动批核 /
    效果引擎接管 / 启用中的批核·解批效果链）而主表没有该列，即 FAIL。发布门
    lifecycle_columns 已按同一口径把关，但只在有人尝试发布该模块时才跑；从未发布、
    也没人打算发布的模块会一直躲过检查，直到有人真去打它的批核端点才炸（已发生一次）。
    本巡检只覆盖**统一工作台模块**（判据 `dbo.V_MODULE_NODE.NODE_KIND = 'WORKBENCH'`）：
    这三个标志位只在工作台上有消费方，目录节点与自定义承载页既不装配工作台定义、
    也不跑效果引擎，按它们判定会报出并不存在的问题。

    再加一条"列 → 字段元数据"巡检：表上已有批核列（CONFIRM_TAG/PERSON/DATE）且该表
    已进入字段元数据体系（FIELDS 里有行），却漏登记批核列的 FIELDS 行，即 FAIL。
    表单与列表按 FIELDS 渲染，缺行等于列不存在（批核状态看不到、选列也选不出来）；
    两处元数据的写入分属不同迁移，先后顺序一错就会留下这种半截状态（已发生一次）。

.EXAMPLE
    pwsh scripts/check-lifecycle-columns.ps1          # exit 0 = 结构口径一致
    pwsh scripts/check-lifecycle-columns.ps1 -Strict  # 可空组漂移也视为失败
#>
[CmdletBinding()]
param(
    [string] $ConnectionString,
    [switch] $Strict
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

try {
    . (Join-Path $PSScriptRoot 'dev/eos-sql.ps1')

    # 收紧组：应为 NOT NULL 且有 DEFAULT 约束
    $tight = Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query @'
SELECT N'NULLABLE | dbo.' + o.name + N'.' + c.name
FROM sys.columns c
JOIN sys.objects o ON c.object_id = o.object_id AND o.type = N'U'
JOIN sys.schemas s ON o.schema_id = s.schema_id
LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = o.object_id AND dc.parent_column_id = c.column_id
WHERE s.name = N'dbo' AND c.is_computed = 0 AND c.is_nullable = 1
  AND c.name IN (N'CONFIRM_TAG', N'FINISHED_TAG', N'CREATE_PERSON', N'CREATE_DATE')
UNION ALL
SELECT N'NO_DEFAULT | dbo.' + o.name + N'.' + c.name
FROM sys.columns c
JOIN sys.objects o ON c.object_id = o.object_id AND o.type = N'U'
JOIN sys.schemas s ON o.schema_id = s.schema_id
LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = o.object_id AND dc.parent_column_id = c.column_id
WHERE s.name = N'dbo' AND c.is_computed = 0 AND dc.object_id IS NULL
  AND c.name IN (N'CONFIRM_TAG', N'FINISHED_TAG', N'CREATE_PERSON', N'CREATE_DATE');
'@

    # 可空组：应保持可空（NULL = 事件尚未发生）
    $loose = Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query @'
SELECT N'dbo.' + o.name + N'.' + c.name
FROM sys.columns c
JOIN sys.objects o ON c.object_id = o.object_id AND o.type = N'U'
JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE s.name = N'dbo' AND c.is_computed = 0 AND c.is_nullable = 0
  AND c.name IN (N'LAST_UPDATE_BY', N'LAST_UPDATE_DATE', N'CONFIRM_PERSON', N'CONFIRM_DATE',
                 N'FINISHED_PERSON', N'FINISHED_DATE');
'@

    # 能力 → 列：模块声明了会触达 CONFIRM_TAG 的批核能力，主表就必须有该列。
    # 发布门 lifecycle_columns 已按同一口径把关，但那只在"有人尝试发布该模块"时才跑；
    # 从未发布、也没人打算发布的模块（配置表被批量复制标志位带上 AUTO_APPROVE 就是这种）
    # 会一直躲过检查，直到有人真去打它的批核端点才炸。这里按库内事实常态巡检。
    # 只覆盖能在 SQL 里精确判定的三个来源；工作流来源（已配置流程）由发布门负责。
    #
    # 形态门：这三个标志位只在统一工作台模块上有消费方（工作台定义装配 + 效果引擎），
    # 目录节点与自定义承载页既不装配定义、也不跑效果引擎，菜单保存会把它们归一成默认值。
    # 不加这道门，历史残留就会让门禁报出并不存在的问题（实测 5 个自定义承载页模块
    # 带着 AUTO_APPROVE=1，而它们的批核端点根本不存在）。判据只有 dbo.V_MODULE_NODE 一处。
    $capability = Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query @'
SELECT N'AUTO_APPROVE | ' + CONVERT(nvarchar(20), m.M_IDX) + N' ' + LTRIM(RTRIM(ISNULL(m.M_DESC, N'')))
       + N' -> dbo.' + LTRIM(RTRIM(m.MASTER_TABLE)) + N' 缺 CONFIRM_TAG'
FROM dbo.MODULES m
INNER JOIN dbo.V_MODULE_NODE n ON n.M_IDX = m.M_IDX AND n.NODE_KIND = N'WORKBENCH'
WHERE ISNULL(m.AUTO_APPROVE, 0) = 1
  AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) <> N''
  AND OBJECT_ID(N'dbo.' + LTRIM(RTRIM(m.MASTER_TABLE)), N'U') IS NOT NULL
  AND COL_LENGTH(N'dbo.' + LTRIM(RTRIM(m.MASTER_TABLE)), N'CONFIRM_TAG') IS NULL
UNION ALL
SELECT N'EFFECT_ENGINE | ' + CONVERT(nvarchar(20), m.M_IDX) + N' ' + LTRIM(RTRIM(ISNULL(m.M_DESC, N'')))
       + N' -> dbo.' + LTRIM(RTRIM(m.MASTER_TABLE)) + N' 缺 CONFIRM_TAG'
FROM dbo.MODULES m
INNER JOIN dbo.V_MODULE_NODE n ON n.M_IDX = m.M_IDX AND n.NODE_KIND = N'WORKBENCH'
WHERE ISNULL(m.EFFECT_ENGINE_TAG, 0) = 1
  AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) <> N''
  AND OBJECT_ID(N'dbo.' + LTRIM(RTRIM(m.MASTER_TABLE)), N'U') IS NOT NULL
  AND COL_LENGTH(N'dbo.' + LTRIM(RTRIM(m.MASTER_TABLE)), N'CONFIRM_TAG') IS NULL
UNION ALL
SELECT N'APPROVE_EFFECT | ' + CONVERT(nvarchar(20), m.M_IDX) + N' ' + LTRIM(RTRIM(ISNULL(m.M_DESC, N'')))
       + N' -> dbo.' + LTRIM(RTRIM(m.MASTER_TABLE)) + N' 缺 CONFIRM_TAG'
FROM dbo.MODULES m
INNER JOIN dbo.V_MODULE_NODE n ON n.M_IDX = m.M_IDX AND n.NODE_KIND = N'WORKBENCH'
WHERE EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a
              WHERE a.M_IDX = m.M_IDX AND a.ENABLED = 1
                AND a.EVENT_CODE IN (N'APPROVE_EFFECT', N'DEAPPROVE'))
  AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) <> N''
  AND OBJECT_ID(N'dbo.' + LTRIM(RTRIM(m.MASTER_TABLE)), N'U') IS NOT NULL
  AND COL_LENGTH(N'dbo.' + LTRIM(RTRIM(m.MASTER_TABLE)), N'CONFIRM_TAG') IS NULL;
'@

    # 列 → 字段元数据：表上已有批核列，且这张表已登记过字段元数据（FIELDS 里有行），
    # 就必须连批核列一起登记。只对有字段元数据的表判定：纯技术表（审计、快照、队列等）
    # 本就不进表单体系，不能按同一把尺子要求。
    $fieldMeta = Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query @'
SELECT N'MISSING_FIELD_META | dbo.' + t.name + N'.' + c.name
FROM sys.tables t
JOIN sys.schemas s ON t.schema_id = s.schema_id
JOIN sys.columns c ON c.object_id = t.object_id AND c.is_computed = 0
WHERE s.name = N'dbo'
  AND c.name IN (N'CONFIRM_TAG', N'CONFIRM_PERSON', N'CONFIRM_DATE')
  AND EXISTS (SELECT 1 FROM dbo.FIELDS f WHERE LTRIM(RTRIM(f.T_ID)) = t.name)
  AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f
                  WHERE LTRIM(RTRIM(f.T_ID)) = t.name AND LTRIM(RTRIM(f.F_ID)) = c.name);
'@
}
catch {
    Write-Output "FAIL lifecycle column probe error: $($_.Exception.Message)"
    exit 2
}

$tight = @($tight | Where-Object { $_ -and $_.Trim() -ne '' })
$loose = @($loose | Where-Object { $_ -and $_.Trim() -ne '' })
$capability = @($capability | Where-Object { $_ -and $_.Trim() -ne '' })
$fieldMeta = @($fieldMeta | Where-Object { $_ -and $_.Trim() -ne '' })

if ($loose.Count -gt 0) {
    Write-Output "WARN lifecycle actor/date columns tightened to NOT NULL (NULL means the event never happened; keep them nullable):"
    $loose | ForEach-Object { Write-Output "  $_" }
}

if ($fieldMeta.Count -gt 0) {
    Write-Output "FAIL lifecycle approve columns exist on tables that already have field metadata but have no FIELDS row (the column cannot be shown or selected):"
    $fieldMeta | ForEach-Object { Write-Output "  $_" }
    exit 1
}

if ($capability.Count -gt 0) {
    Write-Output "FAIL modules declare approve capability but their master table has no CONFIRM_TAG (removing the flag or adding the column are both valid; pick one deliberately):"
    $capability | ForEach-Object { Write-Output "  $_" }
    exit 1
}

if ($tight.Count -gt 0) {
    Write-Output "FAIL lifecycle status/create columns must be NOT NULL with a DEFAULT constraint:"
    $tight | ForEach-Object { Write-Output "  $_" }
    exit 1
}

if ($Strict -and $loose.Count -gt 0) {
    Write-Output 'FAIL -Strict: nullable-column drift is treated as a failure.'
    exit 1
}

Write-Output "PASS lifecycle columns match the nullability contract, approve-capable modules have CONFIRM_TAG, and every registered table exposes its approve columns as fields ($($loose.Count) non-blocking drift notice(s))."
exit 0
