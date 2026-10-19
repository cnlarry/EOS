-- ============================================================================
-- EOS.ERP migration 430: 考勤作业整组菜单停用，并修正父目录名称
-- ----------------------------------------------------------------------------
-- 背景：1808 原名「生成验厂数据」，其下只有 180654 考勤模拟生成、180659 考勤真实
-- 抽取生成、180505 依薪资调整考勤三个作业，名称与内容不符。当前库里考勤业务尚未
-- 上线：HRM_PLAN_M/D、HRM_DIARY、HRM_WAGE_M/D 都是 0 行，HR_SETUP 的 WAGE_* 调整
-- 项目也未配置——生成作业只能建出空行，调整作业点执行必然返回 WAGE_SETUP_MISSING。
--
-- 处置：M_TAG=0 停用菜单。这一位就是菜单管理页的「停用」（NavigationRepository 只
-- 读取 M_TAG=1 的模块，停用后也不再下发该模块的浏览权限键）。页面组件、前端路由与
-- 作业端点全部保留，考勤业务上线时在菜单管理里把 1808 与三个子模块一并启用即可恢复，
-- 授权行不需要重配。
--
-- 1808 的子项全部停用后只剩一个"点进去是占位页"的空目录，故整组一并停用；名称同时
-- 修正为「考勤作业」。
--
-- 幂等：按当前值判断，可重复执行。
-- ============================================================================

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 51970, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

IF (SELECT COUNT(*) FROM dbo.MODULES WHERE M_IDX IN (1808, 180654, 180659, 180505)) <> 4
    THROW 51971, N'考勤作业的四个模块行不齐全，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 1808 AND M_P_IDX = 18)
    THROW 51972, N'1808 不在人事根节点（18）下，迁移中止。', 1;

IF (SELECT COUNT(*) FROM dbo.MODULES WHERE M_P_IDX = 1808 AND M_IDX IN (180654, 180659, 180505)) <> 3
    THROW 51973, N'1808 下的三个考勤作业不齐全，迁移中止。', 1;

BEGIN TRANSACTION;

UPDATE dbo.MODULES
   SET M_DESC = N'考勤作业',
       M_TAG = 0,
       REMARK = N'目录模块，含 3 个考勤作业；原名「生成验厂数据」与内容不符，已更名。考勤业务未上线，整组菜单停用（M_TAG=0）；上线时与三个子模块一并启用。',
       LAST_UPDATE_BY = N'430',
       LAST_UPDATE_DATE = GETDATE()
 WHERE M_IDX = 1808
   AND (M_DESC <> N'考勤作业' OR ISNULL(M_TAG, 1) <> 0);
PRINT N'== 1808 → 「考勤作业」并停用：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

UPDATE dbo.MODULES
   SET M_TAG = 0,
       REMARK = N'主表：HRM_DIARY；考勤业务未上线，菜单停用（M_TAG=0），页面与端点保留。',
       LAST_UPDATE_BY = N'430',
       LAST_UPDATE_DATE = GETDATE()
 WHERE M_IDX = 180654 AND ISNULL(M_TAG, 1) <> 0;
PRINT N'== 180654 菜单停用：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

UPDATE dbo.MODULES
   SET M_TAG = 0,
       REMARK = N'主表：HRM_DIARY；考勤业务未上线，菜单停用（M_TAG=0），页面与端点保留。',
       LAST_UPDATE_BY = N'430',
       LAST_UPDATE_DATE = GETDATE()
 WHERE M_IDX = 180659 AND ISNULL(M_TAG, 1) <> 0;
PRINT N'== 180659 菜单停用：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

UPDATE dbo.MODULES
   SET M_TAG = 0,
       REMARK = N'主表：HRM_WAGE_M；考勤业务未上线且 HR_SETUP 未配置薪资调整项目，菜单停用（M_TAG=0），页面与端点保留。',
       LAST_UPDATE_BY = N'430',
       LAST_UPDATE_DATE = GETDATE()
 WHERE M_IDX = 180505 AND ISNULL(M_TAG, 1) <> 0;
PRINT N'== 180505 菜单停用：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

DECLARE @stillVisible NVARCHAR(400) = N'';
SELECT @stillVisible = @stillVisible + CONVERT(NVARCHAR(20), M_IDX) + N','
  FROM dbo.MODULES
 WHERE M_IDX IN (1808, 180654, 180659, 180505)
   AND ISNULL(M_TAG, 1) <> 0;
IF @stillVisible <> N''
    THROW 51974, N'考勤作业仍有菜单启用行，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 1808 AND M_DESC = N'考勤作业')
    THROW 51975, N'1808 未更名为「考勤作业」，迁移中止。', 1;

COMMIT TRANSACTION;
PRINT N'== 收口：考勤作业整组菜单停用（页面与端点保留） ==';
