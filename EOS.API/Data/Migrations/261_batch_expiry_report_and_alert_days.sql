-- ============================================================================
-- EOS.ERP migration 259: 临期/过期报表 + 近效期提前天数参数
-- ----------------------------------------------------------------------------
-- 三件事，都是"报表要能跑起来"的前置：
--   ① 系统参数 `110111|EXPIRY_ALERT_DAYS`（近效期预警提前天数）——报表与预警**读同一个数**；
--   ② `REPORT` 行（报表号 + 挂载模块 1303 料件库存资料）——报表目录、默认报表解析都读它；
--   ③ `SYSQR_DEFAULT` 两条筛选项——"是否含过期"与"只看不受管控"，
--      后者是**开档 3 之前把在库批次效期补齐**的作业清单（见 docs/plans/A5-批次效期-施工清单.md）。
--
-- 报表取数 SQL 在服务端注册表 `ReportAggregateRegistry`（不写新存储过程、不接受客户端 SQL）：
-- 本迁移只落"配置与参数"，不落任何查询逻辑。
--
-- 幂等：三处均按主键/业务键判存在；参数定义字段在写入前逐项自证（门禁 check-system-params 的判据同源）。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

BEGIN TRANSACTION;

-- ---------------------------------------------------------------- ① 近效期提前天数
IF NOT EXISTS (SELECT 1 FROM dbo.SYSSS WHERE OWNER_MODULE = 110111 AND PARAM_KEY = N'EXPIRY_ALERT_DAYS')
BEGIN
    INSERT INTO dbo.SYSSS (
        OWNER_MODULE, PARAM_KEY, PARAM_VALUE, VALUE_TYPE, DEFAULT_VALUE,
        GROUP_CODE, GROUP_LABEL, GROUP_SEQ, SEQ_NO, DESC_TEXT, EFFECT_SCOPE, REMARK,
        CREATE_PERSON, CREATE_DATE)
    VALUES (
        110111, N'EXPIRY_ALERT_DAYS', N'30', N'int', N'30',
        N'INVENTORY', N'库存与批次', 90, 10, N'近效期预警提前天数', N'immediate',
        N'临期清单与预警共用：剩余天数小于等于该值即列为临期。',
        -- CREATE_PERSON 是短列（与既有库的建单人列同宽），写全的迁移名会被截断报错
        N'mig-259', SYSDATETIME());
    PRINT N'== 新增系统参数 110111|EXPIRY_ALERT_DAYS（近效期提前天数，默认 30）==';
END
ELSE
    PRINT N'== 系统参数 EXPIRY_ALERT_DAYS 已存在，跳过 ==';

-- 定义字段自证：门禁（check-system-params）要求的六项必须齐备，缺一即中止
IF EXISTS (SELECT 1 FROM dbo.SYSSS
            WHERE OWNER_MODULE = 110111 AND PARAM_KEY = N'EXPIRY_ALERT_DAYS'
              AND (VALUE_TYPE NOT IN (N'bit', N'int', N'decimal', N'string')
                OR EFFECT_SCOPE NOT IN (N'immediate', N'restart')
                OR LTRIM(RTRIM(ISNULL(GROUP_CODE, N''))) = N''
                OR LTRIM(RTRIM(ISNULL(GROUP_LABEL, N''))) = N''
                OR LTRIM(RTRIM(ISNULL(DESC_TEXT, N''))) = N''
                OR ISNULL(GROUP_SEQ, 0) = 0))
    THROW 52160, N'EXPIRY_ALERT_DAYS 的参数定义不完整（类型/生效范围/分组/说明/分组顺序）。', 1;

-- ---------------------------------------------------------------- ② 报表行
IF NOT EXISTS (SELECT 1 FROM dbo.REPORT WHERE REPORT_ID = N'INV_Batch_Expiry_1')
BEGIN
    INSERT INTO dbo.REPORT (
        REPORT_ID, REPORT_NAME, R_M_IDX, Q_M_IDX, IS_DEFAULT, REMARK,
        CREATE_PERSON, CREATE_DATE)
    VALUES (
        -- 明细区的列宽/排序不落在这里：报表取数与输出列由服务端注册表给出
        N'INV_Batch_Expiry_1', N'批次效期与临期清单', 1303, 1303, 0,
        N'临期与过期批次清单：阈值取系统参数 EXPIRY_ALERT_DAYS，含"是否含过期"与"只看不受管控"两项筛选。',
        N'mig-259', SYSDATETIME());
    PRINT N'== 新增报表 INV_Batch_Expiry_1（模块 1303）==';
END
ELSE
    PRINT N'== 报表 INV_Batch_Expiry_1 已存在，跳过 ==';

IF NOT EXISTS (SELECT 1 FROM dbo.REPORT r JOIN dbo.MODULES m ON m.M_IDX = r.R_M_IDX
                WHERE r.REPORT_ID = N'INV_Batch_Expiry_1' AND r.R_M_IDX = 1303)
    THROW 52161, N'报表未按预期挂到模块 1303，目录与权限门都会对不上。', 1;

-- ---------------------------------------------------------------- ③ 报表筛选项
-- 形态照库存日报（139901）的固定单选行：F_TYPE='2' + F_VALUE 选项串 + PARA_NAME。
-- 序号 1/2 与注册表里两个参数的 SerialNo **一一对应**，错位就等于"筛选项和查询条件对不上"。
DECLARE @Conditions TABLE (SERIAL_NO SMALLINT NOT NULL PRIMARY KEY, F_DESC NVARCHAR(200) NOT NULL,
    F_VALUE NVARCHAR(200) NOT NULL, PARA_NAME NVARCHAR(100) NOT NULL, FILTER_JSON NVARCHAR(1000) NOT NULL);
INSERT INTO @Conditions (SERIAL_NO, F_DESC, F_VALUE, PARA_NAME, FILTER_JSON) VALUES
    (1, N'是否含过期', N'不含过期:0;含过期:1', N'@include_expired',
     N'{"logic":"AND","items":[{"field":"","op":"EQ","value":"{p.@include_expired}"}],"options":[{"label":"不含过期","value":"0"},{"label":"含过期","value":"1"}],"parameterName":"@include_expired"}'),
    (2, N'只看不受管控', N'否:0;是:1', N'@unmanaged_only',
     N'{"logic":"AND","items":[{"field":"","op":"EQ","value":"{p.@unmanaged_only}"}],"options":[{"label":"否","value":"0"},{"label":"是","value":"1"}],"parameterName":"@unmanaged_only"}');

INSERT INTO dbo.SYSQR_DEFAULT (M_IDX, SERIAL_NO, F_ID, F_TYPE, F_EXPR, F_DESC, F_VALUE, REMARK, PARA_NAME, FILTER_TEMPLATE)
SELECT 1303, c.SERIAL_NO, N'', N'2', N'', c.F_DESC, c.F_VALUE,
       N'批次效期与临期清单的筛选项（报表 INV_Batch_Expiry_1）', c.PARA_NAME, c.FILTER_JSON
FROM @Conditions c
WHERE NOT EXISTS (SELECT 1 FROM dbo.SYSQR_DEFAULT q WHERE q.M_IDX = 1303 AND q.SERIAL_NO = c.SERIAL_NO);

-- 后置自证：两条筛选项齐备，且序号与注册表的 SerialNo 对得上
IF EXISTS (SELECT 1 FROM @Conditions c
           WHERE NOT EXISTS (SELECT 1 FROM dbo.SYSQR_DEFAULT q
                              WHERE q.M_IDX = 1303 AND q.SERIAL_NO = c.SERIAL_NO
                                AND LTRIM(RTRIM(ISNULL(q.PARA_NAME, N''))) = c.PARA_NAME))
    THROW 52162, N'报表筛选项未按预期就位（序号或参数名不符）。', 1;

COMMIT TRANSACTION;

PRINT N'== 完成：近效期参数 + 报表行 + 两条筛选项 ==';
