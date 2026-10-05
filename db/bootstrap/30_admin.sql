/*
 * 初始账号与权限
 *
 * 建立两个账号：admin（管理员，全开）与 larry（普通用户，无配置/保密/成本与审批权限）。
 * 组织结构（公司/部门/员工）由基础资料种子 25_base_data.sql 提供，此处只作兜底补齐。
 *
 * 初始口令：admin / admin，larry / larry，仅用于首次登录。请在登录后立即通过「修改密码」更换，
 * 或执行本文件末尾注释中的语句写入自定义口令哈希。
 *
 * 幂等：可重复执行；已存在的记录不会被覆盖，权限行会补齐到最新模块清单。
 */

SET NOCOUNT ON;
GO

-- ---------------------------------------------------------------- 组织结构（兜底）
IF NOT EXISTS (SELECT 1 FROM dbo.COMPANY WHERE LTRIM(RTRIM(COMPANY_ID)) = 'DEFAULT')
    INSERT dbo.COMPANY (COMPANY_ID, NAME_CN, NAME_EN, SHORT_NAME_CN, SHORT_NAME_EN, CONFIRM_TAG)
    VALUES ('DEFAULT', N'默认有限公司', N'DEFAULT LLC', N'默认公司', N'DEFAULT', 1);
GO

IF NOT EXISTS (SELECT 1 FROM dbo.DEPT WHERE LTRIM(RTRIM(DEPT_ID)) = 'ZJB')
    INSERT dbo.DEPT (DEPT_ID, DEPT_NAME, CONFIRM_TAG)
    VALUES ('ZJB', N'总经办', 1);
GO

IF NOT EXISTS (SELECT 1 FROM dbo.SYSDN WHERE LTRIM(RTRIM(EMP_ID)) = 'admin')
    INSERT dbo.SYSDN (EMP_ID, EMP_NAME, DEPT_ID, CONFIRM_TAG)
    VALUES ('admin', N'系统管理员', 'ZJB', 1);
GO

IF NOT EXISTS (SELECT 1 FROM dbo.SYSDN WHERE LTRIM(RTRIM(EMP_ID)) = 'larry')
    INSERT dbo.SYSDN (EMP_ID, EMP_NAME, DEPT_ID, CONFIRM_TAG)
    VALUES ('larry', N'演示用户', 'ZJB', 1);
GO

-- ---------------------------------------------------------------- 账号
-- 口令哈希格式："v1" + Base64(盐[12] || PBKDF2-SHA256(口令, 盐, 210000 次迭代, 24 字节))
-- 下面的值分别对应口令 admin 与 larry。
IF NOT EXISTS (SELECT 1 FROM dbo.SYSDL WHERE LTRIM(RTRIM(USER_ID)) = 'admin')
    INSERT dbo.SYSDL (USER_ID, USER_PWD, EMP_ID, ACTIVE_TAG, CONFIRM_TAG, REMARK)
    VALUES ('admin', N'v1Z6SJh2GJYChyu+14+n5FcDFhMVlPuK0HxKD8fcl1mkuCLJe3', 'admin', 1, 1,
            N'初始管理员账号，首次登录后请立即修改口令');
GO

IF NOT EXISTS (SELECT 1 FROM dbo.SYSDL WHERE LTRIM(RTRIM(USER_ID)) = 'larry')
    INSERT dbo.SYSDL (USER_ID, USER_PWD, EMP_ID, ACTIVE_TAG, CONFIRM_TAG, REMARK)
    VALUES ('larry', N'v1gnnK62ABonFL3CkSYtsMC5ZTpIWZUWM4+2A4lJbueAKE2ZL4', 'larry', 1, 1,
            N'初始普通用户账号，首次登录后请立即修改口令');
GO

-- ---------------------------------------------------------------- 权限
-- 权限模型：一旦某用户在某模块存在个人权限记录，即完全采用个人权限，不再合并用户组权限。
-- 因此为 admin 逐模块写入全开的个人权限行，即可获得完整权限，无需建立用户组。
--
-- EXEC_TAG 是执行类别，按字符串比较取较大者为更高权限；取字典中的最大值，字典为空时用 'A'。
DECLARE @execTag CHAR(1) = ISNULL((SELECT MAX(EXEC_ID) FROM dbo.SYSDD_EXEC_TYPE), 'A');

INSERT dbo.SYSDD (
    USER_ID, M_IDX, EXEC_TAG,
    ADDNEW_TAG, EDIT_TAG, DELETE_TAG, REPORT_TAG, COST_TAG, SETUP_TAG, SECRECY_TAG,
    ENDCASE_TAG, UNENDCASE_TAG, APPROVE_TAG, DEAPPROVE_TAG,
    OTHER1_TAG, OTHER2_TAG, OTHER3_TAG, OTHER4_TAG,
    FILE_VIEW_TAG, FILE_UPDA_TAG, FILE_EDIT_TAG, FILE_DELE_TAG,
    FORM_DESIGN_TAG, MODULE_CONFIG_TAG,
    OPERFLAG
)
SELECT 'admin', m.M_IDX, @execTag,
       1, 1, 1, 1, 1, 1, 1,
       1, 1, 1, 1,
       1, 1, 1, 1,
       1, 1, 1, 1,
       1, 1,
       0
FROM (SELECT M_IDX FROM dbo.MODULES) m
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SYSDD d
    WHERE LTRIM(RTRIM(d.USER_ID)) = 'admin' AND d.M_IDX = m.M_IDX
);
GO

-- 普通用户 larry：可日常录入/修改/删除/查询，但不含配置（SETUP）、保密（SECRECY）、
-- 成本（COST）与审批（批核/解批/结案/反结案）权限——审批与分权属职权，不下放。
DECLARE @execTagUser CHAR(1) = ISNULL((SELECT MAX(EXEC_ID) FROM dbo.SYSDD_EXEC_TYPE), 'A');

INSERT dbo.SYSDD (
    USER_ID, M_IDX, EXEC_TAG,
    ADDNEW_TAG, EDIT_TAG, DELETE_TAG, REPORT_TAG, COST_TAG, SETUP_TAG, SECRECY_TAG,
    ENDCASE_TAG, UNENDCASE_TAG, APPROVE_TAG, DEAPPROVE_TAG,
    OTHER1_TAG, OTHER2_TAG, OTHER3_TAG, OTHER4_TAG,
    FILE_VIEW_TAG, FILE_UPDA_TAG, FILE_EDIT_TAG, FILE_DELE_TAG,
    OPERFLAG
)
SELECT 'larry', m.M_IDX, @execTagUser,
       1, 1, 1, 1, 0, 0, 0,
       0, 0, 0, 0,
       1, 1, 1, 1,
       1, 1, 1, 1,
       0
FROM (SELECT M_IDX FROM dbo.MODULES) m
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SYSDD d
    WHERE LTRIM(RTRIM(d.USER_ID)) = 'larry' AND d.M_IDX = m.M_IDX
);
GO

-- ---------------------------------------------------------------- 报表权限
-- 报表可见性由**归属模块的 REPORT_TAG** 决定（个人 SYSDD 优先，否则组 SYSDH 取或），
-- 没有报表级 override；上面逐模块写入的全开个人权限已覆盖它。
-- SYSDD_REPORT 只承载报表中心的用户状态（收藏/排序/最近使用），不预置。
GO

-- 系统参数（SYSSS）已改为按模块 + 参数键的纵向表，参数行随元数据种子发布
-- （见 db/bootstrap/20_metadata.sql），此处不再单独写入。

PRINT '初始账号已就绪：admin / admin（管理员）、larry / larry（普通用户），请立即修改口令';
GO

/*
 * 更换初始口令：在应用中登录后使用「修改密码」功能，
 * 或用下面的方式生成哈希后直接写库（PowerShell 7）：
 *
 *   $salt = [byte[]]::new(12)
 *   [System.Security.Cryptography.RandomNumberGenerator]::Fill($salt)
 *   $key = [System.Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2(
 *              '你的口令', $salt, 210000,
 *              [System.Security.Cryptography.HashAlgorithmName]::SHA256, 24)
 *   $buf = [byte[]]::new(36)
 *   [Buffer]::BlockCopy($salt, 0, $buf, 0, 12)
 *   [Buffer]::BlockCopy($key,  0, $buf, 12, 24)
 *   'v1' + [Convert]::ToBase64String($buf)
 *
 *   UPDATE dbo.SYSDL SET USER_PWD = N'<上面输出的值>' WHERE LTRIM(RTRIM(USER_ID)) = 'admin';
 */
