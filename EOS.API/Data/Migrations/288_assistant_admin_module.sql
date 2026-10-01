-- ============================================================================
-- EOS.ERP migration 288: 工作助手管理（根组 31 + 会话管理 3101 + 授权镜像）
-- ----------------------------------------------------------------------------
--  来源：ADR-030（工作助手管理面）§1 / §4 / §5
--
--  为什么新开根组 31 而不是挂到 23 系统管理：助手是横切能力，与 21 工作流 /
--  25 数据查询中心同级；"定制页一律落 /admin/*、都归 23"的惯例针对的是系统管理类
--  运维页，把助手管理塞进 23 会让那个根组同时承载两种东西。这是**明确记录的例外**——
--  历史上 110112（日志管理）曾因不符惯例被迁移 284 重编号为 2313，ADR-030 §1 把这
--  条决定与理由留了下来，就是为了避免 31 段再被"顺手订正"。
--
--  权限镜像参照 2306（用户权限设定）：它是"谁能做系统管理"的既有口径，与 ADR-030 §4
--  「不新造角色、新模块镜像参照模块授权行」一致。**镜像只保证与参照模块同权**，授不
--  授予由管理员在权限界面决定。
--
--  根组 31 不单独镜像授权行：导航查询会自动补齐祖先节点（NavigationRepository）。
--
--  幂等：模块与授权行均按"不存在才插"；可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GuardMessage NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 51700, @GuardMessage, 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2306)
    THROW 51701, N'参照模块 2306 用户权限设定不存在，迁移中止（工作助手管理面的授权镜像以它为准）。', 1;

BEGIN TRANSACTION;

/* ---------- 1. 根组 31（M_P_IDX=0 表示根；M_ROOT_IDX 指向自身）---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 31)
BEGIN
    INSERT INTO dbo.MODULES (M_IDX, M_DESC, M_URL, M_P_IDX, M_ROOT_IDX, SORT_IDX, M_TAG, MASTER_TABLE)
    VALUES (31, N'工作助手管理', N'', 0, 31, 270, 1, NULL);
    PRINT N'== 新增根组 31 工作助手管理 ==';
END
ELSE
    PRINT N'== 根组 31 已存在，跳过 ==';

/* ---------- 2. 子模块 3101 会话管理（M_URL 指向定制页）---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 3101)
BEGIN
    INSERT INTO dbo.MODULES (M_IDX, M_DESC, M_URL, M_P_IDX, M_ROOT_IDX, SORT_IDX, M_TAG, MASTER_TABLE)
    VALUES (3101, N'会话管理', N'/admin/assistant/sessions', 31, 31, 10, 1, NULL);
    PRINT N'== 新增模块 3101 会话管理（/admin/assistant/sessions）==';
END
ELSE
    PRINT N'== 模块 3101 已存在，跳过 ==';

/* ---------- 3. 组权限镜像（照 2306 的口径）---------- */
INSERT INTO dbo.SYSDH
    (G_IDX, M_IDX, EXEC_TAG, ADDNEW_TAG, DELETE_TAG, EDIT_TAG, REPORT_TAG, COST_TAG, SETUP_TAG, SECRECY_TAG,
     ENDCASE_TAG, UNENDCASE_TAG, OTHER1_TAG, OTHER2_TAG, OTHER3_TAG, OTHER4_TAG,
     DENY_VIEW_FIELD_MASTER, DENY_VIEW_FIELD_DETAIL, DENY_NEW_FIELD_MASTER, DENY_NEW_FIELD_DETAIL,
     DENY_MODI_FIELD_MASTER, DENY_MODI_FIELD_DETAIL, DATA_FILTER, CI, OPERFLAG,
     APPROVE_TAG, DEAPPROVE_TAG, FILE_VIEW_TAG, FILE_UPDA_TAG, FILE_EDIT_TAG, FILE_DELE_TAG,
     FORM_DESIGN_TAG, MODULE_CONFIG_TAG)
SELECT h.G_IDX, 3101, h.EXEC_TAG, h.ADDNEW_TAG, h.DELETE_TAG, h.EDIT_TAG, h.REPORT_TAG, h.COST_TAG,
       h.SETUP_TAG, h.SECRECY_TAG, h.ENDCASE_TAG, h.UNENDCASE_TAG, h.OTHER1_TAG, h.OTHER2_TAG,
       h.OTHER3_TAG, h.OTHER4_TAG, h.DENY_VIEW_FIELD_MASTER, h.DENY_VIEW_FIELD_DETAIL,
       h.DENY_NEW_FIELD_MASTER, h.DENY_NEW_FIELD_DETAIL, h.DENY_MODI_FIELD_MASTER, h.DENY_MODI_FIELD_DETAIL,
       h.DATA_FILTER, h.CI, h.OPERFLAG, h.APPROVE_TAG, h.DEAPPROVE_TAG, h.FILE_VIEW_TAG, h.FILE_UPDA_TAG,
       h.FILE_EDIT_TAG, h.FILE_DELE_TAG, h.FORM_DESIGN_TAG, h.MODULE_CONFIG_TAG
FROM dbo.SYSDH h
WHERE h.M_IDX = 2306
  AND NOT EXISTS (SELECT 1 FROM dbo.SYSDH x WHERE x.G_IDX = h.G_IDX AND x.M_IDX = 3101);

PRINT N'== 已镜像组权限 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

/* ---------- 4. 个人权限镜像（admin 靠这一步才看得见新菜单）---------- */
INSERT INTO dbo.SYSDD
    (USER_ID, M_IDX, EXEC_TAG, ADDNEW_TAG, DELETE_TAG, EDIT_TAG, REPORT_TAG, COST_TAG, SETUP_TAG, SECRECY_TAG,
     ENDCASE_TAG, UNENDCASE_TAG, OTHER1_TAG, OTHER2_TAG, OTHER3_TAG, OTHER4_TAG,
     DENY_VIEW_FIELD_MASTER, DENY_VIEW_FIELD_DETAIL, DENY_NEW_FIELD_MASTER, DENY_NEW_FIELD_DETAIL,
     DENY_MODI_FIELD_MASTER, DENY_MODI_FIELD_DETAIL, DATA_FILTER, CI, OPERFLAG,
     APPROVE_TAG, DEAPPROVE_TAG, FILE_VIEW_TAG, FILE_UPDA_TAG, FILE_EDIT_TAG, FILE_DELE_TAG,
     FORM_DESIGN_TAG, MODULE_CONFIG_TAG)
SELECT d.USER_ID, 3101, d.EXEC_TAG, d.ADDNEW_TAG, d.DELETE_TAG, d.EDIT_TAG, d.REPORT_TAG, d.COST_TAG,
       d.SETUP_TAG, d.SECRECY_TAG, d.ENDCASE_TAG, d.UNENDCASE_TAG, d.OTHER1_TAG, d.OTHER2_TAG,
       d.OTHER3_TAG, d.OTHER4_TAG, d.DENY_VIEW_FIELD_MASTER, d.DENY_VIEW_FIELD_DETAIL,
       d.DENY_NEW_FIELD_MASTER, d.DENY_NEW_FIELD_DETAIL, d.DENY_MODI_FIELD_MASTER, d.DENY_MODI_FIELD_DETAIL,
       d.DATA_FILTER, d.CI, d.OPERFLAG, d.APPROVE_TAG, d.DEAPPROVE_TAG, d.FILE_VIEW_TAG, d.FILE_UPDA_TAG,
       d.FILE_EDIT_TAG, d.FILE_DELE_TAG, d.FORM_DESIGN_TAG, d.MODULE_CONFIG_TAG
FROM dbo.SYSDD d
WHERE d.M_IDX = 2306
  AND NOT EXISTS (SELECT 1 FROM dbo.SYSDD x WHERE x.USER_ID = d.USER_ID AND x.M_IDX = 3101);

PRINT N'== 已镜像个人权限 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

/* ---------- 5. 收口断言 ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 31 AND M_P_IDX = 0 AND M_ROOT_IDX = 31 AND M_TAG = 1)
    THROW 51702, N'根组 31 未就位（或不是 M_P_IDX=0 的启用根节点），迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES
               WHERE M_IDX = 3101 AND M_P_IDX = 31 AND M_ROOT_IDX = 31
                 AND M_URL = N'/admin/assistant/sessions' AND M_TAG = 1)
    THROW 51703, N'模块 3101 未就位或 URL 不是 /admin/assistant/sessions，迁移中止。', 1;

DECLARE @RefGroups INT = (SELECT COUNT(*) FROM dbo.SYSDH WHERE M_IDX = 2306);
DECLARE @NewGroups INT = (SELECT COUNT(*) FROM dbo.SYSDH WHERE M_IDX = 3101);
IF @NewGroups < @RefGroups
    THROW 51704, N'模块 3101 的组权限行数少于参照模块 2306，迁移中止。', 1;

DECLARE @RefUsers INT = (SELECT COUNT(*) FROM dbo.SYSDD WHERE M_IDX = 2306);
DECLARE @NewUsers INT = (SELECT COUNT(*) FROM dbo.SYSDD WHERE M_IDX = 3101);
IF @NewUsers < @RefUsers
    THROW 51705, N'模块 3101 的个人权限行数少于参照模块 2306，迁移中止（admin 会看不到新菜单）。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：工作助手管理（31）与会话管理（3101）已按 2306 的口径就位 ==';
