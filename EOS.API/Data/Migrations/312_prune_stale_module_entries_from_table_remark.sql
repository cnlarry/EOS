-- ============================================================================
-- EOS.ERP migration 312: 表描述去掉 MODULES 中不存在的模块条目
-- ----------------------------------------------------------------------------
--  背景：`TABLES.T_REMARK` 记录「这张表被哪些模块当作主表」。历史上从现实库导出元数据时，
--  描述里混进了 15 个 `MODULES` 中并不存在的模块号——其中 1208 / 2406 后缀带客户代号，
--  1510 与 2405 直接以客户代号命名，属客户定制痕迹；其余是与现役模块同名的历史重号
--  （如 1207 与 1204 都叫「产品BOM表」）。按 `M_IDX` 与 `M_P_IDX` 双向查证，这些号均无对应行。
--
--  改动：按**模块号**删除描述里的条目，而不是按名称匹配——同一模块号在库内与建库种子里
--  可能后缀不同（名称调整过），按号删才能两种形态都覆盖。条目形态为 `NNNN:名称；`，
--  故从该号起删到其后第一个全角分号。
--  影响面：`T_REMARK` 是备注文本，不参与运行时解析；无表结构、字段与其他元数据改动。
--  幂等：删掉后该号不再命中，重复执行无副作用。
-- ============================================================================

DECLARE @stale TABLE (M int PRIMARY KEY);
INSERT @stale (M) VALUES
    (1206), (1207), (1208), (1210), (1406), (1415), (1416), (1505),
    (1510), (2405), (2406), (2816), (290103), (290104), (290105);

DECLARE @m int, @pat nvarchar(20);
DECLARE stale_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT M FROM @stale ORDER BY M;

OPEN stale_cursor;
FETCH NEXT FROM stale_cursor INTO @m;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @pat = CAST(@m AS nvarchar(20)) + N':';

    UPDATE dbo.TABLES
    SET T_REMARK = STUFF(T_REMARK,
                         CHARINDEX(@pat, T_REMARK),
                         CHARINDEX(N'；', T_REMARK, CHARINDEX(@pat, T_REMARK)) - CHARINDEX(@pat, T_REMARK) + 1,
                         N'')
    WHERE CHARINDEX(@pat, T_REMARK) > 0
      AND CHARINDEX(N'；', T_REMARK, CHARINDEX(@pat, T_REMARK)) > CHARINDEX(@pat, T_REMARK);

    FETCH NEXT FROM stale_cursor INTO @m;
END;

CLOSE stale_cursor;
DEALLOCATE stale_cursor;
GO
