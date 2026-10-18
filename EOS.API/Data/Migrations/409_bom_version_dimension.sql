-- ============================================================================
-- EOS.ERP migration 409: 模块 1204 产品BOM 引入版本维度（ADR-039 D4）
-- ----------------------------------------------------------------------------
-- 目标：一个产品（PRO_NO）可以有多个版次的 BOM 并存，主/明细都按版次隔离。
--
-- 结构变更：
--   BOM_STRU_M  主键 PK_BOMIA：PRO_NO              → (PRO_NO, EDITION)
--   BOM_STRU_D  主键 PK_BOMIB：PRO_NO, SERIAL_NO    → (PRO_NO, EDITION, SERIAL_NO)
--   两表 EDITION 由 nchar(10) NULL 改为 NOT NULL（先回填，再加约束）
--   主表 EDITION 加 DEFAULT ('01')：字段在界面上只读，新增记录由库默认值兜底
--   新增反查索引 IX_BOM_STRU_D_ELEMENT (ELEMENT_PRO_NO, PRO_NO, EDITION)
--
-- 为什么是复合主键而不是代理键：见 ADR-039 第 5 节。要点是工作台命令处理器要求
-- 明细表单定义里存在与主表主键同名的列，而明细字段的补齐只认 IS_VISIBLE=1 的
-- FIELDS 行——代理键会让明细必须在界面上暴露一列自增 ID。复合主键用的两列
-- 都是明细上已有的业务列，零新增字段。
--
-- 为什么版次只读：主键是记录身份，改它等于换一条记录；而明细是按"URL 里的旧键值"
-- 增删的，改主表版次会让旧版次明细挂在一个已不存在的父键下（孤儿）。界面锁死 + 新增由
-- 默认值兜底，是当前不改共享写入管线的前提下唯一安全的口径。换版走"新建版本"动作
-- （复制明细到新版次），随定制页一起落。
--
-- 为什么版次是两位数字：让 MAX(EDITION) 的字典序与数值序一致，"V9 > V10"的问题
-- 从根上消失，不必再加数值列或"取最新"的排序字段。起始值 01 与 sample-edition-bump
-- （样件批核版次递增）的口径一致：空 → '01'，否则两位补零递增。
--
-- 为何不加 STATUS / 生效日期：当前没有"按日期自动切换版本"的需求；加了它，每次取版本
-- 都要判区间，且一旦同一日期有两个版本生效就要再加互斥约束。取版本规则只有一条：
-- 按调用方指定的版次，缺省取最大版次。
--
-- 数据现状（实施前实测）：主表 185 行 / 明细 1084 行；孤儿明细 0、多版次混存 0、
-- 重复子件 0、底数 0 / 用量负数 / 损耗率越界 均为 0；版次列全部为空。
-- 故本迁移不需要拆分记录，一一对应即可。
--
-- 幂等：回填按"仍为空"判断；列改 NOT NULL 可重复执行；约束按存在性判断。
-- 收口断言在提交前校验结构、约束、元数据与索引均已到位。
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
    THROW 57500, @GUARD_MESSAGE, 1;

DECLARE @ModuleId INT = 1204;
DECLARE @Mig NVARCHAR(100) = N'DbUp';

/* ---------------------------------------------------------------- 前置：形态与对象 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m
               WHERE m.M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) = N'BOM_STRU_M'
                 AND LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, N''))) = N'BOM_STRU_D')
    THROW 57501, N'模块 1204 形态不符（应为 BOM_STRU_M / BOM_STRU_D），迁移中止。', 1;

IF OBJECT_ID(N'dbo.BOM_STRU_M', N'U') IS NULL OR OBJECT_ID(N'dbo.BOM_STRU_D', N'U') IS NULL
    THROW 57502, N'BOM_STRU_M / BOM_STRU_D 不存在，迁移中止。', 1;

/* 多版次混存的判定：同一料号下明细出现多个不同版次，说明这批数据已经在"用明细行
   冒充版本"，本迁移的一一对应前提不再成立，必须先人工裁决。实测为 0，断言留着防他库。 */
IF EXISTS (SELECT 1 FROM dbo.BOM_STRU_D
           GROUP BY LTRIM(RTRIM(PRO_NO))
           HAVING COUNT(DISTINCT ISNULL(LTRIM(RTRIM(EDITION)), N'')) > 1)
    THROW 57503, N'BOM_STRU_D 存在同一料号下多版次混存，无法一一对应迁移，先人工裁决，中止。', 1;

/* 孤儿明细：PRO_NO 在主表没有对应行。迁移后它们会挂不上父键。 */
IF EXISTS (SELECT 1 FROM dbo.BOM_STRU_D d
           WHERE NOT EXISTS (SELECT 1 FROM dbo.BOM_STRU_M m
                             WHERE LTRIM(RTRIM(m.PRO_NO)) = LTRIM(RTRIM(d.PRO_NO))))
    THROW 57504, N'BOM_STRU_D 存在孤儿明细（父件料号在主表不存在），先清理，中止。', 1;

BEGIN TRANSACTION;

/* ---------------------------------------------------------------- ① 回填版次 */
UPDATE dbo.BOM_STRU_M
   SET EDITION = N'01'
 WHERE EDITION IS NULL OR LTRIM(RTRIM(EDITION)) = N'';
PRINT N'[409] BOM_STRU_M 版次回填 01：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

UPDATE dbo.BOM_STRU_D
   SET EDITION = N'01'
 WHERE EDITION IS NULL OR LTRIM(RTRIM(EDITION)) = N'';
PRINT N'[409] BOM_STRU_D 版次回填 01：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

/* ---------------------------------------------------------------- ② 版次列改 NOT NULL */
ALTER TABLE dbo.BOM_STRU_M ALTER COLUMN EDITION nchar(10) NOT NULL;
ALTER TABLE dbo.BOM_STRU_D ALTER COLUMN EDITION nchar(10) NOT NULL;
PRINT N'[409] 两表 EDITION 已改为 NOT NULL';

/* 主表默认版次：字段在界面上只读，新增记录的版次由库默认值兜底，不依赖前端提交 */
IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
               WHERE name = N'DF_BOM_STRU_M_EDITION' AND parent_object_id = OBJECT_ID(N'dbo.BOM_STRU_M'))
BEGIN
    ALTER TABLE dbo.BOM_STRU_M ADD CONSTRAINT DF_BOM_STRU_M_EDITION DEFAULT (N'01') FOR EDITION;
    PRINT N'[409] 已加主表版次默认值约束 DF_BOM_STRU_M_EDITION';
END

/* ---------------------------------------------------------------- ③ 换主键 */
IF EXISTS (SELECT 1 FROM sys.key_constraints
           WHERE name = N'PK_BOMIA' AND parent_object_id = OBJECT_ID(N'dbo.BOM_STRU_M')
             AND (SELECT COUNT(*) FROM sys.index_columns
                  WHERE object_id = OBJECT_ID(N'dbo.BOM_STRU_M')
                    AND index_id = (SELECT unique_index_id FROM sys.key_constraints
                                    WHERE name = N'PK_BOMIA' AND parent_object_id = OBJECT_ID(N'dbo.BOM_STRU_M'))) < 2)
BEGIN
    ALTER TABLE dbo.BOM_STRU_M DROP CONSTRAINT PK_BOMIA;
    ALTER TABLE dbo.BOM_STRU_M ADD CONSTRAINT PK_BOMIA PRIMARY KEY CLUSTERED (PRO_NO ASC, EDITION ASC);
    PRINT N'[409] PK_BOMIA 已改为 (PRO_NO, EDITION)';
END
ELSE
    PRINT N'[409] PK_BOMIA 已是复合主键，跳过';

IF EXISTS (SELECT 1 FROM sys.key_constraints
           WHERE name = N'PK_BOMIB' AND parent_object_id = OBJECT_ID(N'dbo.BOM_STRU_D')
             AND (SELECT COUNT(*) FROM sys.index_columns
                  WHERE object_id = OBJECT_ID(N'dbo.BOM_STRU_D')
                    AND index_id = (SELECT unique_index_id FROM sys.key_constraints
                                    WHERE name = N'PK_BOMIB' AND parent_object_id = OBJECT_ID(N'dbo.BOM_STRU_D'))) < 3)
BEGIN
    ALTER TABLE dbo.BOM_STRU_D DROP CONSTRAINT PK_BOMIB;
    ALTER TABLE dbo.BOM_STRU_D ADD CONSTRAINT PK_BOMIB PRIMARY KEY CLUSTERED (PRO_NO ASC, EDITION ASC, SERIAL_NO ASC);
    PRINT N'[409] PK_BOMIB 已改为 (PRO_NO, EDITION, SERIAL_NO)';
END
ELSE
    PRINT N'[409] PK_BOMIB 已是三列复合主键，跳过';

/* ---------------------------------------------------------------- ④ 反查用途索引 */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_BOM_STRU_D_ELEMENT' AND object_id = OBJECT_ID(N'dbo.BOM_STRU_D'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_BOM_STRU_D_ELEMENT
        ON dbo.BOM_STRU_D (ELEMENT_PRO_NO ASC, PRO_NO ASC, EDITION ASC);
    PRINT N'[409] 已建反查索引 IX_BOM_STRU_D_ELEMENT';
END

/* ---------------------------------------------------------------- ⑤ 元数据：版次只读 + 两位数字格式 */
UPDATE dbo.FIELDS
   SET IS_READONLY = 1,
       IS_VERIFY = 0,
       REGEX = N'^\d{2}$',
       LAST_UPDATE_BY = @Mig,
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE T_ID = N'BOM_STRU_M' AND F_ID = N'EDITION'
   AND (ISNULL(IS_READONLY, 0) = 0 OR ISNULL(REGEX, N'') <> N'^\d{2}$');
PRINT N'[409] BOM_STRU_M.EDITION 元数据（只读 + ^\d{2}$）：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

UPDATE dbo.FIELDS
   SET IS_READONLY = 1,
       IS_VERIFY = 0,
       REGEX = N'^\d{2}$',
       LAST_UPDATE_BY = @Mig,
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE T_ID = N'BOM_STRU_D' AND F_ID = N'EDITION'
   AND (ISNULL(IS_READONLY, 0) = 0 OR ISNULL(REGEX, N'') <> N'^\d{2}$');
PRINT N'[409] BOM_STRU_D.EDITION 元数据（只读 + ^\d{2}$）：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

/* 主表版次字段的说明改准确：它不再是一个"可填的版次"，而是记录身份 */
UPDATE dbo.FIELDS
   SET F_REMARK = N'BOM 版次（两位数字，01 起）。建单时由默认值或"新建版本"动作产生，界面只读；换版请用新建版本动作复制一条记录。',
       LAST_UPDATE_BY = @Mig,
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE T_ID = N'BOM_STRU_M' AND F_ID = N'EDITION';
UPDATE dbo.FIELDS
   SET F_REMARK = N'随主表版次，由服务端按主键回填，界面只读。',
       LAST_UPDATE_BY = @Mig,
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE T_ID = N'BOM_STRU_D' AND F_ID = N'EDITION';

/* ---------------------------------------------------------------- ⑥ 模块备注与标脏 */
UPDATE dbo.MODULES
   SET REMARK = N'主表：BOM_STRU_M；明细表：BOM_STRU_D；主键 (PRO_NO, EDITION)，一个料号可有多版 BOM；保存期四条校验与写动作由效果引擎承载',
       LAST_UPDATE_BY = @Mig,
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE M_IDX = @ModuleId;
PRINT N'[409] MODULES.REMARK 已改写为多版次口径';

MERGE dbo.WORKBENCH_MODULE_DIRTY AS T
USING (SELECT @ModuleId AS M_IDX) AS S
   ON T.M_IDX = S.M_IDX
WHEN MATCHED THEN
    UPDATE SET T.DIRTY_TAG = 1, T.LAST_MODIFIED_BY = @Mig, T.LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.M_IDX, 1, @Mig, SYSDATETIME());
PRINT N'[409] 模块 1204 已标脏（主键与字段口径变化，须重新发布）';

/* ---------------------------------------------------------------- 收口断言 */
IF (SELECT COUNT(*) FROM sys.index_columns
    WHERE object_id = OBJECT_ID(N'dbo.BOM_STRU_M')
      AND index_id = (SELECT unique_index_id FROM sys.key_constraints
                      WHERE name = N'PK_BOMIA' AND parent_object_id = OBJECT_ID(N'dbo.BOM_STRU_M'))) <> 2
    THROW 57510, N'PK_BOMIA 不是两列复合主键，迁移中止。', 1;
IF (SELECT COUNT(*) FROM sys.index_columns
    WHERE object_id = OBJECT_ID(N'dbo.BOM_STRU_D')
      AND index_id = (SELECT unique_index_id FROM sys.key_constraints
                      WHERE name = N'PK_BOMIB' AND parent_object_id = OBJECT_ID(N'dbo.BOM_STRU_D'))) <> 3
    THROW 57511, N'PK_BOMIB 不是三列复合主键，迁移中止。', 1;
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id IN (OBJECT_ID(N'dbo.BOM_STRU_M'), OBJECT_ID(N'dbo.BOM_STRU_D'))
             AND name = N'EDITION' AND is_nullable = 1)
    THROW 57512, N'EDITION 列仍可空，迁移中止。', 1;
IF EXISTS (SELECT 1 FROM dbo.BOM_STRU_M WHERE EDITION IS NULL OR LTRIM(RTRIM(EDITION)) = N''
           UNION ALL
           SELECT 1 FROM dbo.BOM_STRU_D WHERE EDITION IS NULL OR LTRIM(RTRIM(EDITION)) = N'')
    THROW 57513, N'仍存在空版次数据，迁移中止。', 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_BOM_STRU_D_ELEMENT' AND object_id = OBJECT_ID(N'dbo.BOM_STRU_D'))
    THROW 57514, N'反查索引 IX_BOM_STRU_D_ELEMENT 未建立，迁移中止。', 1;
IF EXISTS (SELECT 1 FROM dbo.FIELDS
           WHERE T_ID IN (N'BOM_STRU_M', N'BOM_STRU_D') AND F_ID = N'EDITION'
             AND (ISNULL(IS_READONLY, 0) = 0 OR ISNULL(REGEX, N'') <> N'^\d{2}$'))
    THROW 57515, N'版次字段元数据未改为只读 + 两位数字格式，迁移中止。', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX = @ModuleId AND DIRTY_TAG = 1)
    THROW 57516, N'模块 1204 未标脏，迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 1204 版本维度就位：主键 (PRO_NO, EDITION) / (PRO_NO, EDITION, SERIAL_NO)，版次两位数字只读，反查索引已建；模块已标脏待发布 ==';
