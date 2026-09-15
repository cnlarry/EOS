-- ============================================================================
-- EOS.ERP migration 083: 重建 1606 采购单的 SAVE 引用校验（按 C# 判据重新推导）
-- ----------------------------------------------------------------------------
-- 原参数含三条断言（厂商存在 / 明细申购单存在 / 明细产品存在），与真实数据严重不符：
-- 8729 张采购单中 6746 张被判"明细申购单不存在"（其中 678 张明细申购单号为空）、
-- 146 张被判"产品不存在"，故迁移 081 先行停用。
--
-- 现按该模块的 C# 领域规则（PurPurchaseAfterSaveAsync）重新推导——其引用类判据只有一条：
--   SELECT TOP 1 1 FROM dbo.PUR_PURCHASE_M m JOIN dbo.SUPPLIER c ON c.SUPPLIER_ID=m.SUPPLIER_ID
--   WHERE m.PURCHASE_TYPE=@Type AND m.PURCHASE_NO=@No AND c.BUSINESS_TAG=0;
-- 即"厂商必须存在且未停止交易"（其余两条是计价有效期与预交日期，不是引用存在性）。
-- 失败文案与 C# 保持一致。
--
-- 幂等：按 模块+阶段+顺序 覆盖参数、文案与启用位。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 重建 1606 采购单引用校验 ==';

DECLARE @PARAM NVARCHAR(MAX) = N'{"checks":[{"refTable":"SUPPLIER",'
    + N'"refKey":{"scope":"MASTER","field":"SUPPLIER_ID"},'
    + N'"activeTag":{"field":"BUSINESS_TAG","expect":0},'
    + N'"message":"厂商编号不存在或已停止交易。"}]}';

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = 1606 AND STAGE = N'SAVE' AND SEQ = 1)
    UPDATE dbo.MODULE_VALIDATION_RULE
    SET VALIDATION_KEY = N'reference-exists', ENABLED = 1,
        PARAM_STRUCT = @PARAM, MESSAGE = N'厂商编号不存在或已停止交易。',
        REMARK = N'采购单厂商引用校验（按 C# 域规则判据重建；原申购单/产品断言与真实数据不符已移除）',
        SOURCE_REF = N'pur-purchase',
        LAST_UPDATE_BY = N'migration-083', LAST_UPDATE_DATE = SYSDATETIME()
    WHERE MODULE_ID = 1606 AND STAGE = N'SAVE' AND SEQ = 1;
ELSE
    INSERT INTO dbo.MODULE_VALIDATION_RULE
        (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)
    VALUES
        (1606, N'SAVE', 1, N'reference-exists', 1, @PARAM, N'厂商编号不存在或已停止交易。',
         N'采购单厂商引用校验（按 C# 域规则判据重建）', N'pur-purchase', N'migration-083', SYSDATETIME());

SELECT CONCAT(N'  module=', MODULE_ID, N' key=', VALIDATION_KEY, N' enabled=', ENABLED, N' msg=', ISNULL(MESSAGE, N'-')) AS SUMMARY
FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = 1606;

-- 影响面复核：按新判据，现存采购单中被判不通过的应有 0 张
SELECT CONCAT(N'  现存采购单=', (SELECT COUNT(*) FROM dbo.PUR_PURCHASE_M),
              N' 新判据下不通过=', (SELECT COUNT(*) FROM dbo.PUR_PURCHASE_M M
                  WHERE NOT EXISTS (SELECT 1 FROM dbo.SUPPLIER R WITH (NOLOCK)
                                    WHERE R.SUPPLIER_ID = M.SUPPLIER_ID AND R.BUSINESS_TAG = 0))) AS IMPACT;

PRINT N'== 1606 采购单引用校验重建完成 ==';
