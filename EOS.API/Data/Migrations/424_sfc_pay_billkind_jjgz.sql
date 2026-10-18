-- ============================================================================
-- 计件工资单别（180402）：补一个已批核的默认单别，并启用它的选择来源
--
-- SFC_PAY_M.PAY_TYPE 是主键第一列、必填，ONLY_CHOOSE=1；BILLKIND 里没有 180402 的档位，
-- 来源行也停在 ACTIVE_TAG=0，所以该字段只能手输。这里补档位并启用来源，
-- 与其它单据的「单别只能选、且必须是已批核的单据性质」同口径。
--
-- 幂等：档位按编码 + 模块双判重；来源启用只在未启用/未配回填时执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @touched INT = 0;

-- ---------------------------------------------------------------------------
-- 1. 单别档位
-- ---------------------------------------------------------------------------
INSERT INTO dbo.BILLKIND
    (BILL_CODE, BILL_NAME, B_M_IDX, IS_AUTO, IS_DEFAULT, USED_BILL_NO, REMARK,
     CREATE_PERSON, CREATE_DATE, CONFIRM_PERSON, CONFIRM_DATE, CONFIRM_TAG)
SELECT N'JJGZ', N'计件工资单', 180402, 1, 1, N'JJGZ{YYMM}0000',
       N'计件工资单别：原先无档位，字段只能手输',
       N'EOS-MIG', SYSDATETIME(), N'EOS-MIG', SYSDATETIME(), 1
WHERE NOT EXISTS (SELECT 1 FROM dbo.BILLKIND k WITH (NOLOCK) WHERE LTRIM(RTRIM(k.BILL_CODE)) = N'JJGZ')
  AND NOT EXISTS
      (
          SELECT 1 FROM dbo.BILLKIND k2 WITH (NOLOCK)
          WHERE k2.B_M_IDX = 180402 AND k2.IS_DEFAULT = 1 AND k2.CONFIRM_TAG = 1
      );
SET @touched += @@ROWCOUNT;

-- ---------------------------------------------------------------------------
-- 2. 选择来源：启用 + 回填 BILL_CODE + 过滤只认本模块已批核档位
-- ---------------------------------------------------------------------------
UPDATE s
SET s.ACTIVE_TAG = 1,
    s.RETURN_ITEMS = N'[{"target":"PAY_TYPE","column":"BILL_CODE"}]',
    s.FILTER_STRUCT = N'{"logic":"AND","items":['
        + N'{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},'
        + N'{"field":"CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}'
        + N']}',
    s.LAST_UPDATE_BY = N'EOS-MIG',
    s.LAST_UPDATE_DATE = SYSDATETIME()
FROM dbo.FIELD_DATASOURCE s
WHERE LTRIM(RTRIM(s.T_ID)) = N'SFC_PAY_M'
  AND LTRIM(RTRIM(s.F_ID)) = N'PAY_TYPE'
  AND LTRIM(RTRIM(ISNULL(s.SOURCE_T_ID, N''))) = N'BILLKIND'
  AND (COALESCE(s.ACTIVE_TAG, 0) = 0 OR ISNULL(s.RETURN_ITEMS, N'') = N'');
SET @touched += @@ROWCOUNT;

-- ---------------------------------------------------------------------------
-- 3. 标脏（单别候选与新增默认值随定义快照冻结）
-- ---------------------------------------------------------------------------
IF @touched > 0
BEGIN
    MERGE dbo.WORKBENCH_MODULE_DIRTY AS t
    USING (SELECT 180402 AS M_IDX) AS s
       ON t.M_IDX = s.M_IDX
    WHEN MATCHED THEN
        UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = N'EOS-MIG', LAST_MODIFIED_AT = SYSDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
        VALUES (s.M_IDX, 1, N'EOS-MIG', SYSDATETIME());
END

PRINT N'[424] 计件工资单别 JJGZ：本次触及 ' + CAST(@touched AS NVARCHAR(10)) + N' 行。';

COMMIT;
