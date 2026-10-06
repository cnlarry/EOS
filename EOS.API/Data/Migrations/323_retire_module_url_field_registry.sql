-- ============================================================================
-- EOS.ERP migration 323: 字段登记里指向已删路由列的虚拟列退场
-- ----------------------------------------------------------------------------
-- 迁移 321 删掉了 MODULES 的 NEW_URL / MODI_URL / HELP_URL，但**元数据里还留着两行**把它们
-- 当虚拟列的来源：`FIELDS.VIRTUAL_EXP = 'MODULES.MODI_URL'`。虚拟列的表达式会被
-- `TableDataController` 直接当 SELECT 表达式拼进查询、也被 2302 字段维护当"受控虚拟列"展示，
-- 指着一列不存在的列 ⇒ 表数据视图必抛 `列名 'MODI_URL' 无效`，字段维护页也会列出个坏字段。
--
-- 两行（执行时会打印留痕）：
--   WF_APPROVE.MODI_URL  标签「查看明细URL」（单据批核历史表；无模块挂它，仅登记）
--   WF_MYTASK.MODI_URL   标签「查看明细URL」（模块 2102「我的任务」的主表，活表）
--
-- 为什么是**删该字段行**而不是改成 `MODULES.M_URL`：这个字段的语义是"打开这条单据的明细"，
-- 现代实现由前端按主键拼 `/workbench/{moduleId}/view/{主键}`（见 ModuleRouteValidator 的注释），
-- 元数据里那一列**表达不出来**；而 M_URL 是模块承载页（如 /my-tasks），填进去只会得到一个
-- 语义错误的链接。删掉后该列从字段登记与表数据视图里消失——比留一个编不出值的坏字段干净。
--
-- 命名全大写；**非幂等**（前提行不在即报错，与 320/321/322 同口径）。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- ① 前置自证：两条登记必须还在，且表达式确实是那条已删列
IF NOT EXISTS (SELECT 1 FROM dbo.FIELDS WITH (NOLOCK)
               WHERE T_ID = N'WF_APPROVE' AND F_ID = N'MODI_URL' AND LTRIM(RTRIM(ISNULL(VIRTUAL_EXP, N''))) = N'MODULES.MODI_URL')
    OR NOT EXISTS (SELECT 1 FROM dbo.FIELDS WITH (NOLOCK)
                   WHERE T_ID = N'WF_MYTASK' AND F_ID = N'MODI_URL' AND LTRIM(RTRIM(ISNULL(VIRTUAL_EXP, N''))) = N'MODULES.MODI_URL')
    THROW 53300, N'WF_APPROVE / WF_MYTASK 的 MODI_URL 登记不齐：本迁移的前提不成立（已处理过或结构已变）。', 1;

-- ② 留痕：删掉的是什么，写在执行日志里
DECLARE @trail NVARCHAR(MAX) = N'';
SELECT @trail = @trail + N'  ' + T_ID + N'.' + F_ID + N' = ' + VIRTUAL_EXP + N'（' + ISNULL(F_DESC, N'') + N'）' + CHAR(10)
FROM dbo.FIELDS
WHERE T_ID IN (N'WF_APPROVE', N'WF_MYTASK') AND F_ID = N'MODI_URL'
  AND LTRIM(RTRIM(ISNULL(VIRTUAL_EXP, N''))) = N'MODULES.MODI_URL';
PRINT N'== 随路由列退役的虚拟字段登记（值是编不出来的，故删行）==' + CHAR(10) + @trail;

DELETE FROM dbo.FIELDS
WHERE T_ID IN (N'WF_APPROVE', N'WF_MYTASK') AND F_ID = N'MODI_URL'
  AND LTRIM(RTRIM(ISNULL(VIRTUAL_EXP, N''))) = N'MODULES.MODI_URL';

-- ③ 选择器过滤条件里的旧判据要**翻译**（不能删：它是"只列可用模块"的语义）：
--    FIELD_DATASOURCE 的 FILTER_STRUCT（JSON）里写着
--    `{"field":"MODULES.MODI_URL","operator":"NE","value":"","nullSafe":"EMPTY"}` =
--    旧口径"模块配了修改地址 ⇒ 这个模块可用"。新口径就是"模块有承载页"（M_URL 非空）：
--    目录节点本就 M_URL 空、被排除在外，与旧行为同集。字段名逐字替换，其余 JSON 原样保留。
UPDATE dbo.FIELD_DATASOURCE
SET FILTER_STRUCT = REPLACE(FILTER_STRUCT, N'"field":"MODULES.MODI_URL"', N'"field":"MODULES.M_URL"')
WHERE FILTER_STRUCT LIKE N'%MODULES.MODI_URL%';

-- ④ 全库自证：任何元数据文本里都不得再出现 MODULES.<已删三列>
--    （不能只盯 VIRTUAL_EXP：登记表里还有 BROWSE_URL / CHOOSE_PAGE / DFT_VALUE / FORM_OPTIONS
--     等文本列，漏一处就会在表数据视图或选择器上变成运行时 500）
IF EXISTS (
    SELECT 1 FROM dbo.FIELDS
    WHERE VIRTUAL_EXP LIKE N'%MODULES.NEW_URL%' OR VIRTUAL_EXP LIKE N'%MODULES.MODI_URL%' OR VIRTUAL_EXP LIKE N'%MODULES.HELP_URL%'
       OR BROWSE_URL LIKE N'%MODULES.NEW_URL%' OR BROWSE_URL LIKE N'%MODULES.MODI_URL%' OR BROWSE_URL LIKE N'%MODULES.HELP_URL%'
       OR CHOOSE_PAGE LIKE N'%MODULES.NEW_URL%' OR CHOOSE_PAGE LIKE N'%MODULES.MODI_URL%' OR CHOOSE_PAGE LIKE N'%MODULES.HELP_URL%'
       OR DFT_VALUE LIKE N'%MODULES.NEW_URL%' OR DFT_VALUE LIKE N'%MODULES.MODI_URL%' OR DFT_VALUE LIKE N'%MODULES.HELP_URL%'
       OR FORM_OPTIONS LIKE N'%MODULES.NEW_URL%' OR FORM_OPTIONS LIKE N'%MODULES.MODI_URL%' OR FORM_OPTIONS LIKE N'%MODULES.HELP_URL%'
       OR F_REMARK LIKE N'%MODULES.NEW_URL%' OR F_REMARK LIKE N'%MODULES.MODI_URL%' OR F_REMARK LIKE N'%MODULES.HELP_URL%'
    UNION ALL
    SELECT 1 FROM dbo.FIELD_DATASOURCE
    WHERE FILTER_STRUCT LIKE N'%MODULES.NEW_URL%' OR FILTER_STRUCT LIKE N'%MODULES.MODI_URL%' OR FILTER_STRUCT LIKE N'%MODULES.HELP_URL%'
       OR RETURN_ITEMS LIKE N'%MODULES.NEW_URL%' OR RETURN_ITEMS LIKE N'%MODULES.MODI_URL%' OR RETURN_ITEMS LIKE N'%MODULES.HELP_URL%'
    UNION ALL
    SELECT 1 FROM dbo.MODULE_FORM_LAYOUT WHERE CELL_GROUP LIKE N'%MODULES.NEW_URL%' OR CELL_GROUP LIKE N'%MODULES.MODI_URL%' OR CELL_GROUP LIKE N'%MODULES.HELP_URL%'
)
    THROW 53301, N'仍有元数据文本引用已删的三条路由列：先逐条改掉（元数据里指着一列不存在的列会在运行期炸成 500）。', 1;

-- ⑤ 后置自证：两行必须消失，且别把整表的字段登记顺手删了
IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID IN (N'WF_APPROVE', N'WF_MYTASK') AND F_ID = N'MODI_URL')
    THROW 53310, N'旧路由虚拟字段登记未被删除。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = N'WF_MYTASK')
    OR NOT EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = N'WF_APPROVE')
    THROW 53311, N'不该删的字段登记被删了（WF_MYTASK / WF_APPROVE 的其它字段必须还在）。', 1;

IF EXISTS (SELECT 1 FROM dbo.FIELD_DATASOURCE WHERE FILTER_STRUCT NOT LIKE N'%MODULES.M_URL%' AND FILTER_STRUCT LIKE N'%"field":"MODULES.%_URL"%'
           AND (FILTER_STRUCT LIKE N'%"field":"MODULES.NEW_URL"%' OR FILTER_STRUCT LIKE N'%"field":"MODULES.MODI_URL"%' OR FILTER_STRUCT LIKE N'%"field":"MODULES.HELP_URL"%'))
    THROW 53312, N'选择器过滤条件里的旧模块地址列未被替换。', 1;

COMMIT TRANSACTION;

PRINT N'== 元数据同步完成：WF_APPROVE / WF_MYTASK 的「查看明细URL」登记已删、三处选择器判据改指 M_URL ==';
