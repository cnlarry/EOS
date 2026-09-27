-- ============================================================================
-- EOS.ERP migration 129: 1413 送货单回执 —— "送/退货已有回执"校验入目录（批 4：退役 `cop-callback` 族）
-- ----------------------------------------------------------------------------
-- 退役对象：`DomainRuleMap[1413] = "cop-callback"` → `CopDomainRules.CopCallbackAfterSaveAsync`：
--     SELECT c.SERIAL_NO FROM dbo.COP_SEND_D s
--     INNER JOIN dbo.COP_CALLBACK_D c
--       ON s.SEND_TYPE=c.S_R_TYPE AND s.SEND_NO=c.S_R_NO AND s.SERIAL_NO=c.S_R_SERIAL_NO
--     WHERE c.CALLBACK_TYPE=@T AND c.CALLBACK_NO=@N AND ISNULL(s.CALLBACK_NO,'')<>''
--     UNION ALL
--     SELECT c.SERIAL_NO FROM dbo.COP_RETURN_D s
--     INNER JOIN dbo.COP_CALLBACK_D c
--       ON s.RETURN_TYPE=c.S_R_TYPE AND s.RETURN_NO=c.S_R_NO AND s.SERIAL_NO=c.S_R_SERIAL_NO
--     WHERE c.CALLBACK_TYPE=@T AND c.CALLBACK_NO=@N AND ISNULL(s.CALLBACK_NO,'')<>'';
--   命中即拒绝，文案 N'以下序号项送、退货已有回执\r\n' + 本单明细序号（每行四空格、行间 CRLF）。
-- 目录承接：SAVE 期 `reference-exists`（一条规则两条 check，任一命中即按该 check 的文案拒绝）——
--   每条 check 用 `join` 把被引用的送货/退货明细行按 (单别,单号,序号) 与**本单明细列** S_R_* 关联，
--   并用**被引用行的闭式条件** `refCondition` 表达"该行已有回执号"（`CALLBACK_NO` 去空格后非空）；
--   命中形态是 EXISTS + 条件（与"引用必须存在"的 NOT EXISTS 形态相反），正是既有实现的反向断言；
--   `lineField:"SERIAL_NO"` 取的是**本单明细**序号（编译为 `D.SERIAL_NO`），与既有实现取 `c.SERIAL_NO` 一致；
--   文案尾部补四空格以复刻既有实现逐行 `SERIAL_NO + " "`（`maxRows` 取上限 100；既有实现不限行数）。
-- 说明：1413 的 `ERROR_NO_SAVE=1` 但该判据本身**无门控**（旧 C# 不读开关），故此处不配 `switch`。
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
    THROW 50000, @GUARD_MESSAGE, 1;

DECLARE @Message NVARCHAR(400) = N'以下序号项送、退货已有回执' + CHAR(13) + CHAR(10) + N'{ROWS}    ';
DECLARE @Params NVARCHAR(MAX) =
    N'{"checks":['
    + N'{"refTable":"COP_SEND_D",'
    + N'"join":[{"target":"SEND_TYPE","source":{"scope":"DETAIL","field":"S_R_TYPE"}},'
    + N'{"target":"SEND_NO","source":{"scope":"DETAIL","field":"S_R_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"S_R_SERIAL_NO"}}],'
    + N'"refCondition":{"logic":"AND","items":[{"type":"blank","field":{"scope":"TARGET","field":"CALLBACK_NO"},"negate":true}]},'
    + N'"lineField":"SERIAL_NO","maxRows":100,"message":"以下序号项送、退货已有回执\r\n{ROWS}    "},'
    + N'{"refTable":"COP_RETURN_D",'
    + N'"join":[{"target":"RETURN_TYPE","source":{"scope":"DETAIL","field":"S_R_TYPE"}},'
    + N'{"target":"RETURN_NO","source":{"scope":"DETAIL","field":"S_R_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"S_R_SERIAL_NO"}}],'
    + N'"refCondition":{"logic":"AND","items":[{"type":"blank","field":{"scope":"TARGET","field":"CALLBACK_NO"},"negate":true}]},'
    + N'"lineField":"SERIAL_NO","maxRows":100,"message":"以下序号项送、退货已有回执\r\n{ROWS}    "}]}';

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
           WHERE MODULE_ID = 1413 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'reference-exists')
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
                   WHERE MODULE_ID = 1413 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'reference-exists'
                     AND PARAM_STRUCT = @Params AND ISNULL(MESSAGE, N'') = @Message)
        THROW 50001, N'1413 SAVE 期 reference-exists 已存在但与预期参数不一致（漂移），迁移中止。', 1;
    PRINT N'1413 回执校验已存在且参数一致，跳过。';
END
ELSE
BEGIN
    INSERT INTO dbo.MODULE_VALIDATION_RULE
        (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES
        (1413, N'SAVE', 1, N'reference-exists', 1, @Params, @Message,
         N'送货回执：送/退货行不得已有回执', N'P_COP_CALLBACK', N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());
    PRINT N'1413 SAVE 期回执校验已播种（reference-exists + 被引用行条件）。';
END

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE MODULE_ID = 1413 AND STAGE = N'SAVE' AND ENABLED = 1
                 AND PARAM_STRUCT LIKE N'%refCondition%')
    THROW 50002, N'1413 缺少启用的回执校验规则，迁移中止。', 1;
