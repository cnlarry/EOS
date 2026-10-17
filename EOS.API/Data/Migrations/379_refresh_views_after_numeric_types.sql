-- 379：刷新引用业务数值列的视图元数据
-- 批 A（359-367）与批 B（369-378）把 float 改成 decimal 后，
-- 视图的列类型元数据不会自动更新，需 sp_refreshview 重新绑定。
-- 幂等：sp_refreshview 可重复执行。

EXEC sp_refreshview N'dbo.V_COP_ACCOUNT_M';
EXEC sp_refreshview N'dbo.V_COP_SEND_D_UNRECONCILED';
EXEC sp_refreshview N'dbo.V_INV_MONTH_SNAPSHOT_RECON';
EXEC sp_refreshview N'dbo.V_MOC_GET_SHOWSUM';
EXEC sp_refreshview N'dbo.V_ORDER_SHEET';
EXEC sp_refreshview N'dbo.V_PUR_DUE_M';
EXEC sp_refreshview N'dbo.V_PUR_RECEIVE_D_UNRECONCILED';
EXEC sp_refreshview N'dbo.V_TODAY_SEND';
EXEC sp_refreshview N'dbo.V_UNUSUAL_ORDER';
GO
