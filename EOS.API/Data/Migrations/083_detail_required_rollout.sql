-- ============================================================================
-- EOS.ERP migration 084: 交易单据类模块明细必填口径落地
-- ----------------------------------------------------------------------------
-- 依《明细必填口径清单》逐行确认结果：有明细表的模块分两类——
--   · 交易单据类（明细承载业务内容）：无明细即不可保存（DETAIL_NO_SAVE=1）；
--   · 附属集合类（联系人/权限明细/日志/公式/排序/员工附属资料）与查询页：保持允许为空。
-- 原 DETAIL_NO_SAVE=0 的 32 个交易单据模块在此置为 1；同时把 180111（员工基本资料，
-- 与 180102/180110 同表同形态）从既有的 1 对齐为 0——员工附属资料属附属集合，
-- 其兄弟模块亦为"允许无明细"，先前的不一致属历史遗留。
--
-- 与 DETAIL_NO_SAVE=1 的配套语义：提交里未提供明细＝保留原样；提交显式空明细＝拒绝。
-- 仅改模块元数据；该字段随模块发布进入 Definition 快照，需重新发布后生效。
-- 幂等：仅当取值与目标不一致时更新。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 开始落地明细必填口径 ==';

-- 交易单据类：无明细不可保存
UPDATE dbo.MODULES
SET DETAIL_NO_SAVE = 1,
    LAST_UPDATE_BY = N'migration-084',
    LAST_UPDATE_DATE = SYSDATETIME()
WHERE DETAIL_NO_SAVE = 0 AND M_TAG = 1 AND M_IDX IN (
    1418, 1423, 1502, 1506, 1509, 1512, 1522, 1616, 2101, 2402, 2703, 2704, 2708,
    2803, 2804, 2906, 2912, 2915, 2916, 3006, 3203, 130101, 170104, 170105, 170204,
    170205, 180203, 180207, 180212, 180655, 329801, 1803091);

-- 员工附属资料：与同表兄弟模块一致，允许为空
UPDATE dbo.MODULES
SET DETAIL_NO_SAVE = 0,
    LAST_UPDATE_BY = N'migration-084',
    LAST_UPDATE_DATE = SYSDATETIME()
WHERE DETAIL_NO_SAVE = 1 AND M_IDX = 180111;

PRINT N'-- 落地后分布 --';
SELECT CONCAT(N'  DETAIL_NO_SAVE=', ISNULL(CONVERT(nvarchar(5), DETAIL_NO_SAVE), N'<null>'),
              N' 且有明细表的模块数=', COUNT(*)) AS SUMMARY
FROM dbo.MODULES
WHERE M_TAG = 1 AND NULLIF(LTRIM(RTRIM(ISNULL(DETAIL_TABLE, N''))), N'') IS NOT NULL
GROUP BY DETAIL_NO_SAVE
ORDER BY DETAIL_NO_SAVE;

PRINT N'== 明细必填口径落地完成 ==';
