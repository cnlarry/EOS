-- ============================================================================
-- EOS.ERP migration 206: 打印报表可指定自己的版式（拣货单 / 上架单）
-- ----------------------------------------------------------------------------
-- 背景：打印版式原先按**模块**解析（formatId = 模块号），而打印对话框里的多个"报表"
-- 共用同一张版式。因此同一张送货单没法既印"送货单"（给客户、含价）又印"拣货单"
-- （给现场、只列料号/品名/数量/库位/批号）。ADR-014 登记的"拣货单 / 上架单带位置"
-- 正是卡在这里。
--
-- 处置：REPORT 增列 FORMAT_ID（可空，空 = 沿用模块默认版式），并登记两条报表：
--   1406 送货单 → 拣货单（版式包 1406-pick）
--   1607 收料单 → 上架单（版式包 1607-putaway）
-- 两个版式包随代码部署（EOS.API/ReportFormats/），此处只做绑定。
--
-- 影响面：新报表的可见性跟随模块级 REPORT_TAG 的既有规则（无 override 行 = 可见），
-- 权限可事后在"用户权限设定"里收紧；既有报表的 FORMAT_ID 保持 NULL，打印行为不变。
--
-- 幂等：列存在性判定 + 按 REPORT_ID 的 UPSERT；重复执行不产生第二行、不改既有取值。
-- 回滚：DROP COLUMN FORMAT_ID 并删除两条报表行（`COP_Send_PICK` / `PUR_Receive_PUTAWAY`）。
--
-- 注：ALTER 与随后的引用必须分批（同批内新列还不可见），故用 GO 分隔。
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
    THROW 52740, @GUARD_MESSAGE, 1;

/* ---------- 1. REPORT.FORMAT_ID ---------- */
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.REPORT') AND name = N'FORMAT_ID')
BEGIN
    ALTER TABLE dbo.REPORT ADD FORMAT_ID NVARCHAR(64) NULL;
    PRINT N'== REPORT.FORMAT_ID 已新增 ==';
END
ELSE
    PRINT N'== REPORT.FORMAT_ID 已存在，跳过 ==';
GO

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

/* 格式包编号的形态守卫：与 ReportFormatRepository 的白名单一致（^[A-Za-z0-9_-]{1,64}$），
   编号进的是文件路径，形状不对就不该落库。 */
IF EXISTS (SELECT 1 FROM dbo.REPORT
           WHERE FORMAT_ID IS NOT NULL
             AND (LEN(FORMAT_ID) > 64 OR FORMAT_ID LIKE N'%[^A-Za-z0-9_-]%'))
    THROW 52741, N'REPORT.FORMAT_ID 存在不合法取值（长度 > 64 或含白名单外字符），迁移中止。', 1;

/* ---------- 2. 登记两条现场作业报表 ---------- */
DECLARE @targets TABLE (REPORT_ID NVARCHAR(50) PRIMARY KEY, REPORT_NAME NVARCHAR(100) NOT NULL, MODULE_ID INT NOT NULL, FORMAT_ID NVARCHAR(64) NOT NULL);
INSERT INTO @targets (REPORT_ID, REPORT_NAME, MODULE_ID, FORMAT_ID) VALUES
    (N'COP_Send_PICK',        N'拣货单', 1406, N'1406-pick'),
    (N'PUR_Receive_PUTAWAY',  N'上架单', 1607, N'1607-putaway');

/* 前提守卫：模块必须是启用的单据模块（否则报表挂上去也没人点得到） */
IF EXISTS (SELECT 1 FROM @targets t WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = t.MODULE_ID AND m.M_TAG = 1))
    THROW 52742, N'目标模块不存在或未启用，迁移中止。', 1;

MERGE dbo.REPORT AS T
USING (SELECT REPORT_ID, REPORT_NAME, MODULE_ID, FORMAT_ID FROM @targets) AS S
    ON T.REPORT_ID = S.REPORT_ID
WHEN MATCHED THEN
    UPDATE SET T.REPORT_NAME = S.REPORT_NAME, T.R_M_IDX = S.MODULE_ID, T.FORMAT_ID = S.FORMAT_ID,
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (REPORT_ID, REPORT_NAME, R_M_IDX, FORMAT_ID, IS_DEFAULT, REMARK, CONFIRM_TAG)
    VALUES (S.REPORT_ID, S.REPORT_NAME, S.MODULE_ID, S.FORMAT_ID, 0, N'现场作业单据（版式随代码部署）', 0);

/* ---------- 3. 收口断言 ---------- */
IF EXISTS (SELECT 1 FROM @targets t
           WHERE NOT EXISTS (SELECT 1 FROM dbo.REPORT r
                             WHERE r.REPORT_ID = t.REPORT_ID AND r.R_M_IDX = t.MODULE_ID AND r.FORMAT_ID = t.FORMAT_ID))
    THROW 52743, N'现场作业报表未登记或版式未绑定，迁移中止。', 1;

/* 默认报表不能被顶掉：既有默认报表的 FORMAT_ID 必须仍为空、IS_DEFAULT 不变 */
IF EXISTS (SELECT 1 FROM dbo.REPORT WHERE IS_DEFAULT = 1 AND FORMAT_ID IS NOT NULL)
    THROW 52744, N'默认报表被绑定了专属版式，与预期不符，迁移中止。', 1;

PRINT N'== 拣货单（1406-pick）/ 上架单（1607-putaway）已登记到 1406 / 1607 ==';
