-- ============================================================================
-- 单据性质资料（BILLKIND）：给现行模块补单别，并只让已批核的单别参与选择
--
-- 两件事：
--   1. 现行模块缺档位 —— 同一业务在库里有两代表（HR_* 与 HRM_*、CUS_* 单表等），
--      BILLKIND 只覆盖了旧表那一代。选择器按 B_M_IDX={module} 过滤，新表那代模块
--      （180651/180653/180655/180656/180657/180658/180661、2801/2802、3001/3002/3004/3005、
--      1803101）打开选择器是空的，单别是必填主键列，单据就录不进去。
--      BILLKIND.BILL_CODE 是主键（全库唯一），旧编码不能复用，故按业务缩写另立编码。
--      180103 的旧档位挂在 1813 上，而 1813 不是现行模块 —— 直接改挂，不新增。
--   2. 单别必须已批核 —— 未批核的单据性质只是草稿配置，不能拿来开单。
--      运行期判据是选择器的过滤条件，故给所有启用中的 BILLKIND 来源补 CONFIRM_TAG=1；
--      存量里 44 个模块的档位随参考系统导入时丢了批核位（CONFIRM_PERSON 为空），
--      先补批核再收紧过滤，否则这些模块会从"能选"直接变成"选不到"。
--
-- 幂等：新增按编码与模块双判重；批核只碰 CONFIRM_TAG=0；过滤条件已含 CONFIRM_TAG 的跳过。
-- 标脏：单别候选与新增默认值都随定义快照冻结，受影响的模块统一标脏。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- 本次是否真的动了数据：幂等重跑（DbUp 之外的重复执行）时不该再标脏
DECLARE @touched INT = 0;

-- ---------------------------------------------------------------------------
-- 1. 现行模块的单别（旧档位在已消失的 1813 下：改挂）
-- ---------------------------------------------------------------------------
UPDATE b
SET b.B_M_IDX = 180103,
    b.LAST_UPDATE_BY = N'EOS-MIG',
    b.LAST_UPDATE_DATE = SYSDATETIME()
FROM dbo.BILLKIND b
WHERE b.B_M_IDX = 1813
  AND LTRIM(RTRIM(b.BILL_CODE)) = N'YGDD'
  AND NOT EXISTS (SELECT 1 FROM dbo.MODULES m WITH (NOLOCK) WHERE m.M_IDX = 1813)
  AND NOT EXISTS (SELECT 1 FROM dbo.BILLKIND k WITH (NOLOCK) WHERE k.B_M_IDX = 180103);
SET @touched += @@ROWCOUNT;

-- ---------------------------------------------------------------------------
-- 2. 现行模块的单别（旧编码已被旧表那代占用：按业务缩写另立）
-- ---------------------------------------------------------------------------
DECLARE @bill TABLE
(
    M_IDX int NOT NULL,
    BILL_CODE nvarchar(10) NOT NULL,
    BILL_NAME nvarchar(50) NOT NULL,
    USED_BILL_NO nvarchar(50) NOT NULL
);

INSERT INTO @bill (M_IDX, BILL_CODE, BILL_NAME, USED_BILL_NO) VALUES
    (2801,    N'TWJH2',   N'托外进货单',   N'{YYMM}TWJH20000'),
    (2802,    N'TWTH2',   N'托外退货单',   N'{YYMM}TWTH20000'),
    (180651,  N'PB2',     N'排班',         N'PB2{YYYYMM}000'),
    (180653,  N'TSD2',    N'调休单',       N'TX2{YYMM}000'),
    (180655,  N'QJD2',    N'请假单',       N'QJD2{YYYYMM}0000'),
    (180656,  N'QKD2',    N'签卡单',       N'QK2{YYMM}000'),
    (180657,  N'FJ2',     N'放假通知单',   N'FJ2{YYMM}000'),
    (180658,  N'TZD2',    N'上班调整单',   N'TZ2{YYMM}000'),
    (180661,  N'CC2',     N'出差单',       N'CC2{YYMM}000'),
    (3001,    N'CUSIN',   N'原材料进仓单', N'CUSIN{YYMM}0000'),
    (3002,    N'CUSOUT',  N'原材料出仓单', N'CUSOUT{YYMM}0000'),
    (3004,    N'CUSPRO',  N'产成品出仓单', N'CUSPRO{YYMM}0000'),
    (3005,    N'CUSPACK', N'装箱单',       N'CUSPACK{YYMM}0000'),
    (1803101, N'LZGZM',   N'保密离职工资表', N'LZGZM{YYYYMM}000');

INSERT INTO dbo.BILLKIND
    (BILL_CODE, BILL_NAME, B_M_IDX, IS_AUTO, IS_DEFAULT, USED_BILL_NO, REMARK,
     CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
SELECT b.BILL_CODE, b.BILL_NAME, b.M_IDX, 1, 1, b.USED_BILL_NO,
       N'现行模块的单别：旧表那一代已占用同业务编码，此处按业务缩写另立',
       N'EOS-MIG', SYSDATETIME(), N'EOS-MIG', SYSDATETIME(), 1
FROM @bill b
WHERE NOT EXISTS
      (
          SELECT 1 FROM dbo.BILLKIND k WITH (NOLOCK)
          WHERE LTRIM(RTRIM(k.BILL_CODE)) = LTRIM(RTRIM(b.BILL_CODE))
      )
  AND NOT EXISTS
      (
          SELECT 1 FROM dbo.BILLKIND k2 WITH (NOLOCK)
          WHERE k2.B_M_IDX = b.M_IDX AND k2.IS_DEFAULT = 1 AND k2.CONFIRM_TAG = 1
      );
SET @touched += @@ROWCOUNT;

-- ---------------------------------------------------------------------------
-- 3. 条码资料 / 考勤采集：启用已配好但未启用的单别来源（原本只能手输）
--    2002 与 180212 的单别列都是主键第一列（必填），参考系统里由页面逻辑赋值；
--    这里让用户至少能从本模块的档位里选，而不是自由输入。
-- ---------------------------------------------------------------------------
UPDATE s
SET s.ACTIVE_TAG = 1,
    s.RETURN_ITEMS = CASE
        WHEN LTRIM(RTRIM(s.T_ID)) = N'PAP_BARCODE_M' AND LTRIM(RTRIM(s.F_ID)) = N'BARCODE_TYPE'
            THEN N'[{"target":"BARCODE_TYPE","column":"BILL_CODE"}]'
        WHEN LTRIM(RTRIM(s.T_ID)) = N'PAP_BARCODE_M' AND LTRIM(RTRIM(s.F_ID)) = N'SEND_TYPE'
            THEN N'[{"target":"SEND_TYPE","column":"BILL_CODE"}]'
        ELSE N'[{"target":"TXT_TYPE","column":"BILL_CODE"}]'
    END,
    s.LAST_UPDATE_BY = N'EOS-MIG',
    s.LAST_UPDATE_DATE = SYSDATETIME()
FROM dbo.FIELD_DATASOURCE s
WHERE LTRIM(RTRIM(ISNULL(s.SOURCE_T_ID, N''))) = N'BILLKIND'
  AND (
        (LTRIM(RTRIM(s.T_ID)) = N'PAP_BARCODE_M' AND LTRIM(RTRIM(s.F_ID)) IN (N'BARCODE_TYPE', N'SEND_TYPE'))
     OR (LTRIM(RTRIM(s.T_ID)) = N'HR_TXT_M'     AND LTRIM(RTRIM(s.F_ID)) = N'TXT_TYPE')
  )
  AND (COALESCE(s.ACTIVE_TAG, 0) = 0 OR ISNULL(s.RETURN_ITEMS, N'') = N'');
SET @touched += @@ROWCOUNT;

-- ---------------------------------------------------------------------------
-- 4. 存量档位补批核：只补「被现行模块的启用选择器引用、且模块存在」的行。
--    这些行随参考系统导入时批核位丢失（CONFIRM_PERSON 为空），不是有意留草稿；
--    指向已消失模块的行（B_M_IDX 不在 MODULES）保持原样 —— 它们不会被任何模块选到。
-- ---------------------------------------------------------------------------
UPDATE b
SET b.CONFIRM_TAG = 1,
    b.CONFIRM_PERSON = COALESCE(NULLIF(LTRIM(RTRIM(b.CONFIRM_PERSON)), N''), N'EOS-MIG'),
    b.CONFIRM_DATE = COALESCE(b.CONFIRM_DATE, SYSDATETIME()),
    b.REMARK = CASE
        WHEN b.REMARK IS NULL OR LTRIM(RTRIM(b.REMARK)) = N'' THEN N'批核位由迁移补齐（导入时缺失）'
        WHEN b.REMARK NOT LIKE N'%批核位由迁移补齐%' THEN b.REMARK + N'；批核位由迁移补齐'
        ELSE b.REMARK
    END
FROM dbo.BILLKIND b
WHERE b.CONFIRM_TAG = 0
  AND EXISTS (SELECT 1 FROM dbo.MODULES m WITH (NOLOCK) WHERE m.M_IDX = b.B_M_IDX)
  AND EXISTS
      (
          SELECT 1
          FROM dbo.FIELD_DATASOURCE s WITH (NOLOCK)
          JOIN dbo.MODULES m2 WITH (NOLOCK)
            ON LTRIM(RTRIM(ISNULL(m2.MASTER_TABLE, N''))) = LTRIM(RTRIM(s.T_ID))
            OR LTRIM(RTRIM(ISNULL(m2.DETAIL_TABLE, N''))) = LTRIM(RTRIM(s.T_ID))
          WHERE LTRIM(RTRIM(ISNULL(s.SOURCE_T_ID, N''))) = N'BILLKIND'
            AND COALESCE(s.ACTIVE_TAG, 0) = 1
            AND m2.M_IDX = b.B_M_IDX
      );
SET @touched += @@ROWCOUNT;

-- ---------------------------------------------------------------------------
-- 5. 选择器只认已批核的单别：给所有启用中的 BILLKIND 来源补 CONFIRM_TAG=1。
--    空 items 的五行（收款/付款/库存流水等）原本没有模块过滤，等价于选全库档位；
--    这里一并收窄为本模块 + 已批核。
-- ---------------------------------------------------------------------------
UPDATE s
SET s.FILTER_STRUCT = CASE
        WHEN s.FILTER_STRUCT IS NULL
          OR JSON_QUERY(s.FILTER_STRUCT, '$.items') IS NULL
          OR JSON_QUERY(s.FILTER_STRUCT, '$.items') = N'[]'
            THEN N'{"logic":"AND","items":['
               + N'{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},'
               + N'{"field":"CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}'
               + N']}'
        ELSE JSON_MODIFY(s.FILTER_STRUCT, 'append $.items',
             JSON_QUERY(N'{"field":"CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}'))
    END,
    s.LAST_UPDATE_BY = N'EOS-MIG',
    s.LAST_UPDATE_DATE = SYSDATETIME()
FROM dbo.FIELD_DATASOURCE s
WHERE LTRIM(RTRIM(ISNULL(s.SOURCE_T_ID, N''))) = N'BILLKIND'
  AND COALESCE(s.ACTIVE_TAG, 0) = 1
  -- 引用列（条码资料的送货单别）按下面的 5b 单独处理：它要按被引用模块过滤
  AND NOT (LTRIM(RTRIM(s.T_ID)) = N'PAP_BARCODE_M' AND LTRIM(RTRIM(s.F_ID)) = N'SEND_TYPE')
  AND NOT EXISTS
      (
          SELECT 1 FROM OPENJSON(s.FILTER_STRUCT, '$.items') j
          WHERE JSON_VALUE(j.value, '$.field') = N'CONFIRM_TAG'
      );
SET @touched += @@ROWCOUNT;

-- 条码资料的「送货单别」是引用列：它指向送货单（1406）的单别，不是条码资料自己的档位。
-- 按 {module} 过滤只会得到条码资料自己的 TM，用户选了也不对，故固定为 1406。
UPDATE s
SET s.FILTER_STRUCT = N'{"logic":"AND","items":['
        + N'{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"1406","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},'
        + N'{"field":"CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}'
        + N']}',
    s.LAST_UPDATE_BY = N'EOS-MIG',
    s.LAST_UPDATE_DATE = SYSDATETIME()
FROM dbo.FIELD_DATASOURCE s
WHERE LTRIM(RTRIM(s.T_ID)) = N'PAP_BARCODE_M'
  AND LTRIM(RTRIM(s.F_ID)) = N'SEND_TYPE'
  AND LTRIM(RTRIM(ISNULL(s.SOURCE_T_ID, N''))) = N'BILLKIND'
  -- 已经是固定 1406 的形态就不重复写
  AND s.FILTER_STRUCT <> N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"1406","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}';
SET @touched += @@ROWCOUNT;

-- ---------------------------------------------------------------------------
-- 6. 标脏：单别候选与新增默认值随定义快照冻结。
--    只在本次真的改了数据时标脏 —— 重复执行不该把已发布的模块重新打成脏。
-- ---------------------------------------------------------------------------
IF @touched > 0
BEGIN
    MERGE dbo.WORKBENCH_MODULE_DIRTY AS t
    USING
    (
        SELECT M_IDX FROM (VALUES
            (2801), (2802), (3001), (3002), (3004), (3005),
            (180103), (180651), (180653), (180655), (180656), (180657), (180658), (180661), (1803101),
            (2002), (180212)
        ) v(M_IDX)
    ) AS s
       ON t.M_IDX = s.M_IDX
    WHEN MATCHED THEN
        UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = N'EOS-MIG', LAST_MODIFIED_AT = SYSDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
        VALUES (s.M_IDX, 1, N'EOS-MIG', SYSDATETIME());
END

PRINT N'[423] 现行模块单别补齐 + BILLKIND 批核门：本次触及 '
    + CAST(@touched AS NVARCHAR(10)) + N' 行（0 = 已执行过，未重复改动）。';

COMMIT;
