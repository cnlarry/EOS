-- ============================================================================
-- EOS.ERP migration 418: 修正 BOM_STRU_M.EDITION 的主键元数据
-- ----------------------------------------------------------------------------
-- 现象：「新建产品 BOM」无法保存，两种写法各报一个错：
--   · 不提交 EDITION → 主键字段 EDITION 不能为空
--   · 提交 EDITION   → 该字段为只读，不可提交（READONLY_FIELD）
--
-- 根因是元数据自相矛盾：`EDITION` 是 `BOM_STRU_M` 物理主键的一半，却被标成
-- `IS_READONLY=1` + `IS_VERIFY=0`：
--   · 只读 → `RecordPayloadValidator.ValidateSubmitted` 的
--           `IsReadonly && !IsRequired && 无选择器` 分支直接拒绝（L178）；
--   · 不必填 → 进不了「只读但必填，界面确实会带值」的豁免分支；
--   · 主键 → 新建时又必须给值。
-- 三条叠在一起，任何提交方式都过不了。`PRO_NO` 的配置是对的（verify=1 / ro=0 / pk=1），
-- 按它对齐即可。
--
-- 影响面：只改 `BOM_STRU_M` 一张表的元数据。1204 是定制页、不参与工作台发布，
-- 这条修正的最大受益者就是「新建 BOM」——它此前从未成功保存过。
--
-- 遗留：`IS_PK` 仅用于界面标注与排序，主键判定一律走物理约束
-- （`WorkbenchSql.GetPrimaryKeyColumnsAsync`），所以标它不影响「主键不可改」的校验。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Mig NVARCHAR(100) = N'DbUp';

IF NOT EXISTS (SELECT 1 FROM dbo.FIELDS
               WHERE LTRIM(RTRIM(T_ID)) = N'BOM_STRU_M' AND LTRIM(RTRIM(F_ID)) = N'EDITION')
    THROW 57901, N'BOM_STRU_M.EDITION 字段元数据不存在，迁移中止。', 1;

BEGIN TRANSACTION;

UPDATE dbo.FIELDS
   SET IS_READONLY = 0,   -- 主键必须可提交，否则新建无从指定
       IS_VERIFY = 1,     -- 必填：它是记录身份的一半
       IS_PK = 1,         -- 与 PRO_NO 一致（仅标注用途）
       LAST_UPDATE_BY = @Mig,
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE LTRIM(RTRIM(T_ID)) = N'BOM_STRU_M' AND LTRIM(RTRIM(F_ID)) = N'EDITION'
   AND (ISNULL(IS_READONLY, 0) = 1 OR ISNULL(IS_VERIFY, 0) = 0 OR ISNULL(IS_PK, 0) = 0);
PRINT N'[418] EDITION 元数据修正：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

/* 断言：三列必须同时到位，缺任何一条新建都会再次卡住 */
IF NOT EXISTS (SELECT 1 FROM dbo.FIELDS
               WHERE LTRIM(RTRIM(T_ID)) = N'BOM_STRU_M' AND LTRIM(RTRIM(F_ID)) = N'EDITION'
                 AND ISNULL(IS_READONLY, 0) = 0 AND ISNULL(IS_VERIFY, 0) = 1)
    THROW 57902, N'EDITION 元数据未修正到位，迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== BOM_STRU_M.EDITION 已可提交、必填、标为主键 ==';
