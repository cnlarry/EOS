/*
 * 初始管理员账号
 *
 * 建立一个拥有全部模块权限的 admin 账号，以及它所依赖的最小组织结构
 * （一个公司、一个部门、一条员工记录）。
 *
 * 初始口令为 admin，仅用于首次登录。请在登录后立即通过「修改密码」更换，
 * 或执行本文件末尾注释中的语句写入自定义口令哈希。
 *
 * 幂等：可重复执行；已存在的记录不会被覆盖，权限行会补齐到最新模块清单。
 */

SET NOCOUNT ON;
GO

-- ---------------------------------------------------------------- 组织结构
IF NOT EXISTS (SELECT 1 FROM dbo.COMPANY WHERE LTRIM(RTRIM(COMPANY_ID)) = 'EOS')
    INSERT dbo.COMPANY (COMPANY_ID, NAME_CN, NAME_EN, SHORT_NAME_CN, SHORT_NAME_EN, CONFIRM_TAG)
    VALUES ('EOS', N'示例公司', N'Demo Company', N'示例', N'Demo', 1);
GO

IF NOT EXISTS (SELECT 1 FROM dbo.DEPT WHERE LTRIM(RTRIM(DEPT_ID)) = 'ADM')
    INSERT dbo.DEPT (DEPT_ID, DEPT_NAME, CONFIRM_TAG)
    VALUES ('ADM', N'系统管理', 1);
GO

IF NOT EXISTS (SELECT 1 FROM dbo.SYSDN WHERE LTRIM(RTRIM(EMP_ID)) = 'admin')
    INSERT dbo.SYSDN (EMP_ID, EMP_NAME, DEPT_ID, CONFIRM_TAG)
    VALUES ('admin', N'系统管理员', 'ADM', 1);
GO

-- ---------------------------------------------------------------- 账号
-- 口令哈希格式："v1" + Base64(盐[12] || PBKDF2-SHA256(口令, 盐, 210000 次迭代, 24 字节))
-- 下面的值对应口令 admin。
IF NOT EXISTS (SELECT 1 FROM dbo.SYSDL WHERE LTRIM(RTRIM(USER_ID)) = 'admin')
    INSERT dbo.SYSDL (USER_ID, USER_PWD, EMP_ID, ACTIVE_TAG, CONFIRM_TAG, REMARK)
    VALUES ('admin', N'v1Z6SJh2GJYChyu+14+n5FcDFhMVlPuK0HxKD8fcl1mkuCLJe3', 'admin', 1, 1,
            N'初始管理员账号，首次登录后请立即修改口令');
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
    OPERFLAG
)
SELECT 'admin', m.M_IDX, @execTag,
       1, 1, 1, 1, 1, 1, 1,
       1, 1, 1, 1,
       1, 1, 1, 1,
       1, 1, 1, 1,
       0
FROM (SELECT M_IDX FROM dbo.MODULES) m
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SYSDD d
    WHERE LTRIM(RTRIM(d.USER_ID)) = 'admin' AND d.M_IDX = m.M_IDX
);
GO

-- ---------------------------------------------------------------- 报表权限
-- 报表权限按「模块 + 报表」维度存放；为 admin 开放全部报表的查看/打印/导出。
-- REPORT.R_M_IDX 是报表归属模块；0/空 视为全局报表，挂到 0。
IF OBJECT_ID(N'dbo.SYSDD_REPORT', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.REPORT', N'U') IS NOT NULL
BEGIN
    INSERT dbo.SYSDD_REPORT (USER_ID, M_IDX, REPORT_ID, PREVIEW_TAG, PRINT_TAG, EXPORT_TAG, FAVORITE_TAG)
    SELECT 'admin', ISNULL(NULLIF(r.R_M_IDX, 0), 0), r.REPORT_ID, 1, 1, 1, 0
    FROM dbo.REPORT r
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.SYSDD_REPORT d
        WHERE LTRIM(RTRIM(d.USER_ID)) = 'admin'
          AND ISNULL(d.M_IDX, 0) = ISNULL(NULLIF(r.R_M_IDX, 0), 0)
          AND LTRIM(RTRIM(d.REPORT_ID)) = LTRIM(RTRIM(r.REPORT_ID))
    );
END
GO

-- 系统参数（SYSSS）已改为按模块 + 参数键的纵向表，参数行随元数据种子发布
-- （见 db/bootstrap/20_metadata.sql），此处不再单独写入。

PRINT '初始管理员已就绪：用户 admin / 口令 admin（请立即修改）';
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
