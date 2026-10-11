-- ============================================================================
-- EOS.ERP migration 345: 批核入口声明（MODULES.APPROVE_ENABLED）
-- ----------------------------------------------------------------------------
-- 背景（ADR-035 §1）：批核入口的判据把"实现方式"混了进来——NeedsApproveColumn 取
-- 「自动批核 / 效果引擎接管 / 有流程 / 有批核动作」四者并集，而"无副作用批核"被写成
-- 「引擎和流程都没有」的兜底。实测后果：今天显示批核入口的 170 个模块里，有 12 个
-- **只靠**"效果引擎"那个开关撑入口——其中 1401 客户基本资料、1601 厂商基本资料在
-- 2026-10-05 还有人批核（各 133 / 263 行已批核），1201 产品/料件有 1318 行已批核。
-- 谁把那个开关取消勾选再发布，它们的批核按钮就无声消失。
--
-- 本迁移只做"先立"（ADR-035 §8 批 1）：加列 + 字段元数据 + 把今天有入口的模块置 1。
-- 判据切换（代码，批 2）、引擎判定改推导（批 3）、旧列 EFFECT_ENGINE_TAG 退役（批 4）
-- 都不在本迁移里——批 2 起才读这一列，所以本迁移可独立执行、也可独立回退。
--
-- 值口径：**一视同仁**——今天有入口的模块全部置 1，不为"独木桥"的那 12 个开特例
-- （ADR-035 §5）。判据写成可机械复算的表达式，而不是手抄一份名单。
--
-- 幂等：列不存在才加；字段元数据走 MERGE；置位只做 0→1（不重算、不清零），
-- 重复执行为空操作。断言用 THROW fail-closed——DbUp 每脚本一个事务，失败即整支回滚。
-- ============================================================================
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ---------- 1. 加列（幂等） ---------- */
IF COL_LENGTH(N'dbo.MODULES', N'APPROVE_ENABLED') IS NULL
BEGIN
    ALTER TABLE dbo.MODULES ADD APPROVE_ENABLED BIT NOT NULL CONSTRAINT DF_MODULES_APPROVE_ENABLED DEFAULT (0);
    PRINT N'== 已新增 MODULES.APPROVE_ENABLED（bit NOT NULL DEFAULT 0） ==';
END
ELSE
    PRINT N'== MODULES.APPROVE_ENABLED 已存在（重复执行），跳过加列 ==';
GO

/* ---------- 2. 字段元数据：照同位的 AUTO_APPROVE（是否自动批核）登记 ---------- */
-- 第 1 批单独成批（加列后的新列不能在**同一批**里被静态引用——SQL Server 在批编译期就解析列名）；
-- 加列本身幂等且无副作用，故由第 2 批起的显式事务覆盖"字段元数据 + 置位 + 收口断言"这一整段。
BEGIN TRANSACTION;

DECLARE @fields TABLE (
    F_ID NVARCHAR(60), F_DESC NVARCHAR(200), F_TYPE NVARCHAR(20), IS_PK BIT, IS_AUTOINC BIT,
    IS_VISIBLE BIT, IS_QUERY BIT, IS_DEFAULT_FIELDS BIT, IS_READONLY BIT, IS_VERIFY BIT,
    VERIFY_INDEX INT, DISPLAY_LENGTH INT, HEADER_ALIGN NVARCHAR(20), ITEM_ALIGN NVARCHAR(20), F_REMARK NVARCHAR(400));

INSERT INTO @fields (F_ID, F_DESC, F_TYPE, IS_PK, IS_AUTOINC, IS_VISIBLE, IS_QUERY, IS_DEFAULT_FIELDS,
                     IS_READONLY, IS_VERIFY, VERIFY_INDEX, DISPLAY_LENGTH, HEADER_ALIGN, ITEM_ALIGN, F_REMARK)
VALUES (N'APPROVE_ENABLED', N'启用批核', N'bit', 0, 0, 1, 1, 1, 0, 0, 0, 100, N'', N'',
        N'模块是否要有批核入口（ADR-035）：勾选时校验主表 CONFIRM_TAG/CONFIRM_PERSON/CONFIRM_DATE 三列；自动批核隐含启用批核');

MERGE dbo.FIELDS AS target
USING (SELECT * FROM @fields) AS source
   ON target.T_ID = N'MODULES' AND target.F_ID = source.F_ID
WHEN MATCHED THEN UPDATE SET
    F_DESC = source.F_DESC, F_TYPE = source.F_TYPE, IS_PK = source.IS_PK, IS_AUTOINC = source.IS_AUTOINC,
    IS_VISIBLE = source.IS_VISIBLE, IS_QUERY = source.IS_QUERY, IS_DEFAULT_FIELDS = source.IS_DEFAULT_FIELDS,
    IS_READONLY = source.IS_READONLY, IS_VERIFY = source.IS_VERIFY, VERIFY_INDEX = source.VERIFY_INDEX,
    DISPLAY_LENGTH = source.DISPLAY_LENGTH, HEADER_ALIGN = source.HEADER_ALIGN, ITEM_ALIGN = source.ITEM_ALIGN,
    F_REMARK = source.F_REMARK, LAST_UPDATE_BY = N'migration-345', LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT
    (T_ID, F_ID, F_DESC, F_TYPE, IS_PK, IS_AUTOINC, IS_VISIBLE, IS_QUERY, IS_DEFAULT_FIELDS, IS_READONLY,
     IS_VERIFY, VERIFY_INDEX, DISPLAY_LENGTH, HEADER_ALIGN, ITEM_ALIGN, F_REMARK, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (N'MODULES', source.F_ID, source.F_DESC, source.F_TYPE, source.IS_PK, source.IS_AUTOINC,
            source.IS_VISIBLE, source.IS_QUERY, source.IS_DEFAULT_FIELDS, source.IS_READONLY,
            source.IS_VERIFY, source.VERIFY_INDEX, source.DISPLAY_LENGTH, source.HEADER_ALIGN, source.ITEM_ALIGN,
            source.F_REMARK, N'migration-345', SYSDATETIME());

PRINT N'== 已登记 MODULES.APPROVE_ENABLED 字段元数据 ==';
GO

/* ---------- 3. 值：今天显示批核入口的模块（一视同仁） ---------- */
DECLARE @entry TABLE (M_IDX INT PRIMARY KEY);
INSERT INTO @entry (M_IDX)
SELECT m.M_IDX
FROM dbo.MODULES m
WHERE NULLIF(LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))), N'') IS NOT NULL
  AND (
        ISNULL(m.AUTO_APPROVE, 0) = 1
     OR ISNULL(m.EFFECT_ENGINE_TAG, 0) = 1
     OR EXISTS (SELECT 1 FROM dbo.WFFORM w WHERE w.WF_M_IDX = m.M_IDX)
     OR EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a
                WHERE a.M_IDX = m.M_IDX AND a.EVENT_CODE IN (N'APPROVE_EFFECT', N'DEAPPROVE')
                  AND ISNULL(a.ENABLED, 1) = 1));

UPDATE m SET APPROVE_ENABLED = 1
FROM dbo.MODULES m INNER JOIN @entry e ON e.M_IDX = m.M_IDX
WHERE ISNULL(m.APPROVE_ENABLED, 0) = 0;
PRINT N'== 本次置位 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 个模块 ==';

/* ---------- 4. 收口自证（三条，任一不成立即整支回滚） ---------- */

/* 4.1 置位集合必须与"今天显示批核入口"的模块集合**逐字相同** */
DECLARE @entryCnt INT = (SELECT COUNT(*) FROM @entry);
DECLARE @set     INT = (SELECT COUNT(*) FROM dbo.MODULES WHERE ISNULL(APPROVE_ENABLED, 0) = 1);
DECLARE @extra   INT = (SELECT COUNT(*) FROM dbo.MODULES m
                        WHERE ISNULL(m.APPROVE_ENABLED, 0) = 1
                          AND NOT EXISTS (SELECT 1 FROM @entry e WHERE e.M_IDX = m.M_IDX));
DECLARE @missing INT = (SELECT COUNT(*) FROM @entry e
                        WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m
                                          WHERE m.M_IDX = e.M_IDX AND ISNULL(m.APPROVE_ENABLED, 0) = 1));
PRINT N'== 置位 ' + CONVERT(NVARCHAR(10), @set) + N' 个（入口集合 ' + CONVERT(NVARCHAR(10), @entryCnt)
      + N' 个）；集合外被置位 ' + CONVERT(NVARCHAR(10), @extra) + N' 个；集合内未置位 ' + CONVERT(NVARCHAR(10), @missing) + N' 个 ==';
IF @extra > 0 OR @missing > 0
    THROW 54110, N'APPROVE_ENABLED 的置位集合必须与"今天显示批核入口"的模块集合逐字相同（ADR-035 §8 批 1）。', 1;

/* 4.2 那 12 个"只靠 EFFECT_ENGINE_TAG 撑入口"的模块必须在内——具名断言，不用模糊条件 */
DECLARE @bridge TABLE (M_IDX INT PRIMARY KEY);
INSERT INTO @bridge (M_IDX) VALUES
    (1201), (1401), (1601), (1204), (3014), (300303), (110309), (180208), (180301), (180309), (1803091), (180502);
DECLARE @bridgeMissing INT = (SELECT COUNT(*) FROM @bridge b
                              WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m
                                                WHERE m.M_IDX = b.M_IDX AND ISNULL(m.APPROVE_ENABLED, 0) = 1));
IF @bridgeMissing > 0
    THROW 54111, N'存在"只靠 EFFECT_ENGINE_TAG 撑批核入口"的模块未被置位：撤掉该开关后它们会失去批核入口（ADR-035 §1）。', 1;

/* 4.3 置位者的主表必须齐批核三列（勾选校验的前置，与发布门 lifecycle_columns 同源口径） */
DECLARE @offenders NVARCHAR(400) = N'';
SELECT TOP 8 @offenders = @offenders + CONCAT(m.M_IDX, N'(', LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))), N') ')
FROM dbo.MODULES m
WHERE ISNULL(m.APPROVE_ENABLED, 0) = 1
  AND ( COL_LENGTH(N'dbo.' + LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))), N'CONFIRM_TAG') IS NULL
     OR COL_LENGTH(N'dbo.' + LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))), N'CONFIRM_PERSON') IS NULL
     OR COL_LENGTH(N'dbo.' + LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))), N'CONFIRM_DATE') IS NULL );
IF @offenders <> N''
BEGIN
    PRINT N'== 缺批核三列却已启用批核：' + @offenders + N' ==';
    THROW 54112, N'启用批核的模块其主表必须同时有 CONFIRM_TAG / CONFIRM_PERSON / CONFIRM_DATE 三列（缺列走迁移补列，不设一键补列）。', 1;
END

PRINT N'== migration 345 收口：批核入口声明就位；代码侧尚未切换（批 2 起才读这一列） ==';

COMMIT TRANSACTION;
