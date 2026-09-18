-- ============================================================================
-- EOS.ERP migration 153: 工资项目设定入效果目录并退役 C#（hr-wage-item 180301 / hrm-wage-item 180502）
-- ----------------------------------------------------------------------------
-- C# 侧动作（`HrDomainRules.HrWageItemAfterSaveAsync`，两族共用一个方法）是把**工资项目明细表**
-- 的显隐/名称/格式/备注同步进 `FIELDS`（表单字段元数据）：
--     UPDATE dbo.FIELDS SET IS_VISIBLE=0 WHERE T_ID=@TId AND F_ID LIKE 'WAGE_ITEM%';
--     UPDATE f SET f.IS_VISIBLE=w.IS_USED, f.F_DESC=w.WAGE_NAME, f.DISPLAY_FORMAT=w.DISPLAY_FORMAT,
--                 f.F_REMARK=w.SQL_REMARK
--     FROM dbo.FIELDS f INNER JOIN dbo.[工资项目表] w ON f.F_ID=w.WAGE_FIELD WHERE f.T_ID=@TId;
-- 承接：新增服务处理器 **`fields-metadata-sync`**（目标表/来源表各列名 + 目标标识值 + 字段前缀，
-- 全部闭合参数并逐个校验为物理列；标识值与前缀只作参数传入，不拼进 SQL 文本）。
--   · 180301：`targetId=HR_WAGE_D`、`source.table=HR_WAGE`；
--   · 180502：`targetId=HRM_WAGE_D`、`source.table=HRM_WAGE`。
-- **本迁移把两模块接进效果引擎**（`EFFECT_ENGINE_TAG` 0 → 1）：两者此前均未接管，目录里既无校验规则
-- 也无业务动作；守卫断言接管前为空配置，避免误激活未知配置。
-- 幂等：动作按 模块+SAVE+SEQ 合并；快照族名仅当仍含对应族名时改写，改写后复核。
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

IF OBJECT_ID(N'tempdb..#Family') IS NOT NULL DROP TABLE #Family;
CREATE TABLE #Family (MODULE_ID INT PRIMARY KEY, FAMILY NVARCHAR(40) NOT NULL, TARGET_ID NVARCHAR(40) NOT NULL, SOURCE_TABLE NVARCHAR(64) NOT NULL);
INSERT INTO #Family (MODULE_ID, FAMILY, TARGET_ID, SOURCE_TABLE) VALUES
    (180301, N'hr-wage-item', N'HR_WAGE_D', N'HR_WAGE'),
    (180502, N'hrm-wage-item', N'HRM_WAGE_D', N'HRM_WAGE');

/* 守卫：模块存在、主表与来源表/标识值符合预期、接管前无任何规则与动作 */
IF EXISTS (SELECT 1 FROM #Family f
           WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = f.MODULE_ID
                               AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, ''))) = f.SOURCE_TABLE))
    THROW 50001, N'工资项目设定模块的主表与预期不符，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM #Family f WHERE OBJECT_ID(N'dbo.' + f.SOURCE_TABLE) IS NULL)
    THROW 50002, N'工资项目表不存在，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM #Family f
           WHERE EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE r WHERE r.MODULE_ID = f.MODULE_ID)
              OR EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a WHERE a.MODULE_ID = f.MODULE_ID))
    THROW 50003, N'待接管模块已有校验规则或业务动作，迁移中止（先核对配置）。', 1;

/* ① 接管：打开效果引擎 */
UPDATE dbo.MODULES SET EFFECT_ENGINE_TAG = 1, LAST_UPDATE_BY = N'DbUp', LAST_UPDATE_DATE = SYSDATETIME()
WHERE M_IDX IN (SELECT MODULE_ID FROM #Family) AND ISNULL(EFFECT_ENGINE_TAG, 0) = 0;

/* ② 播种 SAVE 期字段元数据同步动作 */
DECLARE @CardTemplate NVARCHAR(MAX) =
    N'{"targetId":"{TARGET}","prefix":"WAGE_ITEM",'
    + N'"target":{"table":"FIELDS","idColumn":"T_ID","fieldColumn":"F_ID","visibleColumn":"IS_VISIBLE",'
    + N'"descColumn":"F_DESC","formatColumn":"DISPLAY_FORMAT","remarkColumn":"F_REMARK"},'
    + N'"source":{"table":"{SOURCE}","fieldColumn":"WAGE_FIELD","visibleColumn":"IS_USED",'
    + N'"descColumn":"WAGE_NAME","formatColumn":"DISPLAY_FORMAT","remarkColumn":"SQL_REMARK"}}';

DECLARE @ModuleId INT, @Family NVARCHAR(40), @TargetId NVARCHAR(40), @SourceTable NVARCHAR(64), @Params NVARCHAR(MAX);
DECLARE family_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT MODULE_ID, FAMILY, TARGET_ID, SOURCE_TABLE FROM #Family;
OPEN family_cursor;
FETCH NEXT FROM family_cursor INTO @ModuleId, @Family, @TargetId, @SourceTable;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @Params = REPLACE(REPLACE(@CardTemplate, N'{TARGET}', @TargetId), N'{SOURCE}', @SourceTable);
    MERGE dbo.MODULE_BUSINESS_ACTION AS T
    USING (SELECT @ModuleId AS MODULE_ID) AS S
       ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = 1
    WHEN MATCHED THEN
        UPDATE SET T.EFFECT_KEY = N'fields-metadata-sync', T.EFFECT_NAME = N'工资项目字段元数据联动（保存期）',
                   T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.PARAM_STRUCT = @Params,
                   T.REVERSE_STRUCT = N'{"kind":"none"}',
                   T.REMARK = N'原 C# 工资项目设定保存后动作的忠实移植（FIELDS 显隐/名称/格式/备注联动）',
                   T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
    WHEN NOT MATCHED THEN
        INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
                PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
        VALUES (S.MODULE_ID, N'SAVE', 1, N'fields-metadata-sync', N'工资项目字段元数据联动（保存期）', 1, N'BLOCK', NULL,
                @Params, N'{"kind":"none"}',
                N'原 C# 工资项目设定保存后动作的忠实移植（FIELDS 显隐/名称/格式/备注联动）', @Family,
                N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());
    FETCH NEXT FROM family_cursor INTO @ModuleId, @Family, @TargetId, @SourceTable;
END
CLOSE family_cursor;
DEALLOCATE family_cursor;

/* ③ 置空已发布快照里的族名 */
UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON, N'"DomainRule":"' + F.FAMILY + N'"', N'"DomainRule":null')
FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
JOIN #Family F ON F.MODULE_ID = S.MODULE_ID
WHERE S.IS_CURRENT = 1
  AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + F.FAMILY + N'"%';

/* ④ 收口断言 */
IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S JOIN #Family F ON F.MODULE_ID = S.MODULE_ID
           WHERE S.IS_CURRENT = 1 AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + F.FAMILY + N'"%')
    THROW 50004, N'当前快照仍残留已退役的族名，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM #Family f
           WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a
                             WHERE a.MODULE_ID = f.MODULE_ID AND a.EVENT_CODE = N'SAVE' AND a.SEQ = 1
                               AND a.EFFECT_KEY = N'fields-metadata-sync' AND a.ENABLED = 1))
    THROW 50005, N'退役 C# 后缺少启用的 SAVE 期 fields-metadata-sync 动作，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM #Family f
           WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = f.MODULE_ID AND ISNULL(m.EFFECT_ENGINE_TAG, 0) = 1))
    THROW 50006, N'有模块未接管进效果引擎（EFFECT_ENGINE_TAG 仍为 0），迁移中止。', 1;

DROP TABLE #Family;

PRINT N'== hr-wage-item / hrm-wage-item 入效果目录并退役 C# 完成（180301/180502，SAVE 期 fields-metadata-sync；快照族名已置空）==';
