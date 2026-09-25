-- 退役版式「微调」权限位：SYSDD / SYSDH 的 FORM_ADJUST_TAG。
--
-- 依据：版式设计权只保留一档（FORM_DESIGN_TAG）。微调档服务的"客户维护人员"角色尚不存在，
-- 且它不是安全边界——它禁止增删字段却允许改顺序与占位，破坏性操作照样能做，多一档只多一套
-- 受限模式与一套保存期基线校验。该位自建立以来全库零使用（SYSDD/SYSDH 置 1 的行数为 0），
-- 其唯一消费方（报表版式设计器"微调模式"）已随本次改动一并收敛。
--
-- 与 241 的差别：241 删过一次，运行中的代码与门禁 SQL 仍在读它，随即由 242 还原；本次删列前
-- 读点已全部摘除（FormDesignPermissionResolver / LayoutDesignerMode / LayoutDesignerController /
-- 前端设计器与用例），且运行实例的二进制早于本批改动，故随下一次重启由 DbUp 落地。
--
-- 幂等：逐列判存在后再删；重复执行不报错。
-- 回滚：ALTER TABLE ... ADD FORM_ADJUST_TAG bit NULL（列可重建，数据无价值——全库 0 行置 1）。

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- ① 先摘掉挂在目标列上的扩展属性（列注释）
DECLARE @dropProps NVARCHAR(MAX) = N'';
SELECT @dropProps = @dropProps
    + N'EXEC sys.sp_dropextendedproperty @name = N''' + REPLACE(ep.name, N'''', N'''''')
    + N''', @level0type = N''SCHEMA'', @level0name = N''dbo'''
    + N', @level1type = N''TABLE'', @level1name = N''' + t.name + N''''
    + N', @level2type = N''COLUMN'', @level2name = N''' + c.name + N''';' + CHAR(10)
FROM sys.extended_properties ep
JOIN sys.tables t ON t.object_id = ep.major_id
JOIN sys.columns c ON c.object_id = ep.major_id AND c.column_id = ep.minor_id
WHERE ep.minor_id > 0
  AND t.name IN (N'SYSDD', N'SYSDH')
  AND c.name = N'FORM_ADJUST_TAG';
IF LEN(@dropProps) > 0
    EXEC sp_executesql @dropProps;

-- ② 再摘掉可能绑定在目标列上的默认约束（031 建列时带 DF_*_FORM_ADJUST，242 重建为可空无默认；
--    两种形态都要能删，否则 DROP COLUMN 会因依赖而失败）
DECLARE @dropDefaults NVARCHAR(MAX) = N'';
SELECT @dropDefaults = @dropDefaults
    + N'ALTER TABLE dbo.' + t.name + N' DROP CONSTRAINT [' + dc.name + N'];' + CHAR(10)
FROM sys.default_constraints dc
JOIN sys.tables t ON t.object_id = dc.parent_object_id
JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
WHERE t.name IN (N'SYSDD', N'SYSDH') AND c.name = N'FORM_ADJUST_TAG';
IF LEN(@dropDefaults) > 0
    EXEC sp_executesql @dropDefaults;

-- ③ 删列
IF COL_LENGTH('dbo.SYSDD', 'FORM_ADJUST_TAG') IS NOT NULL
    ALTER TABLE dbo.SYSDD DROP COLUMN FORM_ADJUST_TAG;
IF COL_LENGTH('dbo.SYSDH', 'FORM_ADJUST_TAG') IS NOT NULL
    ALTER TABLE dbo.SYSDH DROP COLUMN FORM_ADJUST_TAG;

-- ④ 后置自证：目标列消失，且同族的完整设计位必须还在
IF COL_LENGTH('dbo.SYSDD', 'FORM_ADJUST_TAG') IS NOT NULL
    OR COL_LENGTH('dbo.SYSDH', 'FORM_ADJUST_TAG') IS NOT NULL
    THROW 55000, N'SYSDD/SYSDH.FORM_ADJUST_TAG 未被删除。', 1;
IF COL_LENGTH('dbo.SYSDD', 'FORM_DESIGN_TAG') IS NULL
    OR COL_LENGTH('dbo.SYSDH', 'FORM_DESIGN_TAG') IS NULL
    THROW 55001, N'不该删的列被删了：SYSDD/SYSDH.FORM_DESIGN_TAG 必须保留。', 1;

COMMIT TRANSACTION;

SELECT N'FORM_ADJUST_TAG 残留' AS CHECK_NAME,
       (CASE WHEN COL_LENGTH('dbo.SYSDD', 'FORM_ADJUST_TAG') IS NULL THEN 0 ELSE 1 END)
     + (CASE WHEN COL_LENGTH('dbo.SYSDH', 'FORM_ADJUST_TAG') IS NULL THEN 0 ELSE 1 END) AS CNT;
