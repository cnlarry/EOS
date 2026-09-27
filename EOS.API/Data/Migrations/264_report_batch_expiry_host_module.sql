-- ============================================================================
-- EOS.ERP migration 262: 批次效期报表改挂报表承载页（宿主模块 1303 → 139808）
-- ----------------------------------------------------------------------------
-- 报表中心按 `REPORT.R_M_IDX` 拼查看器地址（`/reports/{模块号}`），而查看器要求宿主模块
-- 是**报表承载页**——`ReportRepository` 的 `IsReportUrl` 判据不通过即 404，点进去渲染不出来。
-- 原宿主 1303 料件库存资料是 `/workbench` 页 ⇒ 该报表恒 404；改挂 139808 料件批号资料明细
-- （`MASTER_TABLE=INV_BATCH_M`，`M_URL='/reports'`，同族报表 `INV_Batch_List` 也在这里）。
-- `Q_M_IDX` 保持 1303 作"查询模块"留痕：运行期没有读取点（只有写入与模块改名 UPDATE）。
--
-- 两条筛选项必须跟着宿主模块走：查询条件是**按模块**读的（`SYSQR_DEFAULT.M_IDX`），
-- 留在 1303 就再也取不到。139808 已有 1-4 号条件，故顺延为 5/6（`M_IDX+SERIAL_NO` 是键，
-- 不改会撞键）；服务端注册表里这两个参数登记的序号同步改为 5/6（`ReportAggregateRegistry`）。
-- 搬过去后这两项也会出现在 139808 其它报表的条件面板上——条件按模块共用是既有设计
-- （139901 的 3 张报表共用 7 条条件），不是本迁移的副作用。
--
-- 幂等：(M_IDX, SERIAL_NO) / (USER_ID, M_IDX, SERIAL_NO) 判存在；后置自证要求
-- "新宿主齐备、旧宿主不残留、原有条件不受影响"。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

-- 前置：宿主模块必须是报表承载页，否则改过去照样 404（本迁移的立足点）
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES
               WHERE M_IDX = 139808 AND LTRIM(RTRIM(ISNULL(M_URL, N''))) = N'/reports')
    THROW 52170, N'模块 139808 不是报表承载页（M_URL 应为 /reports），本迁移的前提不成立。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.REPORT WHERE REPORT_ID = N'INV_Batch_Expiry_1')
    THROW 52171, N'报表 INV_Batch_Expiry_1 不存在（应由迁移 259 先行落库），本迁移的前提不成立。', 1;

BEGIN TRANSACTION;

-- ---------------------------------------------------------------- 新旧序号对照
-- 认证判据用 PARA_NAME（序号只是承载）：这样即使旧序号被人挪过也认得出来
DECLARE @Address TABLE (
    OLD_SERIAL SMALLINT NOT NULL PRIMARY KEY,
    NEW_SERIAL SMALLINT NOT NULL UNIQUE,
    PARA_NAME NVARCHAR(100) NOT NULL UNIQUE);
INSERT INTO @Address (OLD_SERIAL, NEW_SERIAL, PARA_NAME) VALUES
    (1, 5, N'@include_expired'),
    (2, 6, N'@unmanaged_only');

-- 目标序号若已被**别的**条件占用，搬过去就是序号错位（筛选项与查询参数对不上）⇒ 中止
IF EXISTS (SELECT 1 FROM dbo.SYSQR_DEFAULT q
           JOIN @Address a ON a.NEW_SERIAL = q.SERIAL_NO
           WHERE q.M_IDX = 139808 AND LTRIM(RTRIM(ISNULL(q.PARA_NAME, N''))) <> a.PARA_NAME)
    THROW 52172, N'模块 139808 的 5/6 号查询条件已被别的条件占用，序号会错位。', 1;

-- 个人条件值同样按 (USER_ID, M_IDX, SERIAL_NO) 为键，目标键被占即中止
IF EXISTS (SELECT 1 FROM dbo.SYSQR_USER u
           JOIN @Address a ON a.OLD_SERIAL = u.SERIAL_NO
           JOIN dbo.SYSQR_USER t ON t.USER_ID = u.USER_ID AND t.M_IDX = 139808 AND t.SERIAL_NO = a.NEW_SERIAL
           WHERE u.M_IDX = 1303)
    THROW 52173, N'个人条件值的目标键已被占用，搬过去会撞键。', 1;

-- ---------------------------------------------------------------- ① 报表行改挂
DECLARE @Changed INT;

-- Q_M_IDX 保持 1303 不动：它是"查询模块"留痕，运行期没有读取点
UPDATE dbo.REPORT
SET R_M_IDX = 139808, LAST_UPDATE_BY = N'mig-262', LAST_UPDATE_DATE = SYSDATETIME()
WHERE REPORT_ID = N'INV_Batch_Expiry_1' AND ISNULL(R_M_IDX, 0) <> 139808;
SET @Changed = @@ROWCOUNT;
PRINT N'== 报表 INV_Batch_Expiry_1 宿主模块改挂 139808：' + CONVERT(NVARCHAR(10), @Changed) + N' 行 ==';

-- ---------------------------------------------------------------- ② 筛选项随宿主模块搬
UPDATE u
SET u.M_IDX = 139808, u.SERIAL_NO = a.NEW_SERIAL
FROM dbo.SYSQR_USER u JOIN @Address a ON a.OLD_SERIAL = u.SERIAL_NO
WHERE u.M_IDX = 1303;
SET @Changed = @@ROWCOUNT;
PRINT N'== 个人条件值搬移：' + CONVERT(NVARCHAR(10), @Changed) + N' 行 ==';

-- 认行按 PARA_NAME（序号只是承载）：旧序号被人挪过也照样能认出来
UPDATE d
SET d.M_IDX = 139808, d.SERIAL_NO = a.NEW_SERIAL
FROM dbo.SYSQR_DEFAULT d JOIN @Address a ON a.PARA_NAME = LTRIM(RTRIM(ISNULL(d.PARA_NAME, N'')))
WHERE d.M_IDX = 1303;
SET @Changed = @@ROWCOUNT;
PRINT N'== 模块查询条件搬移：' + CONVERT(NVARCHAR(10), @Changed) + N' 行 ==';

-- ---------------------------------------------------------------- 后置自证
-- ① 报表挂在新宿主，且新宿主确实是报表承载页
IF NOT EXISTS (SELECT 1 FROM dbo.REPORT r
               JOIN dbo.MODULES m ON m.M_IDX = r.R_M_IDX
               WHERE r.REPORT_ID = N'INV_Batch_Expiry_1' AND r.R_M_IDX = 139808
                 AND LTRIM(RTRIM(ISNULL(m.M_URL, N''))) = N'/reports')
    THROW 52174, N'报表未按预期挂到报表承载页 139808。', 1;

-- ② 旧宿主不残留这两条条件（残留即"M_IDX 没搬干净"）
IF EXISTS (SELECT 1 FROM dbo.SYSQR_DEFAULT q
           JOIN @Address a ON a.PARA_NAME = LTRIM(RTRIM(ISNULL(q.PARA_NAME, N'')))
           WHERE q.M_IDX = 1303)
    THROW 52175, N'旧模块 1303 仍残留本报表的查询条件。', 1;

-- ③ 新宿主上两条条件齐备，且序号与参数名对得上
IF EXISTS (SELECT 1 FROM @Address a
           WHERE NOT EXISTS (SELECT 1 FROM dbo.SYSQR_DEFAULT q
                             WHERE q.M_IDX = 139808 AND q.SERIAL_NO = a.NEW_SERIAL
                               AND LTRIM(RTRIM(ISNULL(q.PARA_NAME, N''))) = a.PARA_NAME))
    THROW 52176, N'报表筛选项未按预期搬到新宿主（序号或参数名不符）。', 1;

-- ④ 新宿主原有条件不受影响（139808 自己原有的 1-4 号 + 本迁移的 5/6 = 至少 6 条）
IF (SELECT COUNT(*) FROM dbo.SYSQR_DEFAULT WHERE M_IDX = 139808) < 6
    THROW 52177, N'新宿主的查询条件数量不足，原有条件可能被覆盖。', 1;

COMMIT TRANSACTION;

PRINT N'== 完成：报表改挂 139808，两条筛选项随之搬到 5/6 号 ==';
