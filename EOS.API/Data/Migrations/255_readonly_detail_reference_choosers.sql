/* =============================================================================
 * 只读明细「引用/数量」列的可提交入口。
 *
 * 背景：只读收口（RecordPayloadValidator.ValidateSubmitted）对「只读 + 非必填 + 无活动选择器」
 * 的字段一律拒绝，用来挡"构造请求改写只读列"。但一批**引用列**（本行引用来源单据的一张明细行）
 * 与**业务量列**因此没有合法入口：前端不提交、服务端也不代填，建单时直接 400，业务量随之丢失。
 *
 * 本迁移只处理被 E2E 实测撞到的那批（不是全库一刀切）：
 *   · 引用列 15 个 → 补**活动选择器**（FIELD_DATASOURCE 加行），走收口对"有活动选择器"的既有豁免；
 *     语义就是"这个值本就从来源单据选出来"，与 170101 应收货款单的现状一致。
 *   · 业务量列 2 个 → 订正元数据 IS_READONLY=0（用户本就该填的数，且业务效果/校验依赖它）。
 *   · 成本列（PUR_DUE_D.PRICE）**不在这里放开**：由服务端带出，见
 *     Data/Workbench/DetailReferenceColumnFiller.cs。
 *
 * 选择器过滤器一律"来源主档状态 + 来源明细未结案"，模板变量 {m.X} 只引用目标模块主表确实存在的列
 * （COP_SHIPMENT_M 没有 CLIENT_ID，所以 1408 不按客户过滤）；FILTER_STRUCT 必须非空，
 * 否则运行期选择器 fail-closed 返回空列表。
 *
 * 可重复执行：所有 INSERT 都带 NOT EXISTS 守卫，UPDATE 带现值守卫。
 * ============================================================================= */

SET NOCOUNT ON;

INSERT INTO dbo.FIELD_DATASOURCE (T_ID, F_ID, SERIAL_NO, ACTIVE_TAG, SOURCE_T_ID, SOURCE_DESC, SOURCE_M_IDX, FILTER_STRUCT, RETURN_ITEMS)
SELECT v.T_ID, v.F_ID, v.SERIAL_NO, 1, v.SOURCE_T_ID, v.SOURCE_DESC, v.SOURCE_M_IDX, v.FILTER_STRUCT, v.RETURN_ITEMS
  FROM (VALUES
    -- 1406 送货单：订单号 / 订单项次 ← 客户订单明细（按客户过滤）
    (N'COP_SEND_D', N'ORDER_NO', 1, N'COP_ORDER_D', N'客户订单明细', 1405,
     N'{"logic":"AND","items":[{"field":"COP_ORDER_M.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null},{"field":"COP_ORDER_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"COP_ORDER_D.FINISHED_TAG","operator":"EQ","value":"0","nullSafe":null}]}',
     N'[{"target":"ORDER_TYPE","column":"ORDER_TYPE"},{"target":"ORDER_NO","column":"ORDER_NO"},{"target":"ORDER_SERIAL_NO","column":"SERIAL_NO"},{"target":"PRO_NO","column":"PRO_NO"},{"target":"QTY","column":"QTY"},{"target":"PRICE","column":"PRICE"},{"target":"UNIT_ID","column":"UNIT_ID"}]'),
    (N'COP_SEND_D', N'ORDER_SERIAL_NO', 1, N'COP_ORDER_D', N'客户订单明细', 1405,
     N'{"logic":"AND","items":[{"field":"COP_ORDER_M.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null},{"field":"COP_ORDER_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"COP_ORDER_D.FINISHED_TAG","operator":"EQ","value":"0","nullSafe":null}]}',
     N'[{"target":"ORDER_TYPE","column":"ORDER_TYPE"},{"target":"ORDER_NO","column":"ORDER_NO"},{"target":"ORDER_SERIAL_NO","column":"SERIAL_NO"},{"target":"PRO_NO","column":"PRO_NO"},{"target":"QTY","column":"QTY"},{"target":"PRICE","column":"PRICE"},{"target":"UNIT_ID","column":"UNIT_ID"}]'),
    -- 1408 出货通知单：订单项次 ← 客户订单明细（COP_SHIPMENT_M 无 CLIENT_ID，不按客户过滤）
    (N'COP_SHIPMENT_D', N'ORDER_SERIAL_NO', 1, N'COP_ORDER_D', N'客户订单明细', 1405,
     N'{"logic":"AND","items":[{"field":"COP_ORDER_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"COP_ORDER_D.FINISHED_TAG","operator":"EQ","value":"0","nullSafe":null}]}',
     N'[{"target":"ORDER_TYPE","column":"ORDER_TYPE"},{"target":"ORDER_NO","column":"ORDER_NO"},{"target":"ORDER_SERIAL_NO","column":"SERIAL_NO"},{"target":"PRO_NO","column":"PRO_NO"},{"target":"QTY","column":"QTY"},{"target":"PRICE","column":"PRICE"},{"target":"UNIT_ID","column":"UNIT_ID"}]'),
    -- 1407 退货单 / 1409 扣款退货单：送货单号 / 送货项次 / 送货单别 ← 送货单明细
    (N'COP_RETURN_D', N'SEND_NO', 1, N'COP_SEND_D', N'送货单明细', 1406,
     N'{"logic":"AND","items":[{"field":"COP_SEND_M.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null},{"field":"COP_SEND_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"COP_SEND_D.FINISHED_TAG","operator":"EQ","value":"0","nullSafe":null}]}',
     N'[{"target":"SEND_TYPE","column":"SEND_TYPE"},{"target":"SEND_NO","column":"SEND_NO"},{"target":"SEND_SERIAL_NO","column":"SERIAL_NO"},{"target":"PRO_NO","column":"PRO_NO"},{"target":"QTY","column":"QTY"},{"target":"PRICE","column":"PRICE"},{"target":"UNIT_ID","column":"UNIT_ID"},{"target":"DEPOT_ID","column":"DEPOT_ID"}]'),
    (N'COP_RETURN_D', N'SEND_SERIAL_NO', 1, N'COP_SEND_D', N'送货单明细', 1406,
     N'{"logic":"AND","items":[{"field":"COP_SEND_M.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null},{"field":"COP_SEND_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"COP_SEND_D.FINISHED_TAG","operator":"EQ","value":"0","nullSafe":null}]}',
     N'[{"target":"SEND_TYPE","column":"SEND_TYPE"},{"target":"SEND_NO","column":"SEND_NO"},{"target":"SEND_SERIAL_NO","column":"SERIAL_NO"},{"target":"PRO_NO","column":"PRO_NO"},{"target":"QTY","column":"QTY"},{"target":"PRICE","column":"PRICE"},{"target":"UNIT_ID","column":"UNIT_ID"},{"target":"DEPOT_ID","column":"DEPOT_ID"}]'),
    (N'COP_RETURN_D', N'SEND_TYPE', 1, N'COP_SEND_D', N'送货单明细', 1406,
     N'{"logic":"AND","items":[{"field":"COP_SEND_M.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null},{"field":"COP_SEND_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"COP_SEND_D.FINISHED_TAG","operator":"EQ","value":"0","nullSafe":null}]}',
     N'[{"target":"SEND_TYPE","column":"SEND_TYPE"},{"target":"SEND_NO","column":"SEND_NO"},{"target":"SEND_SERIAL_NO","column":"SERIAL_NO"},{"target":"PRO_NO","column":"PRO_NO"},{"target":"QTY","column":"QTY"},{"target":"PRICE","column":"PRICE"},{"target":"UNIT_ID","column":"UNIT_ID"},{"target":"DEPOT_ID","column":"DEPOT_ID"}]'),
    -- 1407 / 1409：币别 ← 币别主档（与 COP_SEND_D.CURR_ID 同款）
    (N'COP_RETURN_D', N'CURR_ID', 1, N'CURR', N'币别基本资料', 0,
     N'{"logic":"AND","items":[]}',
     N'[{"target":"CURR_ID","column":"CURR_ID"},{"target":"CURR_RATE","column":"CURR_RATE"}]'),
    -- 1413 送货单回执：送(退)货单别 ← 送货单明细 / 客户退货明细（照 170101 应收货款单的既有两条）
    (N'COP_CALLBACK_D', N'S_R_TYPE', 1, N'COP_SEND_D', N'送货单明细', 1406,
     N'{"logic":"AND","items":[{"field":"COP_SEND_M.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null},{"field":"COP_SEND_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"COP_SEND_D.FINISHED_TAG","operator":"EQ","value":"0","nullSafe":null}]}',
     N'[{"target":"S_R_TYPE","column":"SEND_TYPE"},{"target":"S_R_NO","column":"SEND_NO"},{"target":"S_R_SERIAL_NO","column":"SERIAL_NO"},{"target":"PRO_NO","column":"PRO_NO"},{"target":"QTY","column":"QTY"},{"target":"PRICE","column":"PRICE"},{"target":"UNIT_ID","column":"UNIT_ID"}]'),
    (N'COP_CALLBACK_D', N'S_R_TYPE', 2, N'COP_RETURN_D', N'客户退货明细', 0,
     N'{"logic":"AND","items":[{"field":"COP_RETURN_M.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null},{"field":"COP_RETURN_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null}]}',
     N'[{"target":"S_R_TYPE","column":"RETURN_TYPE"},{"target":"S_R_NO","column":"RETURN_NO"},{"target":"S_R_SERIAL_NO","column":"SERIAL_NO"},{"target":"PRO_NO","column":"PRO_NO"},{"target":"QTY","column":"QTY"},{"target":"PRICE","column":"PRICE"},{"target":"UNIT_ID","column":"UNIT_ID"}]'),
    -- 1413：币别 ← 币别主档
    (N'COP_CALLBACK_D', N'CURR_ID', 1, N'CURR', N'币别基本资料', 0,
     N'{"logic":"AND","items":[]}',
     N'[{"target":"CURR_ID","column":"CURR_ID"},{"target":"CURR_RATE","column":"CURR_RATE"}]'),
    -- 1607 收料单：采购项次 ← 采购单明细
    (N'PUR_RECEIVE_D', N'PURCHASE_SERIAL_NO', 1, N'PUR_PURCHASE_D', N'采购单明细', 1606,
     N'{"logic":"AND","items":[{"field":"PUR_PURCHASE_M.SUPPLIER_ID","operator":"EQ","value":"{m.SUPPLIER_ID}","nullSafe":null},{"field":"PUR_PURCHASE_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PUR_PURCHASE_D.FINISHED_TAG","operator":"EQ","value":"0","nullSafe":null}]}',
     N'[{"target":"PURCHASE_TYPE","column":"PURCHASE_TYPE"},{"target":"PURCHASE_NO","column":"PURCHASE_NO"},{"target":"PURCHASE_SERIAL_NO","column":"SERIAL_NO"},{"target":"PRO_NO","column":"PRO_NO"},{"target":"QTY","column":"QTY"},{"target":"PRICE","column":"PRICE"},{"target":"UNIT_ID","column":"UNIT_ID"}]'),
    -- 1608 退料单 / 1612 扣款退料单：采购项次与收料项次 ← 采购单明细 / 收料单明细
    (N'PUR_CANCEL_D', N'PURCHASE_SERIAL_NO', 1, N'PUR_PURCHASE_D', N'采购单明细', 1606,
     N'{"logic":"AND","items":[{"field":"PUR_PURCHASE_M.SUPPLIER_ID","operator":"EQ","value":"{m.SUPPLIER_ID}","nullSafe":null},{"field":"PUR_PURCHASE_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PUR_PURCHASE_D.FINISHED_TAG","operator":"EQ","value":"0","nullSafe":null}]}',
     N'[{"target":"PURCHASE_TYPE","column":"PURCHASE_TYPE"},{"target":"PURCHASE_NO","column":"PURCHASE_NO"},{"target":"PURCHASE_SERIAL_NO","column":"SERIAL_NO"},{"target":"PRO_NO","column":"PRO_NO"},{"target":"QTY","column":"QTY"},{"target":"PRICE","column":"PRICE"},{"target":"UNIT_ID","column":"UNIT_ID"}]'),
    (N'PUR_CANCEL_D', N'RECEIVE_SERIAL_NO', 1, N'PUR_RECEIVE_D', N'收料单明细', 1607,
     N'{"logic":"AND","items":[{"field":"PUR_RECEIVE_M.SUPPLIER_ID","operator":"EQ","value":"{m.SUPPLIER_ID}","nullSafe":null},{"field":"PUR_RECEIVE_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null}]}',
     N'[{"target":"RECEIVE_TYPE","column":"RECEIVE_TYPE"},{"target":"RECEIVE_NO","column":"RECEIVE_NO"},{"target":"RECEIVE_SERIAL_NO","column":"SERIAL_NO"},{"target":"PRO_NO","column":"PRO_NO"},{"target":"QTY","column":"QTY"},{"target":"PRICE","column":"PRICE"},{"target":"UNIT_ID","column":"UNIT_ID"}]'),
    -- 170203 预付帐款单：采购单号 / 采购序号 ← 采购单明细
    (N'PUR_PREPAY_D', N'PURCHASE_NO', 1, N'PUR_PURCHASE_D', N'采购单明细', 1606,
     N'{"logic":"AND","items":[{"field":"PUR_PURCHASE_M.SUPPLIER_ID","operator":"EQ","value":"{m.SUPPLIER_ID}","nullSafe":null},{"field":"PUR_PURCHASE_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PUR_PURCHASE_D.FINISHED_TAG","operator":"EQ","value":"0","nullSafe":null}]}',
     N'[{"target":"PURCHASE_TYPE","column":"PURCHASE_TYPE"},{"target":"PURCHASE_NO","column":"PURCHASE_NO"},{"target":"PURCHASE_SERIAL_NO","column":"SERIAL_NO"},{"target":"PRO_NO","column":"PRO_NO"}]'),
    (N'PUR_PREPAY_D', N'PURCHASE_SERIAL_NO', 1, N'PUR_PURCHASE_D', N'采购单明细', 1606,
     N'{"logic":"AND","items":[{"field":"PUR_PURCHASE_M.SUPPLIER_ID","operator":"EQ","value":"{m.SUPPLIER_ID}","nullSafe":null},{"field":"PUR_PURCHASE_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PUR_PURCHASE_D.FINISHED_TAG","operator":"EQ","value":"0","nullSafe":null}]}',
     N'[{"target":"PURCHASE_TYPE","column":"PURCHASE_TYPE"},{"target":"PURCHASE_NO","column":"PURCHASE_NO"},{"target":"PURCHASE_SERIAL_NO","column":"SERIAL_NO"},{"target":"PRO_NO","column":"PRO_NO"}]'),
    -- 170201 应付货款单：收料项次 ← 收料单明细
    (N'PUR_DUE_D', N'RECEIVE_SERIAL_NO', 1, N'PUR_RECEIVE_D', N'收料单明细', 1607,
     N'{"logic":"AND","items":[{"field":"PUR_RECEIVE_M.SUPPLIER_ID","operator":"EQ","value":"{m.SUPPLIER_ID}","nullSafe":null},{"field":"PUR_RECEIVE_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null}]}',
     N'[{"target":"RECEIVE_TYPE","column":"RECEIVE_TYPE"},{"target":"RECEIVE_NO","column":"RECEIVE_NO"},{"target":"RECEIVE_SERIAL_NO","column":"SERIAL_NO"},{"target":"PRO_NO","column":"PRO_NO"},{"target":"UNIT_ID","column":"UNIT_ID"}]')
  ) v (T_ID, F_ID, SERIAL_NO, SOURCE_T_ID, SOURCE_DESC, SOURCE_M_IDX, FILTER_STRUCT, RETURN_ITEMS)
 WHERE NOT EXISTS (SELECT 1 FROM dbo.FIELD_DATASOURCE d
                    WHERE d.T_ID = v.T_ID AND d.F_ID = v.F_ID AND d.SERIAL_NO = v.SERIAL_NO);

-- 库里已有一条**没启用**的选择器行：COP_RETURN_D.CURR_ID（SERIAL_NO=1、SOURCE_T_ID 已是 CURR，
-- 只是 ACTIVE_TAG=0 且回填映射为空）。上面的插入按"同 (表,列,序号) 缺行才插"守卫会跳过它，
-- 于是这一列仍然没有合法入口 —— 这里把它补齐启用。
UPDATE dbo.FIELD_DATASOURCE
   SET ACTIVE_TAG = 1,
       FILTER_STRUCT = CASE WHEN ISNULL(FILTER_STRUCT, N'') = N'' THEN N'{"logic":"AND","items":[]}' ELSE FILTER_STRUCT END,
       RETURN_ITEMS = CASE WHEN ISNULL(RETURN_ITEMS, N'') = N''
                           THEN N'[{"target":"CURR_ID","column":"CURR_ID"},{"target":"CURR_RATE","column":"CURR_RATE"}]'
                           ELSE RETURN_ITEMS END
 WHERE T_ID = N'COP_RETURN_D' AND F_ID = N'CURR_ID' AND ISNULL(ACTIVE_TAG, 0) = 0;

-- 业务量列：用户本就该填的数，且业务效果/校验依赖它——改成可写（读侧不改判据）
--   · CUS_EXPORT_D.QTY：出口报关明细数量，手册 EXP_QTY 的累计来源（不可填则累计恒为 0）
--   · PUR_DUE_D.QTY   ：应付对账数量，qty-not-exceed 校验的就是它、detail-rollup 也汇总它
UPDATE dbo.FIELDS SET IS_READONLY = 0, LAST_UPDATE_BY = N'migration-253', LAST_UPDATE_DATE = SYSDATETIME()
 WHERE T_ID = N'CUS_EXPORT_D' AND F_ID = N'QTY' AND ISNULL(IS_READONLY, 0) = 1;

UPDATE dbo.FIELDS SET IS_READONLY = 0, LAST_UPDATE_BY = N'migration-253', LAST_UPDATE_DATE = SYSDATETIME()
 WHERE T_ID = N'PUR_DUE_D' AND F_ID = N'QTY' AND ISNULL(IS_READONLY, 0) = 1;

-- 元数据变了，受影响模块的定义快照即落后：标脏（与 WorkbenchDirtyMarker 同一形状）。
-- 干净模块的定义读自已发布快照，不标脏/不重发布就看不到新选择器；发布通过后脏标记会被清掉。
MERGE dbo.WORKBENCH_MODULE_DIRTY AS t
USING (SELECT DISTINCT M_IDX FROM dbo.MODULES
        WHERE M_IDX IN (1406, 1407, 1408, 1409, 1413, 1607, 1608, 1612, 170201, 170203, 300301, 300304)) AS s
   ON t.M_IDX = s.M_IDX
WHEN MATCHED THEN
    UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = N'migration-253', LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (s.M_IDX, 1, N'migration-253', SYSDATETIME());
