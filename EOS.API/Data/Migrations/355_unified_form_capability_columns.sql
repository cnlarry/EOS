-- ============================================================================
-- EOS.ERP migration 355: 统一表单能力进库（UNIFIED_FORM_ENABLED / UNIFIED_FORM_EDIT_ENABLED）
--                —— 退役 appsettings 的 UnifiedFormEditor 名单
-- ----------------------------------------------------------------------------
-- 背景：在这之前"某个模块能不能用统一表单（浏览 / 新增 / 编辑）"由 appsettings.json 的两个
-- 名单回答（`EnabledModuleIds` 写名单 = 可新增修改删除；`ReadOnlyModuleIds` 只读名单 = 只能浏览）。
-- 那套机制有三处不合身：
--   ① 它是**部署配置**，改一次要重启 API 才生效（IOptions 是启动快照），而它回答的是"这个模块
--      有没有表单"这种**业务/结构**问题——与该问题同类的"模块有没有批核入口"早在 ADR-035 就
--      改成库列 `MODULES.APPROVE_ENABLED` 现读了（不进定义快照），两处口径不一致；
--   ② 名单在库外，库里查不到、审计里看不见、发布管理里也读不到它的来源；
--   ③ 它把两件本来正交的事压成一个名单：**表单开不开**与**表单里能不能写**。
--      现实里两者会分开——审计（AUDIT_EVENT）、库存账（INV_PRO_DEPOT / INV_BATCH_M）、
--      应收未收明细（COP_ACCOUNT_M）这些表**表单要能打开看（含子表）**，
--      但通用表单的写入必须封死（写入方是审计写入器与库存/账务引擎）。
--
-- 本迁移把这件事搬进 MODULES 的两列（与 APPROVE_ENABLED 同口径：**现读、不进定义快照**，
-- 因此改完即刻生效，不需要标脏/重发布）：
--   `UNIFIED_FORM_ENABLED`      = 统一表单可用（浏览、新增、编辑的入口都在它里面）
--   `UNIFIED_FORM_EDIT_ENABLED` = 表单里允许写入（新增/修改/删除）
--   约束 `CK_MODULES_UNIFIED_FORM_EDIT`：未启用统一表单 ⇒ 写入位必须为 0（不启用表单却"可写"没有意义）
--
-- 回填口径（**行为等值**，与今天的名单一一对应）：
--   原写名单 259 个 → (1,1)；原只读名单 4 个（1302/1303/14997/2316）→ (1,0)；
--   其余保持 (0,0)，其中 20 个是"只该有列表、没有表单"的**汇总/查询类**模块
--   （库存日志、安全库存预警、今日需入库、应收未收汇总、考勤日报…——它们的"表单"是查询面，
--   双击进去只会得到一个写不动的编辑器），另 4 类是非工作台形态（目录节点 / 自定义承载页 / 报表页），
--   本就不装配工作台定义。
--
-- 名单退役后，"未评审模块不能写"的闸并没有变松：原来是"不在名单里"（默认关），
-- 现在是**两列默认 0**（默认关），要显式在 2301 菜单管理里打开，且该写路径的改动进审计。
-- 同批必须删掉 appsettings.json 的 `UnifiedFormEditor` 段与 `UnifiedFormEditorSettings` 类型——
-- 留着就是一处没人读的配置（有人改它、以为放量了，其实没有）。
--
-- 幂等：列/约束按存在性判断；回填只在"两列全 0"（尚未回填）时执行，可重复运行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GuardMessage NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 56230, @GuardMessage, 1;

IF OBJECT_ID(N'dbo.MODULES', N'U') IS NULL
    THROW 56231, N'MODULES 表不存在，迁移中止。', 1;

IF OBJECT_ID(N'dbo.V_MODULE_NODE', N'V') IS NULL
    THROW 56232, N'形态判据视图 dbo.V_MODULE_NODE 不存在，迁移中止（回填按它区分工作台模块）。', 1;

BEGIN TRANSACTION;
GO

-- ===== 批次 1/2：加列与约束 =====
-- 必须与后面的回填**分批**：同一批次里"加列后立刻引用新列"，整批在编译期就会被判
-- 「列名无效」（编译先于执行，GO 之前的语句共享一次编译）。
/* ---------- 1. 两列 + 约束 ---------- */
IF COL_LENGTH(N'dbo.MODULES', N'UNIFIED_FORM_ENABLED') IS NULL
BEGIN
    ALTER TABLE dbo.MODULES ADD UNIFIED_FORM_ENABLED BIT NOT NULL
        CONSTRAINT DF_MODULES_UNIFIED_FORM_ENABLED DEFAULT (0);
    PRINT N'== 已新增列 MODULES.UNIFIED_FORM_ENABLED（默认 0）==';
END
ELSE
    PRINT N'== 列 UNIFIED_FORM_ENABLED 已存在，跳过 ==';

IF COL_LENGTH(N'dbo.MODULES', N'UNIFIED_FORM_EDIT_ENABLED') IS NULL
BEGIN
    ALTER TABLE dbo.MODULES ADD UNIFIED_FORM_EDIT_ENABLED BIT NOT NULL
        CONSTRAINT DF_MODULES_UNIFIED_FORM_EDIT_ENABLED DEFAULT (0);
    PRINT N'== 已新增列 MODULES.UNIFIED_FORM_EDIT_ENABLED（默认 0）==';
END
ELSE
    PRINT N'== 列 UNIFIED_FORM_EDIT_ENABLED 已存在，跳过 ==';
GO

-- ===== 批次 2/3：约束（引用两列，必须等它们真实存在后再编译） =====

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
                WHERE name = N'CK_MODULES_UNIFIED_FORM_EDIT' AND parent_object_id = OBJECT_ID(N'dbo.MODULES'))
BEGIN
    ALTER TABLE dbo.MODULES ADD CONSTRAINT CK_MODULES_UNIFIED_FORM_EDIT
        CHECK (UNIFIED_FORM_EDIT_ENABLED = 0 OR UNIFIED_FORM_ENABLED = 1);
    PRINT N'== 已新增约束 CK_MODULES_UNIFIED_FORM_EDIT（未启用表单 ⇒ 不可写）==';
END
ELSE
    PRINT N'== 约束 CK_MODULES_UNIFIED_FORM_EDIT 已存在，跳过 ==';
GO

-- ===== 批次 3/3：列说明、回填与收口断言（两列已存在，可安全引用） =====

/* ---------- 2. 列说明（与既有列注释同风格：读的人不必翻代码） ---------- */
IF NOT EXISTS (SELECT 1 FROM sys.extended_properties
                WHERE major_id = OBJECT_ID(N'dbo.MODULES') AND minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.MODULES'), N'UNIFIED_FORM_ENABLED', 'ColumnId')
                  AND name = N'MS_Description')
    EXEC sys.sp_addextendedproperty @name = N'MS_Description',
        @value = N'统一表单可用：1 = 本模块有统一表单（浏览、新增、编辑的入口都在它里面），0 = 只有列表、双击不进表单。现读、不进定义快照。',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'MODULES',
        @level2type = N'COLUMN', @level2name = N'UNIFIED_FORM_ENABLED';

IF NOT EXISTS (SELECT 1 FROM sys.extended_properties
                WHERE major_id = OBJECT_ID(N'dbo.MODULES') AND minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.MODULES'), N'UNIFIED_FORM_EDIT_ENABLED', 'ColumnId')
                  AND name = N'MS_Description')
    EXEC sys.sp_addextendedproperty @name = N'MS_Description',
        @value = N'统一表单允许写入（新增/修改/删除）：0 = 表单只能浏览（表由系统/引擎维护，如审计、库存账）。未启用统一表单时该位必须为 0（约束 CK_MODULES_UNIFIED_FORM_EDIT）。',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'MODULES',
        @level2type = N'COLUMN', @level2name = N'UNIFIED_FORM_EDIT_ENABLED';

/* ---------- 3. 回填（只在尚未回填时执行） ---------- */
IF (SELECT COUNT(*) FROM dbo.MODULES WHERE UNIFIED_FORM_ENABLED = 1 OR UNIFIED_FORM_EDIT_ENABLED = 1) > 0
BEGIN
    PRINT N'== 已有模块置位（回填过或人工配置过），跳过回填 ==';
END
ELSE
BEGIN
    /* 3.1 只浏览：表单要能打开（含子表），通用写入必须封死——写入方是审计写入器与库存/账务引擎 */
    UPDATE dbo.MODULES
       SET UNIFIED_FORM_ENABLED = 1, UNIFIED_FORM_EDIT_ENABLED = 0,
           LAST_UPDATE_BY = N'migration-355', LAST_UPDATE_DATE = SYSDATETIME()
     WHERE M_IDX IN (1302, 1303, 14997, 2316);
    PRINT N'== 回填「仅浏览」模块 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 个 ==';

    /* 3.2 只有列表、无表单：汇总/查询类模块（它们的"表单"是查询面，双击进去是个写不动的编辑器），
       显式列出而不是"剩下的一律关"——避免把将来新增的模块默认当查询面 */
    UPDATE dbo.MODULES
       SET UNIFIED_FORM_ENABLED = 0, UNIFIED_FORM_EDIT_ENABLED = 0,
           LAST_UPDATE_BY = N'migration-355', LAST_UPDATE_DATE = SYSDATETIME()
     WHERE M_IDX IN (1305, 1311, 1428, 1520, 3007, 3008, 14989, 14993, 14996, 14998, 14999,
                     16998, 16999, 170206, 170293, 170297, 170299, 180214, 180218, 180652);
    PRINT N'== 回填「无表单」模块 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 个 ==';

    /* 3.3 其余工作台模块：原写名单（259 个）→ 表单可用且可写。
       判据是"形态 + 不在上面两类例外里"，不硬编码 259 个编号（换库时编号集合相同、数量可不同） */
    UPDATE m
       SET m.UNIFIED_FORM_ENABLED = 1, m.UNIFIED_FORM_EDIT_ENABLED = 1,
           m.LAST_UPDATE_BY = N'migration-355', m.LAST_UPDATE_DATE = SYSDATETIME()
      FROM dbo.MODULES m
      JOIN dbo.V_MODULE_NODE n ON n.M_IDX = m.M_IDX
     WHERE n.NODE_KIND = N'WORKBENCH'
       AND m.M_IDX NOT IN (1302, 1303, 14997, 2316,
                           1305, 1311, 1428, 1520, 3007, 3008, 14989, 14993, 14996, 14998, 14999,
                           16998, 16999, 170206, 170293, 170297, 170299, 180214, 180218, 180652);
    PRINT N'== 回填「表单可用且可写」模块 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 个 ==';
END

/* ---------- 4. 收口断言 ---------- */
-- 非法组合：不启用表单却"可写"
IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE UNIFIED_FORM_ENABLED = 0 AND UNIFIED_FORM_EDIT_ENABLED = 1)
    THROW 56233, N'存在"未启用统一表单却允许写入"的模块，迁移中止。', 1;

-- 非工作台形态（目录节点 / 自定义承载页 / 报表页）不得启用表单：它们不装配工作台定义
IF EXISTS (SELECT 1 FROM dbo.MODULES m
            JOIN dbo.V_MODULE_NODE n ON n.M_IDX = m.M_IDX
           WHERE n.NODE_KIND <> N'WORKBENCH' AND m.UNIFIED_FORM_ENABLED = 1)
    THROW 56234, N'非工作台形态的模块启用了统一表单，迁移中止（只有工作台模块有表单面）。', 1;

-- 例子清单必须都还在（编号不回收；换库时若整模块退役，这条会红出来让人改清单）
DECLARE @expected TABLE (M_IDX INT);
INSERT INTO @expected (M_IDX) VALUES
    (1302), (1303), (14997), (2316),
    (1305), (1311), (1428), (1520), (3007), (3008), (14989), (14993), (14996), (14998), (14999),
    (16998), (16999), (170206), (170293), (170297), (170299), (180214), (180218), (180652);
DECLARE @missingIDs NVARCHAR(400) = N'';
SELECT @missingIDs = @missingIDs + CONVERT(NVARCHAR(10), e.M_IDX) + N','
  FROM @expected e WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = e.M_IDX);
IF LEN(@missingIDs) > 0
    THROW 56235, N'回填清单里的模块已不存在，请按当前模块集合修正本迁移，迁移中止。', 1;

-- 「仅浏览」清单必须停在 (1,0)：审计与库存/账务账本不允许通用写入
IF EXISTS (SELECT 1 FROM dbo.MODULES
            WHERE M_IDX IN (1302, 1303, 14997, 2316)
              AND NOT (UNIFIED_FORM_ENABLED = 1 AND UNIFIED_FORM_EDIT_ENABLED = 0))
    THROW 56236, N'审计/库存账本模块未停在「仅浏览」，迁移中止。', 1;

-- 计数自证：工作台模块 = 「表单可用且可写」∪「仅浏览」∪「无表单」，且两两不交
DECLARE @workbench INT = (SELECT COUNT(*) FROM dbo.V_MODULE_NODE WHERE NODE_KIND = N'WORKBENCH');
DECLARE @writable INT = (SELECT COUNT(*) FROM dbo.MODULES WHERE UNIFIED_FORM_ENABLED = 1 AND UNIFIED_FORM_EDIT_ENABLED = 1);
DECLARE @readonlyForms INT = (SELECT COUNT(*) FROM dbo.MODULES WHERE UNIFIED_FORM_ENABLED = 1 AND UNIFIED_FORM_EDIT_ENABLED = 0);
-- 「无表单」只数工作台模块：非工作台形态（目录节点 / 自定义承载页 / 报表页）本来就是 0/0，
-- 混进来这条等式永远不成立。
DECLARE @noForm INT = (SELECT COUNT(*) FROM dbo.MODULES m
                        JOIN dbo.V_MODULE_NODE n ON n.M_IDX = m.M_IDX
                       WHERE n.NODE_KIND = N'WORKBENCH'
                         AND m.UNIFIED_FORM_ENABLED = 0 AND m.UNIFIED_FORM_EDIT_ENABLED = 0);
IF @workbench <> @writable + @readonlyForms + @noForm
    THROW 56237, N'能力位与模块形态对不上：工作台模块数 ≠ 三类之和，迁移中止。', 1;

IF @readonlyForms <> 4
    THROW 56238, N'「仅浏览」模块不是 4 个（审计 + 库存账 + 应收未收明细），迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：工作台模块 ' + CONVERT(NVARCHAR(10), @workbench) + N' 个 = 表单可用且可写 '
      + CONVERT(NVARCHAR(10), @writable) + N' + 仅浏览 ' + CONVERT(NVARCHAR(10), @readonlyForms)
      + N' + 无表单 ' + CONVERT(NVARCHAR(10), @noForm) + N'（另含非工作台形态模块，均为 0/0）==';
PRINT N'== 提醒：appsettings.json 的 UnifiedFormEditor 段与 UnifiedFormEditorSettings 类型必须同批删除，'
      + N'否则留着一处没人读的配置（改它的人会以为放量了）==';
