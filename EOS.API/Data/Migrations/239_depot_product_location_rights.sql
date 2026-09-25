-- ============================================================================
-- EOS.ERP migration 240: 物料主货位模块的组权限（照同级主档授权）
-- ----------------------------------------------------------------------------
-- 订正迁移 238：那一步只建了模块与字段，**没授权**。结果是无头案——模块在菜单里、字段也齐，
-- 但每个访问入口都 404：`DocumentWorkbenchController` 的每一条路都要过
-- `IPermissionService`（`ModuleRightsRepository` → `SYSDH` 组权限 / `SYSDD` 个人权限），
-- 而全新模块一行权限都没有。**"没授权"与"没这个模块"在 HTTP 上都是 404**，所以
-- 光看状态码会以为是模块不存在，实际是权限空。
--
-- 授权口径：**照同级主档（110309 库位主档）授权**——凡是能看库位主档的组，也就能维护物料主货位。
-- 这不是"顺手放权"，而是这两个模块的定位完全一致（仓库管理下的基础主档），
-- 差别只在一张表登记"哪个料放哪个位"、另一张登记"有哪些位"；把新主档单独扣住会让它无人能维护。
-- 将来若要按组细化，改的是这一行，不是代码。
--
-- 幂等：按 (G_IDX, M_IDX) 判重，可重复执行。
-- ============================================================================

INSERT INTO dbo.SYSDH
    (G_IDX, M_IDX, EXEC_TAG, ADDNEW_TAG, DELETE_TAG, EDIT_TAG, REPORT_TAG, COST_TAG, SETUP_TAG, SECRECY_TAG,
     ENDCASE_TAG, UNENDCASE_TAG, OTHER1_TAG, OTHER2_TAG, OTHER3_TAG, OTHER4_TAG,
     DENY_VIEW_FIELD_MASTER, DENY_VIEW_FIELD_DETAIL, DENY_NEW_FIELD_MASTER, DENY_NEW_FIELD_DETAIL,
     DENY_MODI_FIELD_MASTER, DENY_MODI_FIELD_DETAIL, DATA_FILTER, CI, OPERFLAG,
     APPROVE_TAG, DEAPPROVE_TAG, FILE_VIEW_TAG, FILE_UPDA_TAG, FILE_EDIT_TAG, FILE_DELE_TAG,
     FORM_DESIGN_TAG, FORM_ADJUST_TAG, MODULE_CONFIG_TAG)
SELECT src.G_IDX, 110311, src.EXEC_TAG, src.ADDNEW_TAG, src.DELETE_TAG, src.EDIT_TAG, src.REPORT_TAG,
       src.COST_TAG, src.SETUP_TAG, src.SECRECY_TAG,
       src.ENDCASE_TAG, src.UNENDCASE_TAG, src.OTHER1_TAG, src.OTHER2_TAG, src.OTHER3_TAG, src.OTHER4_TAG,
       src.DENY_VIEW_FIELD_MASTER, src.DENY_VIEW_FIELD_DETAIL, src.DENY_NEW_FIELD_MASTER, src.DENY_NEW_FIELD_DETAIL,
       src.DENY_MODI_FIELD_MASTER, src.DENY_MODI_FIELD_DETAIL, src.DATA_FILTER, src.CI, src.OPERFLAG,
       src.APPROVE_TAG, src.DEAPPROVE_TAG, src.FILE_VIEW_TAG, src.FILE_UPDA_TAG, src.FILE_EDIT_TAG, src.FILE_DELE_TAG,
       src.FORM_DESIGN_TAG, src.FORM_ADJUST_TAG, src.MODULE_CONFIG_TAG
  FROM dbo.SYSDH src
 WHERE src.M_IDX = 110309
   AND NOT EXISTS (SELECT 1 FROM dbo.SYSDH x WHERE x.M_IDX = 110311 AND x.G_IDX = src.G_IDX);

DECLARE @sibling INT = (SELECT COUNT(*) FROM dbo.SYSDH WHERE M_IDX = 110309);
DECLARE @mine INT = (SELECT COUNT(*) FROM dbo.SYSDH WHERE M_IDX = 110311);
IF @mine < @sibling
    THROW 52113, N'物料主货位的组权限没有覆盖到所有能看库位主档的组，迁移中止。', 1;
IF @mine = 0
    THROW 52114, N'物料主货位没有任何组权限——模块会以 404 的形式"消失"，迁移中止。', 1;

PRINT N'== 就位：物料主货位已照库位主档授权 ==';
