-- ============================================================================
-- EOS.ERP migration 321: 模块路由收敛到唯一字段 M_URL
-- ----------------------------------------------------------------------------
-- 用户的"第一刀"（2026-10-06 拍板）：MODULES 里与页面路由有关的四列只留一个。
--
--   M_URL     承载页（唯一真源）
--             空      → 目录节点（有下级，只展开不跳转）/ 未接线模块（落占位页）
--             /workbench   → 统一工作台（列表 + 统一表单；新增/编辑由能力位与权限决定）
--             /reports 等  → 参数化承载页（菜单渲染时追加 /{moduleId}）
--             /admin/xxx   → 自定义承载页（页面自管动作，工作台定义不装配）
--   NEW_URL   新增路由   —— **物理删除**：全库 0/363 有值（实测），从未被任何人填过
--   MODI_URL  修改路由   —— **物理删除**：266 有值里 262 个是默认编辑模板（= 回退值），
--                            4 个真自定义页（2201-2204）的值与本模块 M_URL 逐字相同，
--                            剩 1 个是空格脏值 —— 删掉不丢任何信息（下面的前置自证就按这三档判）
--   HELP_URL  帮助页地址 —— **物理删除**：仅 2 个模块有值（1505/2816 = ~/Client/HungHui/ProductIn.aspx），
--                            是旧客户专属路径；`~/` 形态在现行契约里属非法（IsForbiddenUrl），
--                            运行时点开必坏链 ⇒ 帮助按钮随之退场（值在执行时打印留痕，见 ④）
--
-- 为什么能删：**"能不能新增/编辑"从来不是这三列说了算**，而是统一表单名单
-- （appsettings 的 UnifiedFormEditor.EnabledModuleIds / ReadOnlyModuleIds）裁决；实测 260 个写名单
-- 模块里有 2 个 MODI_URL 是空的，照样能新增/编辑；反过来 100 个名单外模块里只有 5 个 MODI_URL
-- 有值，且都是"不装配工作台定义"的定制页（2201-2204 与 2101）⇒ 删列前后逐模块等价。
--
-- 连带退场（代码侧，本迁移只负责库）：ModuleRouteValidator 的动作模板解析
-- （ResolveActionUrl / IsValidActionUrl / IsUnifiedFormRoute）与它支撑的"动作路由"概念；
-- WorkbenchAccessPolicy.FoldRoutes 改为只按名单折叠；快照 JSON 里的 newUrl/modiUrl/helpUrl
-- 字段不再产出（历史快照多这三个键，反序列化大小写不敏感、缺字段退化 null，不报错）。
--
-- 命名全大写；**非幂等**：本脚本删列，前提是三个列都还在（缺列即报错，不静默跳过——
-- 与 320 同一口径：删列意味着有人做过决定，该由台账而不是守卫来记录）。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- ① 前置自证：三个列必须都在（缺一个就说明库结构不是本脚本预期的起点）
IF COL_LENGTH(N'dbo.MODULES', N'NEW_URL') IS NULL
    OR COL_LENGTH(N'dbo.MODULES', N'MODI_URL') IS NULL
    OR COL_LENGTH(N'dbo.MODULES', N'HELP_URL') IS NULL
    THROW 53100, N'MODULES 的 NEW_URL / MODI_URL / HELP_URL 不齐：本迁移的前提不成立（库结构已变或已删过）。', 1;

-- ② 不静默丢配置：NEW_URL 必须全空（实测 0/363；若真有值，先迁到 M_URL 或交给统一表单）
IF EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK)
           WHERE NULLIF(LTRIM(RTRIM(ISNULL(NEW_URL, N''))), N'') IS NOT NULL)
    THROW 53101, N'仍有模块配着 NEW_URL：先把它迁到统一表单（加进名单）或落成 M_URL 承载页，再删列。', 1;

-- ③ 不静默丢配置：MODI_URL 只允许三种形态——空、默认编辑模板、与本模块 M_URL 同值。
--    前两种是无信息量（等于回退值），第三种信息已由 M_URL 承载（实测只有 2201~2204 这 4 个）。
IF EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK)
           WHERE NULLIF(LTRIM(RTRIM(ISNULL(MODI_URL, N''))), N'') IS NOT NULL
             AND LTRIM(RTRIM(MODI_URL)) <> N'/workbench/{moduleId}/edit'
             AND LTRIM(RTRIM(MODI_URL)) <> LTRIM(RTRIM(ISNULL(M_URL, N''))))
    THROW 53102, N'仍有模块的 MODI_URL 既不是默认编辑模板、也不等于本模块 M_URL：先把它的编辑入口解释清楚（统一表单 or M_URL 承载页），再删列。', 1;

-- ④ HELP_URL：随列退休。先把值打印出来（执行日志即留痕），再清空。
DECLARE @helpTrail NVARCHAR(MAX) = N'';
SELECT @helpTrail = @helpTrail + N'  ' + CONVERT(nvarchar(20), M_IDX) + N' ' + ISNULL(M_DESC, N'')
                     + N' = ' + ISNULL(HELP_URL, N'') + CHAR(10)
FROM dbo.MODULES
WHERE NULLIF(LTRIM(RTRIM(ISNULL(HELP_URL, N''))), N'') IS NOT NULL;

IF LEN(@helpTrail) > 0
    PRINT N'== 随列退役的 HELP_URL 值（旧客户专属路径，运行时本就不可达）==' + CHAR(10) + @helpTrail;

UPDATE dbo.MODULES SET HELP_URL = NULL WHERE HELP_URL IS NOT NULL;

-- ⑤ 先摘挂在三列上的扩展属性（列注释），再删列
DECLARE @dropSql NVARCHAR(MAX) = N'';
SELECT @dropSql = @dropSql + N'EXEC sys.sp_dropextendedproperty @name = N'''
                  + REPLACE(ep.name, N'''', N'''''') + N''', @level0type = N''SCHEMA'', @level0name = N''dbo'''
                  + N', @level1type = N''TABLE'', @level1name = N''MODULES'''
                  + N', @level2type = N''COLUMN'', @level2name = N''' + c.name + N''';' + CHAR(10)
FROM sys.extended_properties ep
JOIN sys.columns c ON c.object_id = ep.major_id AND c.column_id = ep.minor_id
WHERE ep.major_id = OBJECT_ID(N'dbo.MODULES')
  AND c.name IN (N'NEW_URL', N'MODI_URL', N'HELP_URL');

IF @dropSql <> N'' EXEC sp_executesql @dropSql;

-- ⑥ 删列
ALTER TABLE dbo.MODULES DROP COLUMN NEW_URL, MODI_URL, HELP_URL;

-- ⑦ 字段元数据行（FIELDS 里描述这三列的行）随列一起走：它们描述的是已经不存在的列
DELETE FROM dbo.FIELDS WHERE T_ID = N'MODULES' AND F_ID IN (N'NEW_URL', N'MODI_URL', N'HELP_URL');

-- ⑧ 后置自证：三列必须消失
IF COL_LENGTH(N'dbo.MODULES', N'NEW_URL') IS NOT NULL
    OR COL_LENGTH(N'dbo.MODULES', N'MODI_URL') IS NOT NULL
    OR COL_LENGTH(N'dbo.MODULES', N'HELP_URL') IS NOT NULL
    THROW 53110, N'三个 URL 列未被删除。', 1;

-- ⑨ 后置自证：该留的必须还在（M_URL 是唯一路由真源，其余是模块自身事实）
IF COL_LENGTH(N'dbo.MODULES', N'M_URL') IS NULL
    OR COL_LENGTH(N'dbo.MODULES', N'M_TAG') IS NULL
    OR COL_LENGTH(N'dbo.MODULES', N'MASTER_TABLE') IS NULL
    OR COL_LENGTH(N'dbo.MODULES', N'FORM_OPEN_MODE') IS NULL
    THROW 53111, N'不该删的列被删了（M_URL / M_TAG / MASTER_TABLE / FORM_OPEN_MODE 必须保留）。', 1;

-- ⑩ 后置自证：FIELDS 里不得再有这三列的元数据行
IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = N'MODULES' AND F_ID IN (N'NEW_URL', N'MODI_URL', N'HELP_URL'))
    THROW 53112, N'FIELDS 里仍留着已删列的元数据行。', 1;

COMMIT TRANSACTION;

PRINT N'== 模块路由收敛完成：只留 M_URL（承载页），NEW_URL / MODI_URL / HELP_URL 已物理删除 ==';
