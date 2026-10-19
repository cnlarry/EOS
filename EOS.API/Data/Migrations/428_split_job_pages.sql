-- ============================================================================
-- EOS.ERP migration 428: 后台作业按模块拆分为独立承载页
-- ----------------------------------------------------------------------------
-- 背景：/jobs 一个承载页同时承载 5 个模块（230901 产品可用库存重计、
-- 180218 员工批量发卡、180654 考勤模拟生成、180659 考勤真实抽取生成、
-- 180505 依薪资调整考勤）。四个菜单入口打开同一份内容，而权限是按模块分别
-- 授予的：能看见 ≠ 能执行；180654 / 180659 还在服务端共用一次"或"判断，
-- 任一模块的设置权即可执行另一作业。
--
-- 本迁移让 MODULES.M_URL 与前端路由一一对应（/jobs/<作业>），入口、页面与
-- 权限锚点回到同一个模块。180218 同时从 /workbench 改到 /jobs/card-batch：
-- 它没有统一表单（迁移 355 已置 UNIFIED_FORM_ENABLED = 0），原落点只能进空列表，
-- 批量发卡功能没有入口。
--
-- 形态变更（180218：WORKBENCH → CUSTOMPAGE）要清掉只有工作台 / 统一表单消费的
-- 配置与快照：IF_COPY 在定制页上没有消费方；非工作台节点保留当前快照会让运行期
-- 继续用旧定义（见 scripts/check-module-node-kind.ps1 ③⑤）。
--
-- 幂等：按当前值判断，可重复执行。
-- ============================================================================

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 51960, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

IF (SELECT COUNT(*) FROM dbo.MODULES WHERE M_IDX IN (230901, 180218, 180654, 180659, 180505)) <> 5
    THROW 51961, N'后台作业的五个模块行不齐全，迁移中止。', 1;

BEGIN TRANSACTION;

UPDATE dbo.MODULES
   SET M_URL = N'/jobs/mrp-recalc', LAST_UPDATE_BY = N'428', LAST_UPDATE_DATE = GETDATE()
 WHERE M_IDX = 230901 AND ISNULL(M_URL, N'') <> N'/jobs/mrp-recalc';
PRINT N'== 230901 承载页 → /jobs/mrp-recalc：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

UPDATE dbo.MODULES
   SET M_URL = N'/jobs/card-batch',
       IF_COPY = 0,
       REMARK = N'已归类特殊页：员工批量发卡以批量作业页交付（/jobs/card-batch）；单卡维护 180208 走 employee-card 领域规则',
       LAST_UPDATE_BY = N'428',
       LAST_UPDATE_DATE = GETDATE()
 WHERE M_IDX = 180218
   AND (ISNULL(M_URL, N'') <> N'/jobs/card-batch' OR ISNULL(IF_COPY, 0) <> 0);
PRINT N'== 180218 承载页 → /jobs/card-batch（清列表复制）：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

UPDATE dbo.MODULES
   SET M_URL = N'/jobs/attendance-simulate', LAST_UPDATE_BY = N'428', LAST_UPDATE_DATE = GETDATE()
 WHERE M_IDX = 180654 AND ISNULL(M_URL, N'') <> N'/jobs/attendance-simulate';
PRINT N'== 180654 承载页 → /jobs/attendance-simulate：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

UPDATE dbo.MODULES
   SET M_URL = N'/jobs/attendance-extract', LAST_UPDATE_BY = N'428', LAST_UPDATE_DATE = GETDATE()
 WHERE M_IDX = 180659 AND ISNULL(M_URL, N'') <> N'/jobs/attendance-extract';
PRINT N'== 180659 承载页 → /jobs/attendance-extract：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

UPDATE dbo.MODULES
   SET M_URL = N'/jobs/attendance-adjust-wage', LAST_UPDATE_BY = N'428', LAST_UPDATE_DATE = GETDATE()
 WHERE M_IDX = 180505 AND ISNULL(M_URL, N'') <> N'/jobs/attendance-adjust-wage';
PRINT N'== 180505 承载页 → /jobs/attendance-adjust-wage：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

/* 180218 已不是工作台节点：发布流程与快照族对它不再适用（定制页每请求实时装配）。
   这两张表由迁移 007 建立过守卫，此处同样按存在性判断，避免在旧库上失败。 */
IF OBJECT_ID(N'dbo.WORKBENCH_MODULE_DIRTY', N'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX = 180218;
    PRINT N'== 180218 脏标记清理：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';
END

IF OBJECT_ID(N'dbo.WORKBENCH_DEFINITION_SNAPSHOT', N'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE M_IDX = 180218;
    PRINT N'== 180218 定义快照清理：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';
END

/* 收口断言：五条承载页各归各的模块；180218 的形态与残留必须同步收干净。 */
DECLARE @mismatch NVARCHAR(400) = N'';
SELECT @mismatch = @mismatch + CONVERT(NVARCHAR(20), M_IDX) + N','
  FROM dbo.MODULES
 WHERE (M_IDX = 230901 AND M_URL <> N'/jobs/mrp-recalc')
    OR (M_IDX = 180218 AND (M_URL <> N'/jobs/card-batch' OR ISNULL(IF_COPY, 0) <> 0))
    OR (M_IDX = 180654 AND M_URL <> N'/jobs/attendance-simulate')
    OR (M_IDX = 180659 AND M_URL <> N'/jobs/attendance-extract')
    OR (M_IDX = 180505 AND M_URL <> N'/jobs/attendance-adjust-wage');
IF @mismatch <> N''
    THROW 51962, N'后台作业承载页未按预期落位，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.V_MODULE_NODE WHERE M_IDX = 180218 AND NODE_KIND = N'CUSTOMPAGE')
    THROW 51963, N'180218 未转为自定义承载页（检查 M_URL 与主表口径），迁移中止。', 1;

COMMIT TRANSACTION;
PRINT N'== 收口：后台作业五页各自独立 ==';
