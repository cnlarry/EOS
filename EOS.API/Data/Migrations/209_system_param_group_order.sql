/*------------------------------------------------------------------
  系统参数的**分组显示顺序**：分组本身是数据（GROUP_CODE/GROUP_LABEL 已随迁移落库），
  顺序也必须是数据——否则新增分组要改代码，且页面只能按 GROUP_CODE 字母序排，
  与业务分区顺序无关（"其它"会排到第二位）。

  为什么单列一列：SEQ_NO 的语义是"组内排序"，表达不了组间顺序。GROUP_SEQ 按 10 递增，
  预留插队空间；未登记的新分组合法值取 900（排在已知分组之后），由门禁提醒补齐。

  读侧口径：页面按 (GROUP_SEQ, SEQ_NO, PARAM_KEY) 呈现。
------------------------------------------------------------------*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF COL_LENGTH(N'dbo.SYSSS', N'GROUP_SEQ') IS NULL
    ALTER TABLE dbo.SYSSS ADD GROUP_SEQ INT NOT NULL CONSTRAINT DF_SYSSS_GROUP_SEQ DEFAULT (900);
GO

/* 顺序对齐既有的分区阅读顺序：往来与账期 → MRP → 料件与版次 → 单据联动 → 采购 → 生产 →
   数量阈值 → 界面与查询 → 其它；考勤侧为 卡号解析 → 考勤日历 → 考勤其他 → 工资字段映射 → 其它 */
UPDATE dbo.SYSSS SET GROUP_SEQ = CASE GROUP_CODE
    WHEN N'PARTNER'        THEN 10
    WHEN N'MRP'            THEN 20
    WHEN N'PRODUCT'        THEN 30
    WHEN N'DOC_LINK'       THEN 40
    WHEN N'PURCHASE'       THEN 50
    WHEN N'PRODUCE'        THEN 60
    WHEN N'QTY_RULE'       THEN 70
    WHEN N'UI'             THEN 80
    WHEN N'ATT_CARD'       THEN 10
    WHEN N'ATT_CALENDAR'   THEN 20
    WHEN N'ATT_FLAG'       THEN 30
    WHEN N'WAGE_MAP'       THEN 40
    WHEN N'MISC'           THEN 900
    ELSE 900
END;
GO

/* 断言：每个分组都登记了顺序且同一归属模块内顺序不重复 */
IF EXISTS (SELECT 1 FROM dbo.SYSSS WHERE GROUP_SEQ = 0)
    THROW 51000, N'系统参数：存在未登记显示顺序的分组。', 1;

IF EXISTS (
    SELECT 1 FROM (SELECT DISTINCT OWNER_MODULE, GROUP_CODE, GROUP_SEQ FROM dbo.SYSSS) g
    GROUP BY OWNER_MODULE, GROUP_SEQ HAVING COUNT(*) > 1)
    THROW 51000, N'系统参数：同一归属模块下分组显示顺序重复。', 1;

PRINT N'系统参数分组显示顺序已登记（GROUP_SEQ）。';
GO
