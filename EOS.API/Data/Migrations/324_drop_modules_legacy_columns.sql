-- ============================================================================
-- EOS.ERP migration 324: MODULES 卸下八列旧系统遗产
-- ----------------------------------------------------------------------------
-- 用户 2026-10-06 拍板（决策清单 #144）：dbo.MODULES 上这八列一律删。
-- 分两档，理由不同：
--
--   ① 一个"零"字定案（没有任何人填过、也没有任何人读）：
--        CONFIRM_TAG   批核状态   363 行**全 0**。模块自己不存在"批核"这件事——批核是单据的事实，
--                                挂在各自主表上，且有 check-lifecycle-columns 常态巡检
--                                （"声明了批核能力 ⇒ 主表必须有 CONFIRM_TAG"查的是主表，与本列无关）。
--        OWNER         所有者     363 行**全空**。旧系统的字段级归属；现代权限走
--        OWNER_G       所有者组   363 行**全空**。DENY_*_FIELD（按字段名排除）+ 数据过滤。
--
--   ② 占位值/默认值——看着有数据，逐值验过其实无内容：
--        CONFIRM_DATE  批核日期   266 行非 NULL，但**全部是 1900-01-01**（真日期 0 行）。
--        CONFIRM_PERSON 批核人    363 行**全空白**（nchar(40) 定长列的空白填充）。
--        CI            公司别     363 行**全 'DEFAULT'**——单公司部署的隔离键，没有区分信息。
--        CREATE_PERSON 建立人     只有 2 行有值（补录模块时写的，③ 会把取值打印留痕）。
--        CREATE_DATE   建立日期   99 行是 **2026-08-09 ~ 2026-10-01 的补录时间**（不是旧系统历史）。
--
--   保留 REMARK（备注）：352 行有文本，且用户要求把它放出来写"这个模块是干什么的"
--   （2301「基础」页签的可编辑字段，见 MenuAdminRepository / MenuAdminPage）。
--
-- 为什么安全：全仓 grep `MODULES.<列名>` 零命中（命中的同名点号写法都落在别的主表上，
-- 如合同/考勤/生产单的 CONFIRM_TAG）；元数据侧 16 处 VIRTUAL_EXP 指的是
-- MODULES.M_DESC / MASTER_TABLE / DETAIL_TABLE / M_IDX，与这八列无关。
-- 模块 2301 自己的主表就是 MODULES，但它的版式/页签/快照/动作/校验行**全为 0**（实测），
-- 所以删列不会留下悬空版式（check-form-layout 的判据）。
--
-- 命名全大写；**非幂等**：八列缺一即报错（删列意味着有人做过决定，该由台账而不是守卫记着，
-- 与 320 / 321 同一口径）。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- ① 前置自证：八列必须都在，且要留的 REMARK 必须在
IF COL_LENGTH(N'dbo.MODULES', N'CONFIRM_TAG') IS NULL
    OR COL_LENGTH(N'dbo.MODULES', N'OWNER') IS NULL
    OR COL_LENGTH(N'dbo.MODULES', N'OWNER_G') IS NULL
    OR COL_LENGTH(N'dbo.MODULES', N'CONFIRM_DATE') IS NULL
    OR COL_LENGTH(N'dbo.MODULES', N'CONFIRM_PERSON') IS NULL
    OR COL_LENGTH(N'dbo.MODULES', N'CI') IS NULL
    OR COL_LENGTH(N'dbo.MODULES', N'CREATE_PERSON') IS NULL
    OR COL_LENGTH(N'dbo.MODULES', N'CREATE_DATE') IS NULL
    THROW 53400, N'MODULES 的八列不齐：本迁移的前提不成立（库结构已变或已删过）。', 1;

IF COL_LENGTH(N'dbo.MODULES', N'REMARK') IS NULL
    THROW 53401, N'MODULES.REMARK 不在：它是本次要**保留**的列（写模块用途），库结构不是本脚本预期的起点。', 1;

-- ② 不静默丢内容：这五列必须"没有任何实质内容"，否则先把它解释清楚再删
IF EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK) WHERE CONFIRM_TAG <> 0)
    THROW 53402, N'仍有模块的 CONFIRM_TAG 为 1：模块级批核状态若真被用过，先弄清它的语义（批核本该是单据主表的事实），再删列。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK)
           WHERE NULLIF(LTRIM(RTRIM(ISNULL(OWNER, N''))), N'') IS NOT NULL
              OR NULLIF(LTRIM(RTRIM(ISNULL(OWNER_G, N''))), N'') IS NOT NULL)
    THROW 53403, N'仍有模块配着 OWNER / OWNER_G：先把归属迁到字段级权限（DENY_*_FIELD）或数据过滤，再删列。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK)
           WHERE NULLIF(LTRIM(RTRIM(ISNULL(CONFIRM_PERSON, N''))), N'') IS NOT NULL)
    THROW 53404, N'仍有模块的 CONFIRM_PERSON 有内容：先确认它是不是"谁批过这个模块"的唯一留档，再删列。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK)
           WHERE NULLIF(LTRIM(RTRIM(ISNULL(CI, N''))), N'') NOT IN (N'DEFAULT'))
    THROW 53405, N'仍有模块的 CI 不是 DEFAULT：那是多公司隔离信息，删列会丢，先迁走再删。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK)
           WHERE CONFIRM_DATE IS NOT NULL AND CONVERT(date, CONFIRM_DATE) <> '19000101')
    THROW 53406, N'仍有模块的 CONFIRM_DATE 是真实日期：先确认它是不是"谁批过这个模块"的唯一留档，再删列。', 1;

-- ③ 留痕：完整性与"删掉的是什么"进执行日志（CREATE_PERSON / CREATE_DATE 确有补录痕迹）
DECLARE @trail NVARCHAR(MAX) = N'';
SELECT @trail = @trail + N'    ' + CONVERT(nvarchar(20), M_IDX) + N' ' + ISNULL(M_DESC, N'')
                     + N' 建立人=' + ISNULL(NULLIF(LTRIM(RTRIM(CREATE_PERSON)), N''), N'(空)')
                     + N' 建立日期=' + ISNULL(CONVERT(varchar(19), CREATE_DATE, 120), N'(空)')
                     + N' CI=' + ISNULL(NULLIF(LTRIM(RTRIM(CI)), N''), N'(空)') + CHAR(10)
FROM dbo.MODULES
WHERE NULLIF(LTRIM(RTRIM(ISNULL(CREATE_PERSON, N''))), N'') IS NOT NULL
   OR (CREATE_DATE IS NOT NULL AND CONVERT(date, CREATE_DATE) <> '19000101');

IF LEN(@trail) > 0
    PRINT N'== 随列退役的"补录痕迹"（旧系统从未写过这两列，值是本次现代化补录时落下的）==' + CHAR(10) + @trail;

-- PRINT 里不能嵌子查询（Msg 1046：只允许标量表达式），所以先落到变量再拼串。
DECLARE @ciFilled INT = (SELECT COUNT(*) FROM dbo.MODULES WHERE NULLIF(LTRIM(RTRIM(ISNULL(CI, N''))), N'') IS NOT NULL);
DECLARE @confirmDateFilled INT = (SELECT COUNT(*) FROM dbo.MODULES WHERE CONFIRM_DATE IS NOT NULL);
DECLARE @confirmTagTrue INT = (SELECT COUNT(*) FROM dbo.MODULES WHERE CONFIRM_TAG = 1);
DECLARE @remarkFilled INT = (SELECT COUNT(*) FROM dbo.MODULES WHERE NULLIF(LTRIM(RTRIM(ISNULL(REMARK, N''))), N'') IS NOT NULL);

PRINT N'== 八列的占用复核（删前最后一次）=='
    + CHAR(10) + N'    CI 非空 ' + CONVERT(nvarchar(20), @ciFilled) + N' 行（全 DEFAULT）'
    + N'；CONFIRM_DATE 非 NULL ' + CONVERT(nvarchar(20), @confirmDateFilled) + N' 行（全 1900-01-01 占位）'
    + N'；CONFIRM_TAG=1 ' + CONVERT(nvarchar(20), @confirmTagTrue) + N' 行'
    + CHAR(10) + N'    OWNER/OWNER_G/CONFIRM_PERSON 有内容 0 行；REMARK 有文本 '
    + CONVERT(nvarchar(20), @remarkFilled) + N' 行（**保留**）';

-- ④ 先摘挂在八列上的默认约束（CREATE_PERSON / CREATE_DATE / CONFIRM_TAG / CI 各一个），
--    否则 ALTER COLUMN / DROP COLUMN 会被"对象 DF_… 依赖于列 …"挡住。
--    按"绑定到这几列的默认约束"取，不写死约束名——别的库可能是别的命名。
DECLARE @dropDefaults NVARCHAR(MAX) = N'';
SELECT @dropDefaults = @dropDefaults + N'ALTER TABLE dbo.MODULES DROP CONSTRAINT ' + QUOTENAME(dc.name) + N';' + CHAR(10)
FROM sys.default_constraints dc
JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
WHERE dc.parent_object_id = OBJECT_ID(N'dbo.MODULES')
  AND c.name IN (N'CONFIRM_TAG', N'OWNER', N'OWNER_G', N'CONFIRM_DATE', N'CONFIRM_PERSON',
                 N'CI', N'CREATE_PERSON', N'CREATE_DATE');

IF @dropDefaults <> N'' EXEC sp_executesql @dropDefaults;

-- ⑤ 再摘列注释（扩展属性），最后删列
DECLARE @dropProps NVARCHAR(MAX) = N'';
SELECT @dropProps = @dropProps + N'EXEC sys.sp_dropextendedproperty @name = N'''
                    + REPLACE(ep.name, N'''', N'''''') + N''', @level0type = N''SCHEMA'', @level0name = N''dbo'''
                    + N', @level1type = N''TABLE'', @level1name = N''MODULES'''
                    + N', @level2type = N''COLUMN'', @level2name = N''' + c.name + N''';' + CHAR(10)
FROM sys.extended_properties ep
JOIN sys.columns c ON c.object_id = ep.major_id AND c.column_id = ep.minor_id
WHERE ep.major_id = OBJECT_ID(N'dbo.MODULES')
  AND c.name IN (N'CONFIRM_TAG', N'OWNER', N'OWNER_G', N'CONFIRM_DATE', N'CONFIRM_PERSON',
                 N'CI', N'CREATE_PERSON', N'CREATE_DATE');

IF @dropProps <> N'' EXEC sp_executesql @dropProps;

ALTER TABLE dbo.MODULES DROP COLUMN CONFIRM_TAG, OWNER, OWNER_G, CONFIRM_DATE, CONFIRM_PERSON,
                              CI, CREATE_PERSON, CREATE_DATE;

-- ⑥ 字段元数据行（FIELDS 里描述这八列的行）随列一起走：它们描述的是已经不存在的列。
--    先留痕（这些描述就是那八列在元数据里的身份证，删掉后只在执行日志里能查到）。
DECLARE @fieldTrail NVARCHAR(MAX) = N'';
SELECT @fieldTrail = @fieldTrail + N'    MODULES.' + F_ID + N' ' + ISNULL(F_DESC, N'') + N'（登记人 ' + ISNULL(NULLIF(LTRIM(RTRIM(LAST_UPDATE_BY)), N''), N'未知') + N'）' + CHAR(10)
FROM dbo.FIELDS
WHERE T_ID = N'MODULES'
  AND F_ID IN (N'CONFIRM_TAG', N'OWNER', N'OWNER_G', N'CONFIRM_DATE', N'CONFIRM_PERSON',
               N'CI', N'CREATE_PERSON', N'CREATE_DATE');
IF LEN(@fieldTrail) > 0
    PRINT N'== 随列删除的字段登记 ==' + CHAR(10) + @fieldTrail;

DELETE FROM dbo.FIELDS
WHERE T_ID = N'MODULES'
  AND F_ID IN (N'CONFIRM_TAG', N'OWNER', N'OWNER_G', N'CONFIRM_DATE', N'CONFIRM_PERSON',
               N'CI', N'CREATE_PERSON', N'CREATE_DATE');

-- ⑦ 后置自证：八列必须消失
IF COL_LENGTH(N'dbo.MODULES', N'CONFIRM_TAG') IS NOT NULL
    OR COL_LENGTH(N'dbo.MODULES', N'OWNER') IS NOT NULL
    OR COL_LENGTH(N'dbo.MODULES', N'OWNER_G') IS NOT NULL
    OR COL_LENGTH(N'dbo.MODULES', N'CONFIRM_DATE') IS NOT NULL
    OR COL_LENGTH(N'dbo.MODULES', N'CONFIRM_PERSON') IS NOT NULL
    OR COL_LENGTH(N'dbo.MODULES', N'CI') IS NOT NULL
    OR COL_LENGTH(N'dbo.MODULES', N'CREATE_PERSON') IS NOT NULL
    OR COL_LENGTH(N'dbo.MODULES', N'CREATE_DATE') IS NOT NULL
    THROW 53410, N'八列未被删除。', 1;

-- 后置自证：该留的必须还在（模块自身事实 + 要放出来的备注）
IF COL_LENGTH(N'dbo.MODULES', N'M_IDX') IS NULL
    OR COL_LENGTH(N'dbo.MODULES', N'M_DESC') IS NULL
    OR COL_LENGTH(N'dbo.MODULES', N'M_URL') IS NULL
    OR COL_LENGTH(N'dbo.MODULES', N'M_TAG') IS NULL
    OR COL_LENGTH(N'dbo.MODULES', N'SORT_IDX') IS NULL
    OR COL_LENGTH(N'dbo.MODULES', N'MASTER_TABLE') IS NULL
    OR COL_LENGTH(N'dbo.MODULES', N'FORM_OPEN_MODE') IS NULL
    OR COL_LENGTH(N'dbo.MODULES', N'REMARK') IS NULL
    THROW 53411, N'不该删的列被删了（模块自身字段与 REMARK 必须保留）。', 1;

-- 后置自证：FIELDS 里不得再留着八列的登记，且 REMARK 的登记必须在
IF EXISTS (SELECT 1 FROM dbo.FIELDS
           WHERE T_ID = N'MODULES'
             AND F_ID IN (N'CONFIRM_TAG', N'OWNER', N'OWNER_G', N'CONFIRM_DATE', N'CONFIRM_PERSON',
                          N'CI', N'CREATE_PERSON', N'CREATE_DATE'))
    THROW 53412, N'FIELDS 里仍留着已删列的元数据行。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = N'MODULES' AND F_ID = N'REMARK')
    THROW 53413, N'REMARK 的字段登记被顺手删了（它是要保留的列）。', 1;

COMMIT TRANSACTION;

PRINT N'== MODULES 列瘦身完成：CONFIRM_TAG / OWNER / OWNER_G / CONFIRM_DATE / CONFIRM_PERSON / CI / CREATE_PERSON / CREATE_DATE 已物理删除（REMARK 保留）==';
