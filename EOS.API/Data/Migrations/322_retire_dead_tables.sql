-- ============================================================================
-- EOS.ERP migration 322: 退役零引用死表（SYSTEMP / TASK）
-- ----------------------------------------------------------------------------
-- "A 档死表"里**真正能单独删的两张**（2026-10-06 拍板）：
--
--   SYSTEMP  旧「组 × 模块」权限副本（279 行）。全仓零引用（grep dbo.SYSTEMP = 0）；
--            组权限的现代真源是 SYSDH / SYSDH_BUTTON。
--   TASK     旧任务表（0 行），只在 MenuAdminRepository 的"改模块编号"级联名单里被点名，
--            无任何业务读写 ⇒ 表与级联引用一并退场。它带 25 行 FIELDS 元数据（无人挂载的
--            表字段登记），随表一起删。
--
-- **同批被排除的两张**（取证结论与初判相反，故不动）：
--   SYS_WORK_TASK  不是死表：它是**活模块 2308 工作任务记录**的主表（M_TAG=1、M_URL=/workbench、
--                  11 份已发布快照、27 行版式、权限行齐全）——表里 0 行只是没人录数据。
--                  要删它得先退役 2308，属另一刀。
--   SYSQR_DA       挂在 **2205 报表过滤条件设置**（MASTER_TABLE=SYSQR_DA，另有 13 行 FIELDS、
--                  11 行 SYSQL_DEFAULT、17 行 SYSQL_FIELDS 元数据）上；该模块 M_URL 空、M_TAG=0、
--                  页面早在迁移 025 就下线了，但"退役模块 + 连带元数据"是模块级决定，属 A 档其它。
--   SYSDF          11134 行历史审计（WorkbenchAuditWriter 明文"history-only, never written"），
--                  保留作历史留档，不删。
--
-- 前置自证四道：表还在、没有模块挂在上面、没有视图/过程/函数引用、没有入向外键。
-- 命名全大写；**非幂等**（表不存在即报错，与 320/321 同口径）。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- ① 前置自证：两张表都得在
IF OBJECT_ID(N'dbo.SYSTEMP', N'U') IS NULL OR OBJECT_ID(N'dbo.TASK', N'U') IS NULL
    THROW 53200, N'SYSTEMP / TASK 不齐：本迁移的前提不成立（库结构已变或已删过）。', 1;

-- ② 前置自证：不得有模块挂在这两张表上（挂在上面 = 先做模块退役，不能单删表）
IF EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK)
           WHERE LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))) IN (N'SYSTEMP', N'TASK')
              OR LTRIM(RTRIM(ISNULL(DETAIL_TABLE, N''))) IN (N'SYSTEMP', N'TASK'))
    THROW 53201, N'仍有模块以 SYSTEMP / TASK 为主表或副表：先退役该模块，再删表。', 1;

-- ③ 前置自证：不得被任何视图 / 过程 / 函数引用
IF EXISTS (SELECT 1 FROM sys.sql_expression_dependencies
           WHERE referenced_id IN (OBJECT_ID(N'dbo.SYSTEMP'), OBJECT_ID(N'dbo.TASK')))
    THROW 53202, N'SYSTEMP / TASK 仍被视图或存储过程引用：先摘依赖，再删表。', 1;

-- ④ 前置自证：不得有入向外键
IF EXISTS (SELECT 1 FROM sys.foreign_keys
           WHERE referenced_object_id IN (OBJECT_ID(N'dbo.SYSTEMP'), OBJECT_ID(N'dbo.TASK')))
    THROW 53203, N'SYSTEMP / TASK 仍被外键引用：先摘外键，再删表。', 1;

-- ⑤ 行数留痕（执行日志即记录：SYSTEMP 是旧权限副本的最后一份留档）
DECLARE @trail NVARCHAR(400) = N'';
SELECT @trail = N'SYSTEMP ' + CONVERT(nvarchar(20), (SELECT COUNT(*) FROM dbo.SYSTEMP))
              + N' 行；TASK ' + CONVERT(nvarchar(20), (SELECT COUNT(*) FROM dbo.TASK)) + N' 行';
PRINT N'== 退役死表（行数留痕）：' + @trail;

-- ⑥ 删掉 TASK 的字段元数据行（它描述的表即将不存在；SYSTEMP 无字段登记）
DELETE FROM dbo.FIELDS WHERE T_ID = N'TASK';

-- ⑦ 删表
DROP TABLE dbo.SYSTEMP;
DROP TABLE dbo.TASK;

-- ⑧ 后置自证：两张表必须消失
IF OBJECT_ID(N'dbo.SYSTEMP', N'U') IS NOT NULL OR OBJECT_ID(N'dbo.TASK', N'U') IS NOT NULL
    THROW 53210, N'SYSTEMP / TASK 未被删除。', 1;

IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = N'TASK')
    THROW 53211, N'FIELDS 里仍留着 TASK 的元数据行。', 1;

-- ⑨ 后置自证：不该被顺手删掉的表必须在（同批排除的那几张）
IF OBJECT_ID(N'dbo.SYS_WORK_TASK', N'U') IS NULL
    OR OBJECT_ID(N'dbo.SYSQR_DA', N'U') IS NULL
    OR OBJECT_ID(N'dbo.SYSDF', N'U') IS NULL
    OR OBJECT_ID(N'dbo.SYSDH', N'U') IS NULL
    THROW 53212, N'不该删的表被删了（SYS_WORK_TASK 是活模块 2308 的主表；SYSQR_DA / SYSDF / SYSDH 必须保留）。', 1;

COMMIT TRANSACTION;

PRINT N'== 死表退役完成：SYSTEMP / TASK 已删除（SYS_WORK_TASK 是活模块主表、SYSQR_DA 待随 2205 处理，均未动）==';
