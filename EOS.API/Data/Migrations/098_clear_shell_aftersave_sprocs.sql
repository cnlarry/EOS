-- ============================================================================
-- EOS.ERP migration 099: 清空「已迁校验目录」模块的空壳 AFTERSAVE_SP
-- ----------------------------------------------------------------------------
-- 背景：这些模块的保存后行为已由校验目录（MODULE_VALIDATION_RULE）承接，现代保存链
--   不再调用其 AFTERSAVE_SP（装配期即被置空，见 CatalogAfterSaveMap）。但字段仍被留在
--   MODULES 上，原因只有一个——此前"模块是否自动编号"是靠"有没有保存后钩子"推断的，
--   一旦清空钩子，BILLKIND 配了自动单号的模块就会静默失去编号能力。
-- 现状：自动编号已改为显式判定（BILLKIND 有 IS_DEFAULT=1 且 IS_AUTO=1 的单别即自动编号），
--   与保存后钩子无关；取号也改走独立序列表 BILL_NO_SEQUENCE（迁移 098），不再依赖
--   "有钩子"分支推导出的 BusinessRule。因此这批钩子字段成了纯粹的死配置。
-- 行为等价性：清空的 21 个模块中，带自动单号的（1610/2914/180106/180205/180211/
--   180309/1803091）改由 BILLKIND 显式判定继续编号；1404/1604 走静态规则表，本就不受
--   影响；其余模块无自动单号，BusinessRule 从"全空"变为 null，保存链各判定点均按
--   null 处理（WorkflowSproc/DomainRule/AfterSaveSproc 本就为空），无可观测差异。
-- 可逆：按需写回原过程名即可（过程本身不删除）。
-- 幂等：仅当 AFTERSAVE_SP 非空时改写。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

UPDATE dbo.MODULES
   SET AFTERSAVE_SP = NULL,
       LAST_UPDATE_BY = N'DbUp',
       LAST_UPDATE_DATE = GETDATE()
 WHERE M_IDX IN (
    2914,                              -- 料号开模评估资料唯一
    180102, 180105, 180110, 180111,    -- 员工工号唯一
    180205,                            -- 每人每月一笔出勤参数
    180211, 180651,                    -- 当月每人一班排班
    180309, 1803091, 180504,           -- 当月每人一份工资表
    180106, 180107, 180108,            -- 合同/投保/证件的期间不重叠与同单重复
    2704,                              -- 工单制程：制令单引用存在
    2908, 2909, 2910,                  -- 模具出/入库与报废：模具编号引用存在
    1404,                              -- 报价单：客户与询价单引用存在
    1604,                              -- 厂商报价单：厂商与询价单引用存在
    1610                               -- 收料核价单：厂商引用存在
 )
   AND AFTERSAVE_SP IS NOT NULL
   AND LTRIM(RTRIM(AFTERSAVE_SP)) <> '';

SELECT CONCAT(N'  清空空壳保存后钩子：', @@ROWCOUNT, N' 个模块') AS SUMMARY;

COMMIT;
