-- ============================================================================
-- EOS.ERP migration 340: 菜单节点形态收口（dbo.V_MODULE_NODE）与非工作台节点的配置残留清理
-- ----------------------------------------------------------------------------
-- 背景：`MODULES` 一行是**菜单节点**，不是"模块"。实测 357 行分三形态：
--       · 统一工作台模块 282 —— 有主表，M_URL 留空或 /workbench；
--       · 自定义承载页   28 —— M_URL 是精确路径，业务由页面自己解释，不装配工作台定义；
--       · 纯目录节点     47 —— 无主表、无承载页，只承担层级 / 排序 / 图标 / 权限锚点。
--       三形态能配的东西完全不同，而"这行是什么"此前只在各处 SQL 里各自推断
--       （`M_URL LIKE '/workbench%'`、`MASTER_TABLE IS NOT NULL AND M_URL IN ('','/workbench')` …），
--       写法已经出现分歧：同一行在不同路径上会被判成"是/不是模块"，而工作台定义装配、
--       字段级权限、报表可见性、助手模块清单都按模块号判定。
--
-- 决策（用户 2026-10-08 拍板）：形态**不**落成列（同一事实的第二处来源，与迁移 320/321
--       刚清掉的那类"两处真源"同病），收口到唯一一处 SQL 定义 `dbo.V_MODULE_NODE`；
--       代码侧唯一入口是 `ModuleRouteValidator.ResolveKind`，两侧共用取值
--       WORKBENCH / CUSTOMPAGE / DIRECTORY。同时清掉非工作台节点上**没有消费方**的
--       工作台配置残留：它们是历史默认值，留着会让 `check-lifecycle-columns` 门禁
--       （按 AUTO_APPROVE / EFFECT_ENGINE_TAG 要求主表必须有 CONFIRM_TAG）与"待发布"
--       清单报出并不存在的问题。
--
-- 刻意不动的四类（都有真实消费方，与形态无关）：
--   · MASTER_TABLE / DETAIL_TABLE —— 报表数据集（`REPORT.M_IDX → MODULES.MASTER_TABLE`）、
--     搜索中心、统一选择器、业务流"表↔模块"映射、助手指标都按它取数；实测 11 个自定义承载页
--     模块挂着 20 张报表，清掉主表等于让那些报表当场变空数据源；
--   · FILTER —— 同时是报表的数据范围与选择器的数据范围，被**实时**读取（不经快照）；
--   · SEARCH_1/2 —— 搜索中心是独立于承载页的可达面（`/search-center`），有主表就仍然可用；
--   · M_DESC / M_ALIAS / M_ICON / M_TAG / SYSQL_DEFAULT —— 菜单呈现与既有列配置。
--
-- 编号：339 已被并行会话占用（跨表视图），本脚本取 340；编号只是排序键，不要求连续。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;
-- 引用视图与过滤索引的 DML 要求这两个选项为 ON，而执行入口的默认值不一致（sqlcmd 默认 OFF、
-- SqlClient 默认 ON）：脚本自带，结果与入口无关（同迁移 087 的口径）。
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;
GO

-- ① 形态判据：唯一一处 SQL 定义 -------------------------------------------------
IF OBJECT_ID(N'dbo.V_MODULE_NODE', N'V') IS NOT NULL
    DROP VIEW dbo.V_MODULE_NODE;
GO

CREATE VIEW dbo.V_MODULE_NODE
AS
-- 规则必须与 ModuleRouteValidator.ResolveKind 逐字一致（先判工作台，再判承载页留空，其余是自定义承载页）：
--   WORKBENCH  = M_URL 以 /workbench 开头，或 M_URL 留空但有主表（留空有主表即默认落统一工作台）
--   DIRECTORY  = 不是工作台且 M_URL 留空（既无承载页也无主表，只剩层级与权限锚点）
--   CUSTOMPAGE = 其余（M_URL 是非 /workbench 的精确路径）
-- 空白串一律按"没填"处理（NULLIF + LTRIM/RTRIM），与 C# 侧的 IsNullOrWhiteSpace 对齐。
SELECT m.M_IDX,
       CASE
           WHEN LTRIM(RTRIM(ISNULL(m.M_URL, N''))) LIKE N'/workbench%'
             OR (LTRIM(RTRIM(ISNULL(m.M_URL, N''))) = N''
                 AND NULLIF(LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))), N'') IS NOT NULL)
               THEN N'WORKBENCH'
           WHEN NULLIF(LTRIM(RTRIM(ISNULL(m.M_URL, N''))), N'') IS NULL
               THEN N'DIRECTORY'
           ELSE N'CUSTOMPAGE'
       END AS NODE_KIND
FROM dbo.MODULES AS m;
GO

-- ② 非工作台节点的工作台专属配置落回默认值 ---------------------------------------
DECLARE @targets TABLE (M_IDX INT NOT NULL PRIMARY KEY);
INSERT INTO @targets (M_IDX)
SELECT m.M_IDX
FROM dbo.MODULES m WITH (NOLOCK)
INNER JOIN dbo.V_MODULE_NODE n ON n.M_IDX = m.M_IDX
WHERE n.NODE_KIND <> N'WORKBENCH';

DECLARE @scope NVARCHAR(MAX) = N'';
SELECT @scope = CONCAT(
        N'    待清节点 ', CONVERT(nvarchar(10), COUNT(*)), N' 行；其中 IF_COPY=1 ',
        CONVERT(nvarchar(10), SUM(CASE WHEN ISNULL(m.IF_COPY, 0) = 1 THEN 1 ELSE 0 END)),
        N'、AUTO_APPROVE=1 ', CONVERT(nvarchar(10), SUM(CASE WHEN ISNULL(m.AUTO_APPROVE, 0) = 1 THEN 1 ELSE 0 END)),
        N'、EFFECT_ENGINE_TAG=1 ', CONVERT(nvarchar(10), SUM(CASE WHEN ISNULL(m.EFFECT_ENGINE_TAG, 0) = 1 THEN 1 ELSE 0 END)),
        N'、有分组表达式 ', CONVERT(nvarchar(10), SUM(CASE WHEN NULLIF(LTRIM(RTRIM(ISNULL(m.GROUP_EXP1, N''))), N'') IS NOT NULL
                                                            OR NULLIF(LTRIM(RTRIM(ISNULL(m.GROUP_EXP2, N''))), N'') IS NOT NULL
                                                            OR NULLIF(LTRIM(RTRIM(ISNULL(m.GROUP_EXP3, N''))), N'') IS NOT NULL
                                                            OR NULLIF(LTRIM(RTRIM(ISNULL(m.GROUP_EXP4, N''))), N'') IS NOT NULL
                                                            OR NULLIF(LTRIM(RTRIM(ISNULL(m.GROUP_EXP5, N''))), N'') IS NOT NULL THEN 1 ELSE 0 END)),
        N'、有表单打开方式 ', CONVERT(nvarchar(10), SUM(CASE WHEN NULLIF(LTRIM(RTRIM(ISNULL(m.FORM_OPEN_MODE, N''))), N'') IS NOT NULL THEN 1 ELSE 0 END)))
FROM dbo.MODULES m WITH (NOLOCK)
INNER JOIN dbo.V_MODULE_NODE n ON n.M_IDX = m.M_IDX
WHERE n.NODE_KIND <> N'WORKBENCH';

UPDATE m
SET m.SORT_FIELDS = NULL,
    m.NOT_BACK_FIELDS = NULL,
    m.NOT_BACK_FIELDS_M = NULL,
    m.DETAIL_NO_FIELDS = NULL,
    m.AUTO_APPROVE = 0,
    m.IF_COPY = 0,
    m.ERROR_NO_SAVE = 0,
    m.DETAIL_NO_SAVE = 0,
    m.EFFECT_ENGINE_TAG = 0,
    m.GROUP1 = 0, m.GROUP_EXP1 = NULL, m.GROUP_DESC1 = NULL,
    m.GROUP2 = 0, m.GROUP_EXP2 = NULL, m.GROUP_DESC2 = NULL,
    m.GROUP3 = 0, m.GROUP_EXP3 = NULL, m.GROUP_DESC3 = NULL,
    m.GROUP4 = 0, m.GROUP_EXP4 = NULL, m.GROUP_DESC4 = NULL,
    m.GROUP5 = 0, m.GROUP_EXP5 = NULL, m.GROUP_DESC5 = NULL,
    -- 表单呈现配置（打开方式 / 弹窗宽高）只对统一表单有意义，由表单设计器与版式同一笔保存
    m.FORM_OPEN_MODE = NULL,
    m.FORM_DIALOG_WIDTH = NULL,
    m.FORM_DIALOG_HEIGHT = NULL
FROM dbo.MODULES m
INNER JOIN @targets t ON t.M_IDX = m.M_IDX;
DECLARE @reset INT = @@ROWCOUNT;

-- ③ 脏标记：非工作台节点不会装配工作台定义，发布门也会直接拒绝它们 ---------------
-- 留着只会在"待发布"清单里挂上永远发布不出来的条目（工作台定义发布门会直接拒绝）。
DELETE d
FROM dbo.WORKBENCH_MODULE_DIRTY d
INNER JOIN dbo.MODULES m ON m.M_IDX = d.M_IDX
INNER JOIN dbo.V_MODULE_NODE n ON n.M_IDX = m.M_IDX
WHERE n.NODE_KIND <> N'WORKBENCH';
DECLARE @dirtyNonWorkbench INT = @@ROWCOUNT;

-- ④ 脏标记：指向已不存在模块的孤儿行 -------------------------------------------
-- 成因是模块编号变更级联（`ChangeModuleIndexSql`）不覆盖这张表——它没有外键，
-- 随编号走的只有带 `ON UPDATE CASCADE` 的引用表；保存路径随后又按旧编号补标了一笔。
-- 保存路径已改为"编号变更后撤掉旧编号的行"，这里清掉历史留下的孤儿。
DELETE d
FROM dbo.WORKBENCH_MODULE_DIRTY d
WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m WITH (NOLOCK) WHERE m.M_IDX = d.M_IDX);
DECLARE @dirtyOrphan INT = @@ROWCOUNT;

-- ⑤ 历史快照：非工作台节点只留当前版本，非当前版本只会让"版本历史"列出一串断掉的记录 -----
DELETE s
FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT s
INNER JOIN dbo.MODULES m ON m.M_IDX = s.M_IDX
INNER JOIN dbo.V_MODULE_NODE n ON n.M_IDX = m.M_IDX
WHERE n.NODE_KIND <> N'WORKBENCH' AND s.IS_CURRENT = 0;
DECLARE @snapshotHistory INT = @@ROWCOUNT;

-- ⑥ M_URL 非法形态：取值改成解析器实际给出的落点 --------------------------------
-- 存的是 `/legacy/modules/<id>`，既不在精确路径白名单、也不是参数化承载页，运行时**已经**
-- 落占位页 `/fallback/modules/<id>`；但保存校验（`IsValidHostUrl`）会拒收这个值，
-- 于是这个节点在菜单管理里怎么都存不下去（改名/启停/排序走的是另外的端点，所以此前没暴露）。
-- 改写成解析器实际给出的落点：用户看到的行为逐字不变，取值变成合法形态（占位页形态在白名单内）。
UPDATE dbo.MODULES
SET M_URL = N'/fallback/modules/' + CONVERT(nvarchar(20), M_IDX)
WHERE M_URL LIKE N'/legacy/modules/%';
DECLARE @badUrl INT = @@ROWCOUNT;

-- ⑦ 后置自证：形状与标记都必须已经干净 ------------------------------------------
-- 上一段 UPDATE 是**无条件**覆盖 @targets 全体的，所以这里可以直接用"列是否非 NULL"判定，
-- 不需要再复述一遍 NULLIF/LTRIM 的归一化口径。
IF EXISTS (
        SELECT 1
        FROM dbo.MODULES m WITH (NOLOCK)
        INNER JOIN dbo.V_MODULE_NODE n ON n.M_IDX = m.M_IDX
        WHERE n.NODE_KIND <> N'WORKBENCH'
          AND (m.SORT_FIELDS IS NOT NULL
            OR m.NOT_BACK_FIELDS IS NOT NULL
            OR m.NOT_BACK_FIELDS_M IS NOT NULL
            OR m.DETAIL_NO_FIELDS IS NOT NULL
            OR m.GROUP_EXP1 IS NOT NULL OR m.GROUP_EXP2 IS NOT NULL OR m.GROUP_EXP3 IS NOT NULL
            OR m.GROUP_EXP4 IS NOT NULL OR m.GROUP_EXP5 IS NOT NULL
            OR m.GROUP_DESC1 IS NOT NULL OR m.GROUP_DESC2 IS NOT NULL OR m.GROUP_DESC3 IS NOT NULL
            OR m.GROUP_DESC4 IS NOT NULL OR m.GROUP_DESC5 IS NOT NULL
            OR m.FORM_OPEN_MODE IS NOT NULL
            OR m.FORM_DIALOG_WIDTH IS NOT NULL
            OR m.FORM_DIALOG_HEIGHT IS NOT NULL
            OR ISNULL(m.AUTO_APPROVE, 0) <> 0
            OR ISNULL(m.IF_COPY, 0) <> 0
            OR ISNULL(m.ERROR_NO_SAVE, 0) <> 0
            OR ISNULL(m.DETAIL_NO_SAVE, 0) <> 0
            OR ISNULL(m.EFFECT_ENGINE_TAG, 0) <> 0
            OR ISNULL(m.GROUP1, 0) <> 0 OR ISNULL(m.GROUP2, 0) <> 0 OR ISNULL(m.GROUP3, 0) <> 0
            OR ISNULL(m.GROUP4, 0) <> 0 OR ISNULL(m.GROUP5, 0) <> 0))
    THROW 53802, N'后置不成立：非工作台节点仍挂着工作台专属配置。', 1;

IF EXISTS (
        SELECT 1
        FROM dbo.WORKBENCH_MODULE_DIRTY d WITH (NOLOCK)
        LEFT JOIN dbo.V_MODULE_NODE n ON n.M_IDX = d.M_IDX
        WHERE n.NODE_KIND IS NULL OR n.NODE_KIND <> N'WORKBENCH')
    THROW 53803, N'后置不成立：脏标记表里仍有非工作台模块或已不存在编号的行。', 1;

PRINT N'== 菜单节点形态收口（决策：形态不落列，收口到 dbo.V_MODULE_NODE）=='
    + CHAR(10) + @scope
    + CHAR(10) + N'    清工作台专属配置：' + CONVERT(nvarchar(10), @reset) + N' 行'
    + CHAR(10) + N'    清非工作台脏标记：' + CONVERT(nvarchar(10), @dirtyNonWorkbench) + N' 行'
    + CHAR(10) + N'    清孤儿脏标记（指向已不存在编号）：' + CONVERT(nvarchar(10), @dirtyOrphan) + N' 行'
    + CHAR(10) + N'    清非工作台的非当前快照：' + CONVERT(nvarchar(10), @snapshotHistory) + N' 行'
    + CHAR(10) + N'    修正非法 M_URL（/legacy/modules/* -> /fallback/modules/*）：' + CONVERT(nvarchar(10), @badUrl) + N' 行'
    + CHAR(10) + N'    刻意未动：MASTER_TABLE / DETAIL_TABLE（报表数据集与选择器锚点）、'
    + N'FILTER（报表与选择器的实时数据范围）、SEARCH_1/2（搜索中心可达面）';
