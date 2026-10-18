-- ============================================================================
-- EOS.ERP migration 419: 「计划人员」只能从员工资料选择，不能手输
-- ----------------------------------------------------------------------------
-- 旧系统里 `BOM_STRU_M.PLAN_PERSON` 是从员工档案选出来的：
--   `~/Admin/MagEmployee.aspx?IDX=EMP_ID={PLAN_PERSON}`（按 EMP_ID 打开员工记录）
--
-- 本库的字段元数据里，**选择器本身一直在**（`FieldChooserSource`），只是
-- `ONLY_CHOOSE` 为 0：
--   table = SYSDN（员工资料）
--   returnMapping = PLAN_PERSON ← EMP_ID，PLAN_PERSON_NAME ← EMP_NAME
-- 也就是说，界面上既能点选、也能手打——手打的值不会经过任何校验，打错一个字符
-- 就是一条指向不存在员工的数据。本次只把 `ONLY_CHOOSE` 收紧为 1。
--
-- **不动 `BROWSE_M_IDX`**（保持 0）：这个字段走的是**源表直连选择器**（`SYSDN`，
-- `moduleId=0`），不经过"打开某个模块的记录页"那条路。给它填模块号会让前端误以为
-- 它属于那个模块，生成一条指向错误位置的链接。同理 `BROWSE_URL` 也清空——那是旧
-- 系统 WebForms 的路径，本系统不读。
--
-- 存量数据：`PLAN_PERSON` 全库 0 条有值（实测），本次收紧不会让任何已有记录变不合法。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Mig NVARCHAR(100) = N'DbUp';

IF NOT EXISTS (SELECT 1 FROM dbo.FIELDS
               WHERE LTRIM(RTRIM(T_ID)) = N'BOM_STRU_M' AND LTRIM(RTRIM(F_ID)) = N'PLAN_PERSON')
    THROW 58001, N'BOM_STRU_M.PLAN_PERSON 字段元数据不存在，迁移中止。', 1;

BEGIN TRANSACTION;

UPDATE dbo.FIELDS
   SET ONLY_CHOOSE = 1,     -- 只能选，不能手输
       CHOOSE_MULTI = 0,    -- 计划员是一个人
       BROWSE_M_IDX = 0,    -- 走源表直连选择器（SYSDN），不经模块跳转
       BROWSE_URL = '',     -- 旧系统 WebForms 路径，本系统不读
       LAST_UPDATE_BY = @Mig,
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE LTRIM(RTRIM(T_ID)) = N'BOM_STRU_M' AND LTRIM(RTRIM(F_ID)) = N'PLAN_PERSON'
   AND (ISNULL(ONLY_CHOOSE, 0) <> 1
        OR ISNULL(CHOOSE_MULTI, 0) <> 0
        OR ISNULL(BROWSE_M_IDX, 0) <> 0
        OR LTRIM(RTRIM(ISNULL(BROWSE_URL, N''))) <> N'');
PRINT N'[419] 计划人员选择器收紧：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

/* 断言只查 FIELDS。**不要**在这里校验选择器来源表：那张表的名字属于
   FieldAdmin 的内部实现，写在迁移里会随实现改名而失效；更糟的是 SQL Server
   在批次编译期就解析对象名，名字一错整批不执行（本次就因此回滚过一次）。
   选择器来源是否还在，由 `form-definition` 的 `choosers` 数组保证——
   那是运行期契约，比迁移里硬编码表名可靠。 */
IF NOT EXISTS (SELECT 1 FROM dbo.FIELDS
               WHERE LTRIM(RTRIM(T_ID)) = N'BOM_STRU_M' AND LTRIM(RTRIM(F_ID)) = N'PLAN_PERSON'
                 AND ISNULL(ONLY_CHOOSE, 0) = 1
                 AND ISNULL(BROWSE_M_IDX, 0) = 0)
    THROW 58002, N'计划人员元数据未修正到位，迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 计划人员：只能从员工资料选择（BROWSE_M_IDX 保持 0，走 SYSDN 直连）==';
