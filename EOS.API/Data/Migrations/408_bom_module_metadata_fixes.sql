-- ============================================================================
-- EOS.ERP migration 408: 模块 1204（产品BOM表）元数据收口——原地修复
-- ----------------------------------------------------------------------------
-- 六项修复，均不依赖任何待定设计（见 ADR-039 第 3 节 D9）。逐项判据：
--
--   ① 料号浏览链接指向已退役模块
--      BOM_STRU_M.PRO_NO / BOM_STRU_D.PRO_NO / BOM_STRU_D.ELEMENT_PRO_NO 的
--      BROWSE_M_IDX 分别是 1206 / 1210 / 1210。这三个模块已合并进 1201 并从
--      MODULES 消失（见 ModuleBusinessMap 的 1201 注释），WorkbenchBrowseResolver
--      在目标不可达时把 BrowseModuleId 置空，料号于是退化为纯文本——能选、不能点开。
--      改为 1201（PRODUCT），并把 BROWSE_URL 的旧 WebForms 路径换成现承载页。
--      **路径段不参与解析**（ParseBrowseUrl 只取 `?` 之后的 IDX 段），但留着
--      `~/BOM/Product.aspx` 会误导后来的人，故一并改掉。
--
--   ② 删除 3 行永远渲染不出的版式
--      MODULE_FORM_LAYOUT 里 BOM_STRU_D 的 PRO_ELEMENT_PRO_NO / STUFF_ID /
--      SPECIFIC_GRAVITY 三行，对应字段的 IS_VISIBLE=0。运行态字段视图在
--      FormFieldSelector.Select 阶段就剔除不可见字段，版式施加在它之后，
--      故"只补版式行变不出字段"，反之这些行永远渲染不出。
--      删的是无效行，不是字段——三个字段本身保留（FIELDS 行不动）。
--      注：REMARK 的 IS_VISIBLE=1，其版式行不是死行，不删。
--
--   ③ ELEMENT_QTY 的正则不是"数字格式"
--      现值 `^\d+|^$` 在非锚定的 IsMatch 下只要求"以数字开头"：
--      `0.5` 过、`5abc` 也过（随后 decimal 转换才报错）、`-1` 拒、`.5` 拒。
--      字段声明的小数格式是 `0.####`，正则应与之一致。
--
--   ④ 明细行的 PRO_NO 可编辑但改了无效
--      WorkbenchCommandHandler 在保存明细时用主表键强制覆盖主键列
--      （row[pkColumns[i]] = keyValues[i]），所以明细行的父件料号无论前端提交
--      什么都写不进去。界面上它是可编辑的，用户改了会被静默丢弃。
--      改为只读：仍显示父件，但不再假装可改。
--
--   ⑤ LOST_RATE（损耗率%）没有上下界
--      它直接参与算料：ELEMENT_QTY/BASE_QTY*(1+LOST_RATE/100)。负值会减少需求、
--      超 100 会翻倍，两者都不该静默通过。加一条 line-require，逐行回报序号。
--      LOST_RATE 可空，nullSkips=true：不填不校验（沿用 159 对 BASE_QTY 的同一口径）。
--
--   ⑥ MODULES.REMARK 仍写着已退役的批核过程
--      原文"批核存储过程：P_BOM_STRU_After_Save"——该过程已在迁移 159 退役，
--      四条 SAVE 校验与一个写动作改由效果引擎承载。备注留着会让排障的人去找一个
--      不存在的过程。
--      注：TABLES.T_REMARK 里的 `129802:BOM展开表` 已由迁移 407 清理（含收口断言），
--      本脚本不重复处理。
--
-- 发布语义：②修改 MODULE_FORM_LAYOUT，属模块级事实、随定义快照冻结；本脚本末尾
--   把 1204 标脏（WORKBENCH_MODULE_DIRTY），由既有的发布动作重新生成快照。
--   不标脏会被快照落后检测判为静默落后。①③④ 走字段视图实时读取，标脏后一并固化。
--
-- 幂等：全部按 EXISTS / 目标值判断，重复执行是空操作。收口断言在提交前校验
--   六项均已落到位，口径变化时中止而不是"尽力而为"。
-- ============================================================================

SET NOCOUNT ON;

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 57400, @GUARD_MESSAGE, 1;

DECLARE @ModuleId INT = 1204;
DECLARE @Mig NVARCHAR(100) = N'DbUp';

/* ---------------------------------------------------------------- 前置：形态与字段存在性 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m
               WHERE m.M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) = N'BOM_STRU_M'
                 AND LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, N''))) = N'BOM_STRU_D')
    THROW 57401, N'模块 1204 形态不符（应为 BOM_STRU_M / BOM_STRU_D），迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = N'BOM_STRU_M' AND F_ID = N'PRO_NO')
    THROW 57402, N'字段 BOM_STRU_M.PRO_NO 不存在，迁移中止。', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = N'BOM_STRU_D' AND F_ID = N'PRO_NO')
    THROW 57403, N'字段 BOM_STRU_D.PRO_NO 不存在，迁移中止。', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = N'BOM_STRU_D' AND F_ID = N'ELEMENT_PRO_NO')
    THROW 57404, N'字段 BOM_STRU_D.ELEMENT_PRO_NO 不存在，迁移中止。', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = N'BOM_STRU_D' AND F_ID = N'ELEMENT_QTY')
    THROW 57405, N'字段 BOM_STRU_D.ELEMENT_QTY 不存在，迁移中止。', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = N'BOM_STRU_D' AND F_ID = N'LOST_RATE')
    THROW 57406, N'字段 BOM_STRU_D.LOST_RATE 不存在，迁移中止。', 1;

/* ⑤ 前置统计：库内是否已有越界损耗率。不阻塞迁移（规则生效后保存时会逐行拒绝），
   但要在部署日志里看得见，否则用户第一次保存某张 BOM 被拒时无从判断是新规则还是数据坏了。 */
IF OBJECT_ID(N'dbo.BOM_STRU_D', N'U') IS NOT NULL
BEGIN
    DECLARE @OutOfRange INT = (
        SELECT COUNT(*) FROM dbo.BOM_STRU_D
         WHERE LOST_RATE IS NOT NULL AND (LOST_RATE < 0 OR LOST_RATE > 100));
    IF @OutOfRange > 0
    BEGIN
        DECLARE @OutOfRangeMsg NVARCHAR(400) =
            N'[408] 注意：BOM_STRU_D 已有 ' + CAST(@OutOfRange AS NVARCHAR(10))
            + N' 行损耗率不在 0..100 内；新规则生效后，保存这些 BOM 会被逐行拒绝，需人工修正。';
        PRINT @OutOfRangeMsg;
    END
    ELSE
        PRINT N'[408] BOM_STRU_D 损耗率全部在 0..100 内（或为空）';
END

BEGIN TRANSACTION;

/* ---------------------------------------------------------------- ① 料号浏览链接改指 1201 */
UPDATE dbo.FIELDS
   SET BROWSE_M_IDX = 1201,
       BROWSE_URL = N'~/workbench/1201?IDX=PRO_NO={PRO_NO}',
       LAST_UPDATE_BY = @Mig,
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE T_ID = N'BOM_STRU_M' AND F_ID = N'PRO_NO'
   AND (ISNULL(BROWSE_M_IDX, 0) <> 1201
        OR ISNULL(BROWSE_URL, N'') <> N'~/workbench/1201?IDX=PRO_NO={PRO_NO}');
PRINT N'[408] BOM_STRU_M.PRO_NO 浏览链接 → 1201：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

UPDATE dbo.FIELDS
   SET BROWSE_M_IDX = 1201,
       BROWSE_URL = N'~/workbench/1201?IDX=PRO_NO={PRO_NO}',
       LAST_UPDATE_BY = @Mig,
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE T_ID = N'BOM_STRU_D' AND F_ID = N'PRO_NO'
   AND (ISNULL(BROWSE_M_IDX, 0) <> 1201
        OR ISNULL(BROWSE_URL, N'') <> N'~/workbench/1201?IDX=PRO_NO={PRO_NO}');
PRINT N'[408] BOM_STRU_D.PRO_NO 浏览链接 → 1201：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

/* 元件料号：占位符是来源列 ELEMENT_PRO_NO，解析为 目标列 PRO_NO ← 来源列 ELEMENT_PRO_NO */
UPDATE dbo.FIELDS
   SET BROWSE_M_IDX = 1201,
       BROWSE_URL = N'~/workbench/1201?IDX=PRO_NO={ELEMENT_PRO_NO}',
       LAST_UPDATE_BY = @Mig,
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE T_ID = N'BOM_STRU_D' AND F_ID = N'ELEMENT_PRO_NO'
   AND (ISNULL(BROWSE_M_IDX, 0) <> 1201
        OR ISNULL(BROWSE_URL, N'') <> N'~/workbench/1201?IDX=PRO_NO={ELEMENT_PRO_NO}');
PRINT N'[408] BOM_STRU_D.ELEMENT_PRO_NO 浏览链接 → 1201：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

/* ---------------------------------------------------------------- ② 删除不可见字段的版式行 */
DELETE FROM dbo.MODULE_FORM_LAYOUT
 WHERE M_IDX = @ModuleId
   AND T_ID = N'BOM_STRU_D'
   AND F_ID IN (N'PRO_ELEMENT_PRO_NO', N'STUFF_ID', N'SPECIFIC_GRAVITY');
PRINT N'[408] BOM_STRU_D 无效版式行删除：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

/* ---------------------------------------------------------------- ③ 组成用量的正则 */
UPDATE dbo.FIELDS
   SET REGEX = N'^\d+(\.\d+)?$',
       LAST_UPDATE_BY = @Mig,
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE T_ID = N'BOM_STRU_D' AND F_ID = N'ELEMENT_QTY'
   AND ISNULL(REGEX, N'') <> N'^\d+(\.\d+)?$';
PRINT N'[408] BOM_STRU_D.ELEMENT_QTY 正则修正：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

/* ---------------------------------------------------------------- ④ 明细侧父键改只读 */
UPDATE dbo.FIELDS
   SET IS_READONLY = 1,
       LAST_UPDATE_BY = @Mig,
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE T_ID = N'BOM_STRU_D' AND F_ID = N'PRO_NO'
   AND ISNULL(IS_READONLY, 0) = 0;
PRINT N'[408] BOM_STRU_D.PRO_NO 改只读：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

/* ---------------------------------------------------------------- ⑤ 损耗率上下界（line-require，逐行回报序号） */
DECLARE @LostRateMessage NVARCHAR(300) =
    N'以下序号项损耗率必须介于 0 与 100 之间 ' + CHAR(13) + CHAR(10) + N'{ROWS}';
/* PARAM_STRUCT 里的换行必须是转义的 \r\n（JSON 不允许裸控制字符），MESSAGE 列才用真实 CRLF */
DECLARE @LostRateParam NVARCHAR(MAX) =
    N'{"checks":[{"scope":"DETAIL","field":"LOST_RATE","assert":{"op":"GE","value":0,"nullSkips":true},'
    + N'"message":"以下序号项损耗率不能小于 0 \r\n{ROWS}","diagnosticFields":["SERIAL_NO"]},'
    + N'{"scope":"DETAIL","field":"LOST_RATE","assert":{"op":"LE","value":100,"nullSkips":true},'
    + N'"message":"以下序号项损耗率不能大于 100 \r\n{ROWS}","diagnosticFields":["SERIAL_NO"]}]}';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT @ModuleId AS M_IDX) AS S
   ON T.M_IDX = S.M_IDX AND T.STAGE = N'SAVE'
  AND T.VALIDATION_KEY = N'line-require' AND T.SEQ = 5
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @LostRateParam, T.MESSAGE = @LostRateMessage, T.ENABLED = 1,
               T.REMARK = N'BOM 明细损耗率必须在 0..100 之间（逐行回报序号；不填不校验）',
               T.LAST_UPDATE_BY = @Mig, T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (M_IDX, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.M_IDX, N'SAVE', 5, N'line-require', 1, @LostRateParam, @LostRateMessage,
            N'BOM 明细损耗率必须在 0..100 之间（逐行回报序号；不填不校验）', N'bom-stru',
            @Mig, SYSDATETIME(), @Mig, SYSDATETIME());
PRINT N'[408] 损耗率 0..100 校验规则写入（line-require seq=5）';

/* ---------------------------------------------------------------- ⑥ 模块备注去掉已退役的过程名 */
UPDATE dbo.MODULES
   SET REMARK = N'主表：BOM_STRU_M；明细表：BOM_STRU_D；保存期四条校验与写动作由效果引擎承载（原批核过程已退役）',
       LAST_UPDATE_BY = @Mig,
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE M_IDX = @ModuleId
   AND ISNULL(REMARK, N'') LIKE N'%P_BOM_STRU_After_Save%';
PRINT N'[408] MODULES.REMARK 清理退役过程名：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

/* ---------------------------------------------------------------- 标脏：版式属模块级事实，须重新发布 */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS T
USING (SELECT @ModuleId AS M_IDX) AS S
   ON T.M_IDX = S.M_IDX
WHEN MATCHED THEN
    UPDATE SET T.DIRTY_TAG = 1, T.LAST_MODIFIED_BY = @Mig, T.LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.M_IDX, 1, @Mig, SYSDATETIME());
PRINT N'[408] 模块 1204 已标脏（待发布后版式固化进快照）';

/* ---------------------------------------------------------------- 收口断言 */
IF EXISTS (SELECT 1 FROM dbo.FIELDS
           WHERE T_ID = N'BOM_STRU_M' AND F_ID = N'PRO_NO' AND ISNULL(BROWSE_M_IDX, 0) <> 1201)
    THROW 57410, N'BOM_STRU_M.PRO_NO 的浏览模块未指向 1201，迁移中止。', 1;
IF EXISTS (SELECT 1 FROM dbo.FIELDS
           WHERE T_ID = N'BOM_STRU_D' AND F_ID IN (N'PRO_NO', N'ELEMENT_PRO_NO')
             AND ISNULL(BROWSE_M_IDX, 0) <> 1201)
    THROW 57411, N'BOM_STRU_D 的料号浏览模块未指向 1201，迁移中止。', 1;
IF EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT
           WHERE M_IDX = @ModuleId AND T_ID = N'BOM_STRU_D'
             AND F_ID IN (N'PRO_ELEMENT_PRO_NO', N'STUFF_ID', N'SPECIFIC_GRAVITY'))
    THROW 57412, N'BOM_STRU_D 仍存在指向不可见字段的版式行，迁移中止。', 1;
IF EXISTS (SELECT 1 FROM dbo.FIELDS
           WHERE T_ID = N'BOM_STRU_D' AND F_ID = N'ELEMENT_QTY'
             AND ISNULL(REGEX, N'') <> N'^\d+(\.\d+)?$')
    THROW 57413, N'BOM_STRU_D.ELEMENT_QTY 的正则未修正，迁移中止。', 1;
IF EXISTS (SELECT 1 FROM dbo.FIELDS
           WHERE T_ID = N'BOM_STRU_D' AND F_ID = N'PRO_NO' AND ISNULL(IS_READONLY, 0) = 0)
    THROW 57414, N'BOM_STRU_D.PRO_NO 仍可编辑，迁移中止。', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE M_IDX = @ModuleId AND STAGE = N'SAVE'
                 AND VALIDATION_KEY = N'line-require' AND SEQ = 5 AND ENABLED = 1)
    THROW 57415, N'损耗率 0..100 的校验规则未启用，迁移中止。', 1;
IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @ModuleId AND ISNULL(REMARK, N'') LIKE N'%P_BOM_STRU_After_Save%')
    THROW 57416, N'MODULES.REMARK 仍残留已退役的过程名，迁移中止。', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX = @ModuleId AND DIRTY_TAG = 1)
    THROW 57417, N'模块 1204 未标脏，版式改动不会被发布动作固化，迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 1204 产品BOM 元数据收口完成：浏览链接改指 1201 / 3 行死版式删除 / 用量正则修正 / 明细父键只读 / 损耗率 0..100 / 备注清理；模块已标脏待发布 ==';
