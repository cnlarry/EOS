-- ============================================================================
-- EOS.ERP migration 353: 操作审计独立成模块 2316；日志管理 2313 改挂 23 系统管理本级
-- ----------------------------------------------------------------------------
--  操作审计（/admin/audit）与日志管理（/admin/logs）是两层，不是同一件事的两页：
--  日志管理读的是**磁盘上的运行日志文件**（只留 Warning+，回答"这次请求为什么失败"）；
--  操作审计读的是**库表 AUDIT_EVENT**（回答"谁改的、改成什么"）。
--  共用一个模块号会把"能看日志 = 能看审计"焊死，审计也就无法单独授权，故独立成 2316。
--
--  为什么两个模块都挂在 23 系统管理**本级**（而不是 2311 数据表维护下）：
--  它们面向的是系统运维与追责，不属于"看元数据"那一组；挂本级后菜单里各自是一个入口，
--  不用从日志管理页跳过去。SORT_IDX：2313 取 70、2316 取 80，排在 2306（60）之后。
--
--  为什么授权镜像 2313：两者当前由同一批运维人员使用，起步口径一致；
--  独立成模块之后想单独收紧，改 2316 的授权行即可，不再牵动日志。
--
--  幂等：模块 2316 与授权行均按 (主体, M_IDX) 不存在才插；2313 的改挂按当前值判断，可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GuardMessage NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 56200, @GuardMessage, 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 23)
    THROW 56201, N'根模块 23 系统管理不存在，迁移中止（2313 / 2316 挂在它本级）。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2313 AND M_URL = N'/admin/logs')
    THROW 56202, N'模块 2313 未就位或 URL 不是 /admin/logs，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2316 AND M_URL <> N'/admin/audit')
    THROW 56203, N'模块 2316 已被占用（URL 不是 /admin/audit），迁移中止：编号不回收、不复用。', 1;

BEGIN TRANSACTION;

/* ---------- 1. 日志管理 2313：从 2311 数据表维护改挂 23 系统管理本级 ---------- */
IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2313 AND (M_P_IDX <> 23 OR ISNULL(SORT_IDX, 0) <> 70))
BEGIN
    UPDATE dbo.MODULES
       SET M_P_IDX = 23,
           M_ROOT_IDX = 23,
           SORT_IDX = 70,
           LAST_UPDATE_BY = N'353',
           LAST_UPDATE_DATE = GETDATE()
     WHERE M_IDX = 2313;
    PRINT N'== 模块 2313 已改挂 23 系统管理本级（序 70）==';
END
ELSE
    PRINT N'== 模块 2313 已在 23 本级（序 70），跳过改挂 ==';

-- 2311 的子模块由 5 个变为 4 个（2313 迁出），目录备注随之修正
UPDATE dbo.MODULES
   SET REMARK = REPLACE(REMARK, N'含 5 个子模块', N'含 4 个子模块'),
       LAST_UPDATE_BY = N'353',
       LAST_UPDATE_DATE = GETDATE()
 WHERE M_IDX = 2311
   AND REMARK LIKE N'%含 5 个子模块%';
PRINT N'== 2311 数据表维护备注已按子模块数修正 ==';

/* ---------- 2. 模块登记：23 系统管理 → 2316 操作审计 ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2316)
BEGIN
    INSERT INTO dbo.MODULES (M_IDX, M_DESC, M_URL, M_P_IDX, M_ROOT_IDX, SORT_IDX, M_TAG, MASTER_TABLE)
    VALUES (2316, N'操作审计', N'/admin/audit', 23, 23, 80, 1, NULL);
    PRINT N'== 新增模块 2316 操作审计（/admin/audit，父 23、根 23、序 80）==';
END
ELSE
    PRINT N'== 模块 2316 已存在，跳过登记 ==';

/* ---------- 3. 组权限：按 2313 镜像 ---------- */
INSERT INTO dbo.SYSDH
    (G_IDX, M_IDX, EXEC_TAG, ADDNEW_TAG, DELETE_TAG, EDIT_TAG, REPORT_TAG, COST_TAG, SETUP_TAG, SECRECY_TAG,
     ENDCASE_TAG, UNENDCASE_TAG, OTHER1_TAG, OTHER2_TAG, OTHER3_TAG, OTHER4_TAG,
     DENY_VIEW_FIELD_MASTER, DENY_VIEW_FIELD_DETAIL, DENY_NEW_FIELD_MASTER, DENY_NEW_FIELD_DETAIL,
     DENY_MODI_FIELD_MASTER, DENY_MODI_FIELD_DETAIL, DATA_FILTER, OPERFLAG,
     APPROVE_TAG, DEAPPROVE_TAG, FILE_VIEW_TAG, FILE_UPDA_TAG, FILE_EDIT_TAG, FILE_DELE_TAG,
     FORM_DESIGN_TAG, MODULE_CONFIG_TAG)
SELECT h.G_IDX, 2316, h.EXEC_TAG, h.ADDNEW_TAG, h.DELETE_TAG, h.EDIT_TAG, h.REPORT_TAG, h.COST_TAG,
       h.SETUP_TAG, h.SECRECY_TAG, h.ENDCASE_TAG, h.UNENDCASE_TAG, h.OTHER1_TAG, h.OTHER2_TAG,
       h.OTHER3_TAG, h.OTHER4_TAG, h.DENY_VIEW_FIELD_MASTER, h.DENY_VIEW_FIELD_DETAIL,
       h.DENY_NEW_FIELD_MASTER, h.DENY_NEW_FIELD_DETAIL, h.DENY_MODI_FIELD_MASTER, h.DENY_MODI_FIELD_DETAIL,
       h.DATA_FILTER, h.OPERFLAG, h.APPROVE_TAG, h.DEAPPROVE_TAG, h.FILE_VIEW_TAG, h.FILE_UPDA_TAG,
       h.FILE_EDIT_TAG, h.FILE_DELE_TAG, h.FORM_DESIGN_TAG, h.MODULE_CONFIG_TAG
FROM dbo.SYSDH h
WHERE h.M_IDX = 2313
  AND NOT EXISTS (SELECT 1 FROM dbo.SYSDH x WHERE x.G_IDX = h.G_IDX AND x.M_IDX = 2316);

PRINT N'== 已按模块 2313 补组权限 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

/* ---------- 4. 个人权限：同口径（个人权限整表覆盖组权限，漏搬会直接少授权）---------- */
INSERT INTO dbo.SYSDD
    (USER_ID, M_IDX, EXEC_TAG, ADDNEW_TAG, DELETE_TAG, EDIT_TAG, REPORT_TAG, COST_TAG, SETUP_TAG, SECRECY_TAG,
     ENDCASE_TAG, UNENDCASE_TAG, OTHER1_TAG, OTHER2_TAG, OTHER3_TAG, OTHER4_TAG,
     DENY_VIEW_FIELD_MASTER, DENY_VIEW_FIELD_DETAIL, DENY_NEW_FIELD_MASTER, DENY_NEW_FIELD_DETAIL,
     DENY_MODI_FIELD_MASTER, DENY_MODI_FIELD_DETAIL, DATA_FILTER, OPERFLAG,
     APPROVE_TAG, DEAPPROVE_TAG, FILE_VIEW_TAG, FILE_UPDA_TAG, FILE_EDIT_TAG, FILE_DELE_TAG,
     FORM_DESIGN_TAG, MODULE_CONFIG_TAG)
SELECT d.USER_ID, 2316, d.EXEC_TAG, d.ADDNEW_TAG, d.DELETE_TAG, d.EDIT_TAG, d.REPORT_TAG, d.COST_TAG,
       d.SETUP_TAG, d.SECRECY_TAG, d.ENDCASE_TAG, d.UNENDCASE_TAG, d.OTHER1_TAG, d.OTHER2_TAG,
       d.OTHER3_TAG, d.OTHER4_TAG, d.DENY_VIEW_FIELD_MASTER, d.DENY_VIEW_FIELD_DETAIL,
       d.DENY_NEW_FIELD_MASTER, d.DENY_NEW_FIELD_DETAIL, d.DENY_MODI_FIELD_MASTER, d.DENY_MODI_FIELD_DETAIL,
       d.DATA_FILTER, d.OPERFLAG, d.APPROVE_TAG, d.DEAPPROVE_TAG, d.FILE_VIEW_TAG, d.FILE_UPDA_TAG,
       d.FILE_EDIT_TAG, d.FILE_DELE_TAG, d.FORM_DESIGN_TAG, d.MODULE_CONFIG_TAG
FROM dbo.SYSDD d
WHERE d.M_IDX = 2313
  AND NOT EXISTS (SELECT 1 FROM dbo.SYSDD x WHERE x.USER_ID = d.USER_ID AND x.M_IDX = 2316);

PRINT N'== 已按模块 2313 补个人权限 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

/* ---------- 5. 收口断言 ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2313 AND M_URL = N'/admin/logs'
                 AND M_P_IDX = 23 AND M_ROOT_IDX = 23 AND SORT_IDX = 70)
    THROW 56204, N'模块 2313 未就位或层级不是 23 本级（序 70），迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2316 AND M_URL = N'/admin/audit'
                 AND M_P_IDX = 23 AND M_ROOT_IDX = 23 AND SORT_IDX = 80 AND ISNULL(M_TAG, 1) = 1)
    THROW 56205, N'模块 2316 未就位或层级不是 23 本级（序 80），迁移中止。', 1;

IF EXISTS (
        SELECT h.G_IDX FROM dbo.SYSDH h
        WHERE h.M_IDX = 2313
          AND NOT EXISTS (SELECT 1 FROM dbo.SYSDH x WHERE x.G_IDX = h.G_IDX AND x.M_IDX = 2316))
    THROW 56206, N'模块 2316 的组权限少于参照模块 2313，迁移中止。', 1;

IF EXISTS (
        SELECT d.USER_ID FROM dbo.SYSDD d
        WHERE d.M_IDX = 2313
          AND NOT EXISTS (SELECT 1 FROM dbo.SYSDD x WHERE x.USER_ID = d.USER_ID AND x.M_IDX = 2316))
    THROW 56207, N'模块 2316 的个人权限少于参照模块 2313，迁移中止。', 1;

DECLARE @GroupRows INT = (SELECT COUNT(*) FROM dbo.SYSDH WHERE M_IDX = 2316);
DECLARE @UserRows INT = (SELECT COUNT(*) FROM dbo.SYSDD WHERE M_IDX = 2316);
PRINT N'== 收口：2313 已挂 23 本级（序 70）；2316 操作审计已登记，授权 '
      + CONVERT(NVARCHAR(10), @GroupRows) + N' 组 / ' + CONVERT(NVARCHAR(10), @UserRows) + N' 人 ==';

COMMIT TRANSACTION;
