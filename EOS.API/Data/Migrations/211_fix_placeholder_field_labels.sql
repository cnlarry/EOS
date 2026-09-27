/*------------------------------------------------------------------
  字段元数据订正：把被写成文本 'NULL' 的 FIELDS.F_DESC 还原成真正的中文名。

  来历：早期一次性元数据补齐脚本（docs/migrations/archive/eos-doc-metadata-02）在输出
  「SQL NULL」时写成了字符串 'NULL'，共 280 行（LAST_UPDATE_BY='EOS-DOC-GEN'，2026-08-09）。
  读取侧的兜底是 COALESCE(NULLIF(F_DESC,''), F_ID)——空串会退到字段代号，而 'NULL' 是非空值，
  兜底拦不住，于是统一表单/列表上出现字面量 "NULL" 的标签。

  还原来源按可靠性分三档：
    1. F_REMARK：同一批脚本写入的既有实现列说明（真名被误放进备注列），210 行；
    2. 备份表镜像源表：MODULES_Backup_ADR004 / SYSDL_Backup_ADR004 是 MODULES / SYSDL 的
       迁移前备份，列名一一对应，取源表 F_DESC；源表已退役或无对应列的三列
       （UPDATE_SP / AFTERSAVE_SP / G_IDX）按既有实现表单标签补齐，69 行；
    3. 单列按语义补：SAM_OUT_D.IN_SERIAL_NO 与 IN_TYPE/IN_NO 同组，取「入库项次」，1 行。

  同时修正 PRODUCT.CONFIRM_TAG 的复合格角色：它是批核状态位，被配成「批核人」的从字段
  （FORM_CELL_ROLE=2）后，表单只渲染主字段标签，批核状态永远出不了自己的标签。

  幂等：所有更新都以 F_DESC = 'NULL' 为命中条件，重复执行不再命中。
------------------------------------------------------------------*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 51200, @GUARD, 1;

DECLARE @Now DATETIME = SYSDATETIME();
DECLARE @By NVARCHAR(20) = N'EOS-FIELD-LABEL';
DECLARE @Remarks INT = 0, @ModuleBackup INT = 0, @SysdlBackup INT = 0, @Single INT = 0;

/* ---------- 1. 有 F_REMARK 的：用既有实现列说明还原 ---------- */
UPDATE dbo.FIELDS
   SET F_DESC = LTRIM(RTRIM(F_REMARK)),
       LAST_UPDATE_BY = @By,
       LAST_UPDATE_DATE = @Now
 WHERE F_DESC = N'NULL'
   AND NULLIF(LTRIM(RTRIM(F_REMARK)), N'') IS NOT NULL;
SET @Remarks = @@ROWCOUNT;
PRINT N'== 由 F_REMARK 还原 ' + CONVERT(NVARCHAR(10), @Remarks) + N' 行 ==';

/* ---------- 2a. MODULES_Backup_ADR004：镜像 MODULES 的字段名 ---------- */
/* FILTER 不跟随镜像：源表该列的 F_DESC 是占位文本 '&nbsp;'，不是真名，单独按下表补齐。 */
UPDATE b
   SET b.F_DESC = s.F_DESC,
       b.LAST_UPDATE_BY = @By,
       b.LAST_UPDATE_DATE = @Now
  FROM dbo.FIELDS b
  JOIN dbo.FIELDS s
    ON LTRIM(RTRIM(s.T_ID)) = N'MODULES'
   AND LTRIM(RTRIM(s.F_ID)) = LTRIM(RTRIM(b.F_ID))
 WHERE LTRIM(RTRIM(b.T_ID)) = N'MODULES_Backup_ADR004'
   AND b.F_DESC = N'NULL'
   AND LTRIM(RTRIM(b.F_ID)) <> N'FILTER';
SET @ModuleBackup = @@ROWCOUNT;

/* 源表已无此三列，或源表无对应列：按既有实现表单标签补齐 */
UPDATE dbo.FIELDS
   SET F_DESC = v.Label, LAST_UPDATE_BY = @By, LAST_UPDATE_DATE = @Now
  FROM dbo.FIELDS f
  JOIN (VALUES
        (N'MODULES_Backup_ADR004', N'FILTER',       N'主表过滤条件'),
        (N'MODULES_Backup_ADR004', N'UPDATE_SP',    N'数据更新存储过程'),
        (N'MODULES_Backup_ADR004', N'AFTERSAVE_SP', N'存盘后执行存储过程'),
        (N'SYSDL_Backup_ADR004',   N'G_IDX',        N'默认组'),
        (N'SAM_OUT_D',             N'IN_SERIAL_NO', N'入库项次'))
       AS v(TableId, FieldId, Label)
    ON LTRIM(RTRIM(f.T_ID)) = v.TableId
   AND LTRIM(RTRIM(f.F_ID)) = v.FieldId
 WHERE f.F_DESC = N'NULL';
SET @Single = @@ROWCOUNT;

/* ---------- 2b. SYSDL_Backup_ADR004：镜像 SYSDL 的字段名 ---------- */
UPDATE b
   SET b.F_DESC = s.F_DESC,
       b.LAST_UPDATE_BY = @By,
       b.LAST_UPDATE_DATE = @Now
  FROM dbo.FIELDS b
  JOIN dbo.FIELDS s
    ON LTRIM(RTRIM(s.T_ID)) = N'SYSDL'
   AND LTRIM(RTRIM(s.F_ID)) = LTRIM(RTRIM(b.F_ID))
 WHERE LTRIM(RTRIM(b.T_ID)) = N'SYSDL_Backup_ADR004'
   AND b.F_DESC = N'NULL';
SET @SysdlBackup = @@ROWCOUNT;

PRINT N'== 备份表镜像还原 ' + CONVERT(NVARCHAR(10), @ModuleBackup + @SysdlBackup)
      + N' 行，按语义单列补齐 ' + CONVERT(NVARCHAR(10), @Single) + N' 行 ==';

/* ---------- 3. 批核状态位退出复合格：恢复自己的标签与独立格 ---------- */
UPDATE dbo.FIELDS
   SET FORM_CELL_ROLE = 0,
       FORM_CELL_GROUP = NULL,
       LAST_UPDATE_BY = @By,
       LAST_UPDATE_DATE = @Now
 WHERE LTRIM(RTRIM(T_ID)) = N'PRODUCT'
   AND LTRIM(RTRIM(F_ID)) = N'CONFIRM_TAG'
   AND (ISNULL(FORM_CELL_ROLE, 0) <> 0 OR NULLIF(LTRIM(RTRIM(FORM_CELL_GROUP)), N'') IS NOT NULL);
PRINT N'== PRODUCT.CONFIRM_TAG 退出复合格 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

/* ---------- 收口断言 ---------- */
IF @Remarks + @ModuleBackup + @SysdlBackup + @Single = 0
   AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS WHERE F_DESC = N'NULL')
    PRINT N'== 无待订正的占位字段名（重复执行）==';

IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE F_DESC = N'NULL')
    THROW 51201, N'字段元数据：仍有 F_DESC 为文本 ''NULL'' 的行未订正。', 1;

IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE LTRIM(RTRIM(T_ID)) = N'PRODUCT' AND LTRIM(RTRIM(F_ID)) = N'CONFIRM_TAG'
           AND ISNULL(FORM_CELL_ROLE, 0) <> 0)
    THROW 51202, N'字段元数据：PRODUCT.CONFIRM_TAG 仍在复合格内，批核状态标签不可见。', 1;

IF EXISTS (SELECT 1 FROM dbo.FIELDS f
           WHERE LTRIM(RTRIM(f.T_ID)) IN (N'MODULES_Backup_ADR004', N'SYSDL_Backup_ADR004')
             AND NULLIF(LTRIM(RTRIM(f.F_DESC)), N'') IS NULL)
    THROW 51203, N'字段元数据：备份表存在无字段名的行。', 1;

PRINT N'字段元数据订正完成：占位字段名已还原，PRODUCT 批核状态恢复独立标签。';
GO
