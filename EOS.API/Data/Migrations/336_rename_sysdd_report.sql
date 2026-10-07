-- ============================================================================
-- EOS.ERP migration 336: SYSDD_REPORT → REPORT_USER_STATE（表名与语义对齐）
-- ----------------------------------------------------------------------------
-- 背景：这张表自 ADR-024 的 R7 起**只承载用户状态**——收藏（FAVORITE_TAG）、排序（SORT_IDX）、
--       最近使用（LAST_RUN_AT）；逐报表例外层的权限四列已随迁移 278/279 退役，
--       "报表权限"三类语义（预览/打印/导出勾选、数据范围）都不在这张表里了。
--       名字却还叫 SYSDD_REPORT（旧名"用户报表权限"），读代码的人会以为它是权限表。
--
-- 决策：用户 2026-10-08 拍板改名为 REPORT_USER_STATE（ADR-024 执行提示词 §五 待拍板 #3）。
--
-- 做法：表名 + PK + 默认约束一并改名，范式照 Migrations/020_field_datasource_rename.sql；
--       同时订正两处**旧语义残留**的元数据：
--         · TABLES.T_REMARK —— 原文的查询关系还写着 `LEFT JOIN modules ON report.r_m_idx=modules.m_idx`，
--           而 r_m_idx 早在迁移 276 就退役了（那段查询关系本来就是坏的）；改为按 REPORT.M_IDX 关联
--           （这正是 ADR-024 定下的报表归属关系）。
--         · FIELDS.M_IDX 的字段说明 —— 原文"权限ID"，改为"归属模块"。
--       扩展属性注释（MS_Description）绑在 object_id 上，改名后自动跟随，本迁移不需另改。
--
-- 安全性：改名前置自证"无外键、无视图/过程/函数引用"（迁移 279 落库时已实测为 0，
--       这里再断言一次——期间新长出来的依赖必须被拦下，否则改名会静默留下悬空引用）。
--
-- 不做：不动 db/bootstrap —— 它代表日志基线时点（纪律见 Migrations/276 头部）；新库先灌基线、
--       再由本迁移改名，终态与本库一致。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

-- ① 前置自证：起点必须成立，否则说明库已被改过或不是本迁移预期的状态
IF OBJECT_ID(N'dbo.SYSDD_REPORT', N'U') IS NULL
    THROW 53601, N'前置不成立：dbo.SYSDD_REPORT 不存在（可能已改名，或库不是本迁移的起点）。', 1;
IF OBJECT_ID(N'dbo.REPORT_USER_STATE', N'U') IS NOT NULL
    THROW 53602, N'前置不成立：dbo.REPORT_USER_STATE 已存在，改名会撞名。', 1;
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE referenced_object_id = OBJECT_ID(N'dbo.SYSDD_REPORT'))
    THROW 53603, N'有外键引用 dbo.SYSDD_REPORT：改名会断开引用，先处理。', 1;
IF EXISTS (SELECT 1 FROM sys.sql_expression_dependencies WHERE referenced_entity_name = N'SYSDD_REPORT')
    THROW 53604, N'有视图/过程/函数引用 SYSDD_REPORT：改名会留下悬空引用，先处理。', 1;

DECLARE @RowsBefore INT = (SELECT COUNT(*) FROM dbo.SYSDD_REPORT);
DECLARE @FieldsBefore INT = (SELECT COUNT(*) FROM dbo.FIELDS WHERE LTRIM(RTRIM(T_ID)) = N'SYSDD_REPORT');

BEGIN TRANSACTION;

-- ② 改名：表 + 主键约束 + 默认约束
--    约束改名的 @objtype 用 N'OBJECT'：主键按 N'INDEX' 实测会报 15248「参数 @objname 不明确或
--    所声明的 @objtype (INDEX) 有误」（干跑实测），默认约束同 N'OBJECT'。
EXEC sys.sp_rename N'dbo.SYSDD_REPORT', N'REPORT_USER_STATE';
EXEC sys.sp_rename N'dbo.PK_SYSDD_REPORT', N'PK_REPORT_USER_STATE', N'OBJECT';
EXEC sys.sp_rename N'dbo.DF_SYSDD_REPORT_FAVORITE', N'DF_REPORT_USER_STATE_FAVORITE', N'OBJECT';

-- ③ 元数据订正
--    ⚠ 顺序与外键：元数据表之间有一条 FK_FIELD_DATASOURCE_FIELDS（child = FIELD_DATASOURCE，
--    parent = FIELDS，ON UPDATE NO ACTION）。FIELDS.T_ID 一动，子行立刻悬空、被外键拦下
--    （干跑实测 Msg 547）。这里在事务内**临时禁用**它，父行与子行都改完再 WITH CHECK 复核回来——
--    禁用窗口只在事务内且立即恢复，不是长期放宽约束。
ALTER TABLE dbo.FIELD_DATASOURCE NOCHECK CONSTRAINT FK_FIELD_DATASOURCE_FIELDS;

-- ③a 字段登记（父行）
UPDATE dbo.FIELDS
   SET T_ID = N'REPORT_USER_STATE'
 WHERE LTRIM(RTRIM(T_ID)) = N'SYSDD_REPORT';

-- ③b 字段数据源登记（子行）：有 1 行把"报表编号"字段的取值源指到 REPORT 表，随表改名一起迁移
UPDATE dbo.FIELD_DATASOURCE
   SET T_ID = N'REPORT_USER_STATE'
 WHERE LTRIM(RTRIM(T_ID)) = N'SYSDD_REPORT';

-- ③c 表登记（旧表名 + 那段含已退役列 r_m_idx 的查询关系）
UPDATE dbo.TABLES
   SET T_ID = N'REPORT_USER_STATE',
       T_REMARK = N'性质：用户状态表（收藏 / 排序 / 最近使用）。查询关系：REPORT_USER_STATE WITH (NOLOCK)'
                 + N' LEFT JOIN REPORT WITH (NOLOCK) ON REPORT_USER_STATE.REPORT_ID=REPORT.REPORT_ID'
                 + N' LEFT JOIN MODULES WITH (NOLOCK) ON REPORT.M_IDX=MODULES.M_IDX'
 WHERE LTRIM(RTRIM(T_ID)) = N'SYSDD_REPORT';

-- ③d 字段说明（原文"权限ID"，该语义已随 R7 退役）
UPDATE dbo.FIELDS
   SET F_DESC = N'归属模块'
 WHERE LTRIM(RTRIM(T_ID)) = N'REPORT_USER_STATE'
   AND LTRIM(RTRIM(F_ID)) = N'M_IDX';

-- ③e 外键复核回来（WITH CHECK 会校验现有数据，失败即整段回滚）
ALTER TABLE dbo.FIELD_DATASOURCE WITH CHECK CHECK CONSTRAINT FK_FIELD_DATASOURCE_FIELDS;

COMMIT TRANSACTION;

-- ④ 后置自证：旧名与新语义残留必须消失，新名与数据必须就位
IF OBJECT_ID(N'dbo.SYSDD_REPORT', N'U') IS NOT NULL
    THROW 53610, N'后置失败：旧表名 dbo.SYSDD_REPORT 仍在。', 1;
IF OBJECT_ID(N'dbo.REPORT_USER_STATE', N'U') IS NULL
    THROW 53611, N'后置失败：新表名 dbo.REPORT_USER_STATE 未就位。', 1;
IF (SELECT COUNT(*) FROM dbo.REPORT_USER_STATE) <> @RowsBefore
    THROW 53612, N'后置失败：改名前后行数不一致（数据被改动）。', 1;
IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = N'PK_SYSDD_REPORT')
    THROW 53613, N'后置失败：主键约束名仍是旧名 PK_SYSDD_REPORT。', 1;
IF NOT EXISTS (SELECT 1
               FROM sys.key_constraints kc
               JOIN sys.objects o ON o.object_id = kc.parent_object_id
               WHERE o.name = N'REPORT_USER_STATE' AND kc.name = N'PK_REPORT_USER_STATE' AND kc.type = N'PK')
    THROW 53614, N'后置失败：主键约束未改为 PK_REPORT_USER_STATE。', 1;
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = N'DF_SYSDD_REPORT_FAVORITE')
    THROW 53615, N'后置失败：默认约束名仍是旧名 DF_SYSDD_REPORT_FAVORITE。', 1;
IF EXISTS (SELECT 1 FROM dbo.TABLES WHERE LTRIM(RTRIM(T_ID)) = N'SYSDD_REPORT')
    THROW 53616, N'后置失败：TABLES 仍在登记旧表名 SYSDD_REPORT。', 1;
IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE LTRIM(RTRIM(T_ID)) = N'SYSDD_REPORT')
    THROW 53617, N'后置失败：FIELDS 仍有登记挂在旧表名下 SYSDD_REPORT。', 1;
IF (SELECT COUNT(*) FROM dbo.FIELDS WHERE LTRIM(RTRIM(T_ID)) = N'REPORT_USER_STATE') <> @FieldsBefore
    THROW 53618, N'后置失败：字段登记数与改名前后不一致（登记行丢失）。', 1;
IF EXISTS (SELECT 1 FROM dbo.TABLES WHERE LTRIM(RTRIM(T_ID)) = N'REPORT_USER_STATE' AND T_REMARK LIKE N'%r_m_idx%')
    THROW 53619, N'后置失败：表登记里仍残留已退役列 r_m_idx 的查询关系。', 1;
IF EXISTS (SELECT 1 FROM dbo.FIELD_DATASOURCE WHERE LTRIM(RTRIM(T_ID)) = N'SYSDD_REPORT')
    THROW 53620, N'后置失败：字段数据源登记仍挂在旧表名 SYSDD_REPORT 上。', 1;
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_FIELD_DATASOURCE_FIELDS' AND is_not_trusted = 1)
    THROW 53621, N'后置失败：FK_FIELD_DATASOURCE_FIELDS 未恢复为受信任状态（临时禁用没复核回来）。', 1;

PRINT N'[] SYSDD_REPORT → REPORT_USER_STATE 改名完成：'
    + CONVERT(NVARCHAR(20), @RowsBefore) + N' 行用户状态、'
    + CONVERT(NVARCHAR(20), @FieldsBefore) + N' 行字段登记已随改名迁移；'
    + N'TABLES.T_REMARK 与 FIELDS.M_IDX 的旧语义已订正。';
