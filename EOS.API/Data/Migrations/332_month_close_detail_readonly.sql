-- ============================================================================
-- EOS.ERP migration 332: 月结单明细字段设为只读（明细由「生成快照」产生，不该手工录）
-- ----------------------------------------------------------------------------
-- 现象（用户报障，页面 /workbench/1304/new）：在新建页保存被 400 拒——
--   明细写入被拒 table=INV_PRO_MONTH_D row=0 code=REQUIRED_FIELD_MISSING（DEPOT_ID / BATCH_NO 必填）。
--
-- 根因：月结单的明细是**对账快照**，由 MANUAL 动作 month-close-snapshot 按当前库存余额算出来写库
--   （MonthCloseSnapshotService，直接落 INV_PRO_MONTH_D，不经表单）。可明细字段在表单里是**可写**的，
--   新建时界面又常带出一个空行，用户自然去填；填不全就撞必填校验 —— 于是"建单"这一步在界面上走不通。
--
-- 处置（2026-10-07 用户拍板 A+B 的 B 项）：
--   把 INV_PRO_MONTH_D 的字段**全部设为只读**，并去掉必填校验（IS_VERIFY=0）。明细只给看、不给手填：
--   它本来就由动作生成，手工录进去的值下一次「生成快照」也会被覆盖。
--
-- 配套与边界：
--   ⒜ 与迁移 331 配套：建单（不再自动批核）→ 生成快照 → 批核。
--   ⒝ 与前端改动配套：提交体不再带"空白新行"（前端已自行过滤；这里是服务端侧的语义收口）。
--   ⒞ **不动主表字段**：单别 / 单号 / 结转日期仍要手工填。
--   ⒟ 「生成快照」动作直接写库，不受字段只读影响；查询与报表照旧可读这些列。
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
    THROW 52500, @GUARD_MESSAGE, 1;

DECLARE @MonthModule INT = 1304;
DECLARE @DetailTable NVARCHAR(100) = N'INV_PRO_MONTH_D';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @MonthModule
                 AND LTRIM(RTRIM(ISNULL(DETAIL_TABLE, ''))) = @DetailTable)
    THROW 52501, N'模块 1304 形态不符（明细表应为 INV_PRO_MONTH_D），迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.FIELDS WHERE LTRIM(RTRIM(T_ID)) = @DetailTable)
    THROW 52502, N'明细表字段元数据缺失，迁移中止。', 1;

/* ---------- 明细字段：只读 + 去必填 ---------- */
UPDATE dbo.FIELDS
   SET IS_READONLY = 1,
       IS_VERIFY = 0,
       LAST_UPDATE_BY = N'migration-332',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE LTRIM(RTRIM(T_ID)) = @DetailTable
   AND (ISNULL(IS_READONLY, 0) = 0 OR ISNULL(IS_VERIFY, 0) = 1);

/* ---------- 标脏待发布（定义快照要重发布才生效） ---------- */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS T
USING (SELECT @MonthModule AS M_IDX) AS S ON T.M_IDX = S.M_IDX
WHEN MATCHED THEN UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = N'DbUp', LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.M_IDX, 1, N'DbUp', SYSDATETIME());

/* ---------- 收口断言 ---------- */
IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE LTRIM(RTRIM(T_ID)) = @DetailTable AND ISNULL(IS_READONLY, 0) = 0)
    THROW 52503, N'INV_PRO_MONTH_D 仍有可写字段，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE LTRIM(RTRIM(T_ID)) = @DetailTable AND ISNULL(IS_VERIFY, 0) = 1)
    THROW 52504, N'INV_PRO_MONTH_D 仍有必填字段，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX = @MonthModule AND DIRTY_TAG = 1)
    THROW 52505, N'模块 1304 未标记为待发布，迁移中止。', 1;

PRINT N'== 月结单明细已设为只读（只给看、不给手填），模块待发布 ==';
