<#
.SYNOPSIS
    模块级表单版式巡检：版式行不得悬空、越界或把不该藏的字段藏起来。

.DESCRIPTION
    表单版式从字段级（FIELDS.FORM_*）搬到模块级（MODULE_FORM_LAYOUT）后，"配置错"
    第一次成为一种**可能**：版式行引用不存在的模块/表/字段、跨度超出模块列数、
    把复合格配成半成品、把必填字段从表单上拿掉。这些错配**编译与单测都照不出来**，
    界面上的表现是"字段莫名其妙不见了"或"单据存不下去"。

    七条断言（任一命中即 FAIL，exit 1）：
      ① 主从成对：复合格从字段（CELL_ROLE=2）必须有同组主字段（MODULE_FORM_LAYOUT；字段级
         `FIELDS.FORM_CELL_*` 已退役）；
      ② 幽灵引用：版式行引用的模块、表、字段必须在库内真实存在；
      ③ 越界字段：版式行的 T_ID 必须是该模块的主表或明细表；
      ④ 隐藏了用户可填的必填列（含"已定制的表里整行缺失"）；
      ⑤ SPAN 超出**该行所属页签的栅格列数**（MODULE_FORM_TAB.LAYOUT_COLUMNS，NOT NULL DEFAULT 4；
        页签行缺失时按兜底 4 判）、ROW_SPAN 超出 1..3；
      ⑥ 已有版式行的模块，其已发布快照必须已反映该版式（否则运行期按旧版式渲染，
         且没有任何标记提示——与快照落后门禁同口径）；
      ⑦ 持当前快照的模块必须都有版式行（表级默认推导的输入列已退役，"无版式行"不再等价于
         "按元数据顺序渲染"而是"未定制"；缺行 = 表单缺页签、缺复合格、备注不再整行）。

    -SelfTest 做判别力自检：把同一批判据用在合成样本上，命中面必须与预期一致
    （"该抓的抓住、不该抓的放过"，否则全绿没有意义）。只跑 SELECT，不改任何数据。

.EXAMPLE
    pwsh scripts/check-form-layout.ps1
    pwsh scripts/check-form-layout.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [string] $ConnectionString,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# 生命周期/审计列：由服务端写入，不属于"用户可填"，被隐藏不算违规
$systemColumnList = "N'CREATE_PERSON', N'CREATE_DATE', N'LAST_UPDATE_BY', N'LAST_UPDATE_DATE', N'CONFIRM_PERSON', N'CONFIRM_DATE', N'FINISHED_PERSON', N'FINISHED_DATE'"

# ① 主从成对（模块级版式）：字段级 `FIELDS.FORM_CELL_*` 已退役，判据只落在版式表上
$orphanLayoutSql = @'
SELECT N'ORPHAN_LAYOUT | M_IDX=' + CONVERT(nvarchar(12), l.M_IDX) + N' | ' + LTRIM(RTRIM(l.T_ID)) + N'.' + LTRIM(RTRIM(l.F_ID))
       + N' | CELL_GROUP=' + LTRIM(RTRIM(ISNULL(l.CELL_GROUP, N'<null>'))) + N' | CELL_ROLE=2'
FROM dbo.MODULE_FORM_LAYOUT l
WHERE ISNULL(l.CELL_ROLE, 0) = 2 AND ISNULL(l.CELL_GROUP, N'') <> N''
  AND NOT EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT g
                  WHERE g.M_IDX = l.M_IDX AND g.T_ID = l.T_ID
                    AND ISNULL(g.CELL_GROUP, N'') = l.CELL_GROUP AND ISNULL(g.CELL_ROLE, 0) = 1)
ORDER BY l.M_IDX, l.T_ID, l.F_ID;
'@

# ② 幽灵引用：模块 / 表 / 字段三者必须存在
$ghostSql = @'
SELECT N'GHOST | M_IDX=' + CONVERT(nvarchar(12), l.M_IDX) + N' | ' + LTRIM(RTRIM(l.T_ID)) + N'.' + LTRIM(RTRIM(l.F_ID))
       + N' | 原因=' + CASE
             WHEN NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = l.M_IDX) THEN N'模块不存在'
             WHEN OBJECT_ID(N'dbo.' + LTRIM(RTRIM(l.T_ID)), N'U') IS NULL THEN N'表不存在'
             ELSE N'字段未注册' END
FROM dbo.MODULE_FORM_LAYOUT l
WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = l.M_IDX)
   OR OBJECT_ID(N'dbo.' + LTRIM(RTRIM(l.T_ID)), N'U') IS NULL
   OR NOT EXISTS (SELECT 1 FROM dbo.FIELDS f
                  WHERE f.T_ID = LTRIM(RTRIM(l.T_ID)) AND LTRIM(RTRIM(f.F_ID)) = LTRIM(RTRIM(l.F_ID)))
ORDER BY l.M_IDX, l.T_ID, l.F_ID;
'@

# ③ 越界字段：T_ID 必须是该模块的主表或明细表
$foreignFieldSql = @'
SELECT N'FOREIGN_FIELD | M_IDX=' + CONVERT(nvarchar(12), l.M_IDX) + N' | ' + LTRIM(RTRIM(l.T_ID)) + N'.' + LTRIM(RTRIM(l.F_ID))
       + N' | 模块表=' + ISNULL(NULLIF(LTRIM(RTRIM(m.MASTER_TABLE)), N''), N'<空>')
       + N'+' + ISNULL(NULLIF(LTRIM(RTRIM(m.DETAIL_TABLE)), N''), N'<无>')
FROM dbo.MODULE_FORM_LAYOUT l
JOIN dbo.MODULES m ON m.M_IDX = l.M_IDX
WHERE LTRIM(RTRIM(l.T_ID)) <> LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N'')))
  AND LTRIM(RTRIM(l.T_ID)) <> LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, N'')))
ORDER BY l.M_IDX, l.T_ID, l.F_ID;
'@

# ④ 隐藏了用户可填的必填列；以及"已定制的表里必填字段整行缺失"（效果与隐藏相同）
$hiddenRequiredSql = @"
SELECT N'HIDDEN_REQUIRED | M_IDX=' + CONVERT(nvarchar(12), l.M_IDX) + N' | ' + LTRIM(RTRIM(l.T_ID)) + N'.' + LTRIM(RTRIM(l.F_ID))
       + N' | 必填来源=' + CASE WHEN COALESCE(f.IS_VERIFY, 0) = 1 THEN N'IS_VERIFY'
                                ELSE N'NOT NULL 且无默认值' END
FROM dbo.MODULE_FORM_LAYOUT l
JOIN dbo.FIELDS f ON f.T_ID = LTRIM(RTRIM(l.T_ID)) AND LTRIM(RTRIM(f.F_ID)) = LTRIM(RTRIM(l.F_ID))
WHERE l.IS_HIDDEN = 1
  AND LTRIM(RTRIM(f.F_ID)) NOT IN ($systemColumnList)
  AND COALESCE(f.IS_VISIBLE, 1) = 1 AND COALESCE(f.IS_READONLY, 0) = 0 AND COALESCE(f.IS_VIRTUAL, 0) = 0
  AND (COALESCE(f.IS_VERIFY, 0) = 1
       OR EXISTS (SELECT 1 FROM sys.columns c
                  JOIN sys.objects o ON o.object_id = c.object_id AND o.type = 'U'
                  WHERE o.name = LTRIM(RTRIM(l.T_ID)) AND c.name = LTRIM(RTRIM(l.F_ID))
                    AND c.is_nullable = 0 AND c.is_identity = 0 AND c.default_object_id = 0))
ORDER BY l.M_IDX, l.T_ID, l.F_ID;
"@

$missingRequiredSql = @"
SELECT N'MISSING_REQUIRED | M_IDX=' + CONVERT(nvarchar(12), l.M_IDX) + N' | ' + LTRIM(RTRIM(l.T_ID)) + N'.' + LTRIM(RTRIM(f.F_ID))
FROM dbo.MODULE_FORM_LAYOUT l
JOIN dbo.FIELDS f ON f.T_ID = LTRIM(RTRIM(l.T_ID))
JOIN sys.columns c ON c.object_id = OBJECT_ID(N'dbo.' + LTRIM(RTRIM(l.T_ID))) AND c.name = LTRIM(RTRIM(f.F_ID))
JOIN sys.objects o ON o.object_id = c.object_id AND o.type = 'U'
WHERE COALESCE(f.IS_VERIFY, 0) = 1
  AND LTRIM(RTRIM(f.F_ID)) NOT IN ($systemColumnList)
  AND COALESCE(f.IS_VISIBLE, 1) = 1 AND COALESCE(f.IS_READONLY, 0) = 0 AND COALESCE(f.IS_VIRTUAL, 0) = 0
  AND NOT EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT x
                  WHERE x.M_IDX = l.M_IDX AND LTRIM(RTRIM(x.T_ID)) = LTRIM(RTRIM(l.T_ID))
                    AND LTRIM(RTRIM(x.F_ID)) = LTRIM(RTRIM(f.F_ID)))
GROUP BY l.M_IDX, l.T_ID, f.F_ID
ORDER BY l.M_IDX, l.T_ID, f.F_ID;
"@

# ⑤ 跨度越界：SPAN 必须落在 1..**该行所属页签的列数**、ROW_SPAN 必须落在 1..3。列数是**页签级事实**
#    （MODULE_FORM_TAB.LAYOUT_COLUMNS，NOT NULL DEFAULT 4；模块级那层自迁移 319 起就不存在）：
#    按固定 4 判会把"2 列页签里 span=3"这类错配放过去，按模块单值判则会把页签 2 的合法格误判越界。
#    页签行缺失（脏数据）时按兜底 4 判，不静默放过。
$spanSql = @'
SELECT N'SPAN_RANGE | M_IDX=' + CONVERT(nvarchar(12), l.M_IDX) + N' | ' + LTRIM(RTRIM(l.T_ID)) + N'.' + LTRIM(RTRIM(l.F_ID))
       + N' | TAB_NO=' + CONVERT(nvarchar(12), l.TAB_NO)
       + N' | SPAN=' + CONVERT(nvarchar(12), l.SPAN)
       + N' / 列数=' + CONVERT(nvarchar(12), ISNULL(t.LAYOUT_COLUMNS, 4))
       + N' | ROW_SPAN=' + CONVERT(nvarchar(12), l.ROW_SPAN)
FROM dbo.MODULE_FORM_LAYOUT l
LEFT JOIN dbo.MODULE_FORM_TAB t WITH (NOLOCK) ON t.M_IDX = l.M_IDX AND t.TAB_NO = l.TAB_NO
WHERE l.SPAN < 1 OR l.SPAN > ISNULL(t.LAYOUT_COLUMNS, 4)
   OR l.ROW_SPAN < 1 OR l.ROW_SPAN > 3
ORDER BY l.M_IDX, l.T_ID, l.F_ID;
'@

# ⑥ 已有版式行的模块：已发布快照必须已反映该版式（定制标志为真且行数与库内一致）
$staleLayoutSql = @'
WITH counts AS (
    SELECT l.M_IDX, LTRIM(RTRIM(l.T_ID)) AS T_ID, COUNT(*) AS ROWS_
    FROM dbo.MODULE_FORM_LAYOUT l
    GROUP BY l.M_IDX, LTRIM(RTRIM(l.T_ID)))
SELECT N'LAYOUT_NOT_PUBLISHED | M_IDX=' + CONVERT(nvarchar(12), c.M_IDX) + N' | ' + c.T_ID
       + N' | 版式行=' + CONVERT(nvarchar(12), c.ROWS_)
       + N' | 快照=' + CASE WHEN s.DEFINITION_JSON IS NULL THEN N'缺失'
                            ELSE N'未含版式段' END
FROM counts c
LEFT JOIN dbo.WORKBENCH_DEFINITION_SNAPSHOT s ON s.M_IDX = c.M_IDX AND s.IS_CURRENT = 1
WHERE s.DEFINITION_JSON IS NULL
   OR s.DEFINITION_JSON NOT LIKE N'%FormLayout%'
   -- 快照里的版式段为空（历史快照）同样算未反映；引号用 CHAR(34) 拼，避免命令行传递引号
   OR s.DEFINITION_JSON LIKE N'%FormLayout' + CHAR(34) + N':null%';
'@

# ⑦ 持当前快照的模块必须都有版式行（表级默认推导的输入列已退役，"无版式行"不再等价于
#    "按元数据顺序渲染"，而是"该表未定制"——若某模块一条版式行都没有，它的表单会缺页签、
#    缺复合格、备注不再整行。历史上正是这条缺失让两个模块静默退化（删列棘轮把它们当例外放行）。
$snapshotWithoutLayoutSql = @'
SELECT N'SNAPSHOT_WITHOUT_LAYOUT | M_IDX=' + CONVERT(nvarchar(12), s.M_IDX)
       + N' | 快照版本=' + CONVERT(nvarchar(12), s.VERSION)
FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT s
WHERE s.IS_CURRENT = 1
  AND NOT EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT l WHERE l.M_IDX = s.M_IDX)
ORDER BY s.M_IDX;
'@

# 判别力自检：同一批判据用在合成样本上（正例必须命中、反例必须放过）
$selfTestSql = @'
WITH fields_row(T_ID, F_ID, CELL_GROUP, CELL_ROLE, IS_VERIFY, IS_VISIBLE, IS_READONLY, IS_VIRTUAL) AS (
    SELECT * FROM (VALUES
        (N'T', N'MAIN',      N'G', CAST(1 AS int), CAST(0 AS bit), CAST(1 AS bit), CAST(0 AS bit), CAST(0 AS bit)),
        (N'T', N'COMPANION', N'G', CAST(2 AS int), CAST(1 AS bit), CAST(1 AS bit), CAST(0 AS bit), CAST(0 AS bit))
    ) AS f(T_ID, F_ID, CELL_GROUP, CELL_ROLE, IS_VERIFY, IS_VISIBLE, IS_READONLY, IS_VIRTUAL)),
layout_row(M_IDX, T_ID, F_ID, SPAN, ROW_SPAN, IS_HIDDEN, CELL_GROUP, CELL_ROLE) AS (
    SELECT * FROM (VALUES
        (CAST(1 AS int), N'T', N'MAIN',      CAST(2 AS tinyint), CAST(1 AS tinyint), CAST(0 AS bit), N'G',  CAST(1 AS int)),
        (CAST(1 AS int), N'T', N'COMPANION', CAST(1 AS tinyint), CAST(1 AS tinyint), CAST(0 AS bit), N'G',  CAST(2 AS int)),
        (CAST(1 AS int), N'T', N'ORPHAN',    CAST(1 AS tinyint), CAST(1 AS tinyint), CAST(0 AS bit), N'G2', CAST(2 AS int)),
        (CAST(1 AS int), N'T', N'MAIN',      CAST(9 AS tinyint), CAST(4 AS tinyint), CAST(1 AS bit), N'G',  CAST(1 AS int)),
        (CAST(1 AS int), N'T', N'COMPANION', CAST(1 AS tinyint), CAST(1 AS tinyint), CAST(1 AS bit), N'G',  CAST(2 AS int))
    ) AS l(M_IDX, T_ID, F_ID, SPAN, ROW_SPAN, IS_HIDDEN, CELL_GROUP, CELL_ROLE)),
-- 模块 1 有版式行、模块 2 没有：后者必须被 ⑦ 命中
snapshot_row(M_IDX) AS (
    SELECT * FROM (VALUES (CAST(1 AS int)), (CAST(2 AS int))) AS s(M_IDX)),
-- 列数判据必须按**行所属页签**（模块级那层自迁移 319 起就不存在）：
-- 模块 1 页签行缺失（脏数据 → 兜底 4 列判）、模块 3 页签声明 2 列、
-- 模块 4 页签声明 1 列、模块 5 页签 2 声明 2 列而页签 1 是 4 列（同模块不同页签各判各的）
tab_row(M_IDX, TAB_NO, LAYOUT_COLUMNS) AS (
    SELECT * FROM (VALUES
        (CAST(3 AS int), CAST(1 AS int), CAST(2 AS tinyint)),
        (CAST(4 AS int), CAST(1 AS int), CAST(1 AS tinyint)),
        (CAST(5 AS int), CAST(1 AS int), CAST(4 AS tinyint)),
        (CAST(5 AS int), CAST(2 AS int), CAST(2 AS tinyint))
    ) AS t(M_IDX, TAB_NO, LAYOUT_COLUMNS)),
span_row(M_IDX, TAB_NO, SPAN, ROW_SPAN) AS (
    SELECT * FROM (VALUES
        (CAST(1 AS int), CAST(1 AS int), CAST(9 AS tinyint), CAST(1 AS tinyint)),  -- 页签行缺失：按兜底 4 列判，9 越界
        (CAST(1 AS int), CAST(1 AS int), CAST(4 AS tinyint), CAST(1 AS tinyint)),  -- 页签行缺失：4 合法
        (CAST(3 AS int), CAST(1 AS int), CAST(3 AS tinyint), CAST(1 AS tinyint)),  -- 页签声明 2 列：3 越界
        (CAST(3 AS int), CAST(1 AS int), CAST(2 AS tinyint), CAST(1 AS tinyint)),  -- 页签声明 2 列：2 合法
        (CAST(4 AS int), CAST(1 AS int), CAST(1 AS tinyint), CAST(1 AS tinyint)),  -- 页签声明 1 列：1 合法
        (CAST(4 AS int), CAST(1 AS int), CAST(2 AS tinyint), CAST(1 AS tinyint)),  -- 页签声明 1 列：2 越界
        (CAST(5 AS int), CAST(1 AS int), CAST(4 AS tinyint), CAST(1 AS tinyint)),  -- 页签 1 四列：4 合法
        (CAST(5 AS int), CAST(2 AS int), CAST(4 AS tinyint), CAST(1 AS tinyint))   -- 页签 2 两列：4 越界
    ) AS s(M_IDX, TAB_NO, SPAN, ROW_SPAN))
SELECT N'ORPHAN_LAYOUT=' + CONVERT(nvarchar(12), (SELECT COUNT(*) FROM layout_row l
              WHERE ISNULL(l.CELL_ROLE,0) = 2 AND ISNULL(l.CELL_GROUP,N'') <> N''
                AND NOT EXISTS (SELECT 1 FROM layout_row g
                                WHERE g.M_IDX = l.M_IDX AND g.T_ID = l.T_ID AND g.CELL_GROUP = l.CELL_GROUP AND g.CELL_ROLE = 1)))
     + N'|DUP_MAIN=0'
     + N'|SPAN_RANGE=' + CONVERT(nvarchar(12), (SELECT COUNT(*) FROM span_row l
              LEFT JOIN tab_row t ON t.M_IDX = l.M_IDX AND t.TAB_NO = l.TAB_NO
              WHERE l.SPAN < 1 OR l.SPAN > ISNULL(t.LAYOUT_COLUMNS, 4)
                 OR l.ROW_SPAN < 1 OR l.ROW_SPAN > 3))
     + N'|HIDDEN_REQUIRED=' + CONVERT(nvarchar(12), (SELECT COUNT(*) FROM layout_row l
              JOIN fields_row f ON f.T_ID = l.T_ID AND f.F_ID = l.F_ID
              WHERE l.IS_HIDDEN = 1 AND f.IS_VERIFY = 1 AND f.IS_VISIBLE = 1 AND f.IS_READONLY = 0 AND f.IS_VIRTUAL = 0))
     + N'|SNAPSHOT_NO_LAYOUT=' + CONVERT(nvarchar(12), (SELECT COUNT(*) FROM snapshot_row s
              WHERE NOT EXISTS (SELECT 1 FROM layout_row l WHERE l.M_IDX = s.M_IDX)));
'@

function Invoke-LayoutQuery {
    param([string] $Sql)
    return @(Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query $Sql | Where-Object { $_ -and $_.Trim() -ne '' })
}

try {
    . (Join-Path $PSScriptRoot 'dev/eos-sql.ps1')

    $tables = Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query `
        "SELECT CAST(COUNT(*) AS varchar(10)) FROM sys.tables WHERE name IN (N'MODULE_FORM_LAYOUT', N'MODULE_FORM_TAB');"
    if ("$tables".Trim() -ne '2') {
        Write-Output 'FAIL 版式表未就位（MODULE_FORM_LAYOUT / MODULE_FORM_TAB）：先落库对应迁移。'
        exit 1
    }

    # ⑤ 的列数判据读 MODULE_FORM_TAB.LAYOUT_COLUMNS（页签级、NOT NULL DEFAULT 4）；
    # 模块级那一列（迁移 319 之前短暂存在过）不该回来——它还在，说明库结构不对，直接给结论而不是让探针抛 SQL 报错。
    # 这段守卫放在 -SelfTest 之后：自检跑的是合成样本（不读真实列），不该因为"迁移还没落库"而跑不了。
    $columnGuards = {
        $columnsColumn = Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query `
            "SELECT CAST(COUNT(*) AS varchar(10)) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.MODULES') AND name = N'FORM_LAYOUT_COLUMNS';"
        if ("$columnsColumn".Trim() -ne '0') {
            Write-Output 'FAIL 模块级列数不该存在（MODULES.FORM_LAYOUT_COLUMNS）：先落库迁移 319。'
            exit 1
        }

        $tabColumnsColumn = Invoke-EosSqlQuery -ConnectionString $ConnectionString -Query `
            "SELECT CAST(COUNT(*) AS varchar(10)) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.MODULE_FORM_TAB') AND name = N'LAYOUT_COLUMNS' AND is_nullable = 0;"
        if ("$tabColumnsColumn".Trim() -ne '1') {
            Write-Output 'FAIL 页签级列数元数据未就位（MODULE_FORM_TAB.LAYOUT_COLUMNS NOT NULL）：先落库迁移 319。'
            exit 1
        }
    }

    if ($SelfTest) {
        $line = (Invoke-LayoutQuery -Sql $selfTestSql | Select-Object -First 1)
        $expected = 'ORPHAN_LAYOUT=1|DUP_MAIN=0|SPAN_RANGE=4|HIDDEN_REQUIRED=1|SNAPSHOT_NO_LAYOUT=1'
        if ("$line".Trim() -ne $expected) {
            Write-Output "FAIL -SelfTest 判别力不符：期望 $expected（版式孤儿 1、越界跨度 4〔页签行缺失 1 条 + 页签声明 2 列 1 条 + 页签声明 1 列 1 条 + 同模块页签 2 两列 1 条〕、被隐藏的必填 1），实得 $line"
            exit 3
        }
        Write-Output "PASS -SelfTest 判别力：$line（正例逐条命中，反例未误报）"
    }

    # 自检先跑（它只看合成样本），随后校验"新列已落库"——没落库的门禁跑不了，给出明确结论
    & $columnGuards

    $hits = @()
    $hits += Invoke-LayoutQuery -Sql $orphanLayoutSql
    # 同组多主字段不再判失败：既有数据里存在组名复用（如 HR_DIMISSION_M 的 EMP 组），
# 渲染侧把它当两个独立格处理，不会串格（与服务端校验器同口径）。
    $hits += Invoke-LayoutQuery -Sql $ghostSql
    $hits += Invoke-LayoutQuery -Sql $foreignFieldSql
    $hits += Invoke-LayoutQuery -Sql $hiddenRequiredSql
    $hits += Invoke-LayoutQuery -Sql $missingRequiredSql
    $hits += Invoke-LayoutQuery -Sql $spanSql
    $hits += Invoke-LayoutQuery -Sql $staleLayoutSql
    $hits += Invoke-LayoutQuery -Sql $snapshotWithoutLayoutSql
}
catch {
    Write-Output "FAIL form layout probe error: $($_.Exception.Message)"
    exit 2
}

if ($hits.Count -gt 0) {
    Write-Output "FAIL 表单版式存在 $($hits.Count) 处结构问题（悬空引用 / 越界占位 / 必填被移除 / 快照未反映版式 / 持快照却无版式行）："
    $hits | ForEach-Object { Write-Output "  $_" }
    exit 1
}

Write-Output 'PASS 表单版式结构一致：主从成对、引用真实、未越界、必填字段均在表单内、已定制的模块快照已反映版式、持快照的模块均有版式行。'
exit 0
