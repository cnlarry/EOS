-- 校验规则消息对齐：MODULE_VALIDATION_RULE.MESSAGE 与 PARAM_STRUCT 内的消息同源同文。
--
-- 背景：引擎渲染拒绝文案用的是 PARAM_STRUCT 里的 message（reference-exists 等按 checks[].message，
-- 定制校验按 check.message / check.deleteMessage），而列 MESSAGE 是规则列表与管理界面的展示面。
-- 两处曾经不一致：10425 的列少了 {ROWS} 占位；10448 / 10449 的列干脆为空，消息只写在 PARAM_STRUCT。
-- 后果不是行为错误，而是"同一事实有两份表述"——诊断、审计与规则列表可能各说一句话。
--
-- 只订正这三条（按 RULE_ID 精确匹配）：
--   10425  产品BOM表 SAVE/3  line-require       → 补回 "\r\n{ROWS}" 占位
--   10448  库位主档 SAVE/1  custom-validation   → 取 PARAM_STRUCT.check.message
--   10449  库位主档 DELETE/1 custom-validation  → 取 PARAM_STRUCT.check.deleteMessage
-- 幂等：目标值与现状相同则不动；规则若已被删除，UPDATE 自然 0 行。

UPDATE dbo.MODULE_VALIDATION_RULE
SET MESSAGE = N'以下序号项元件底数不能小于0 ' + CHAR(13) + CHAR(10) + N'{ROWS}',
    MODIFIED_BY = N'MIGRATION',
    MODIFIED_TIME = SYSDATETIME()
WHERE RULE_ID = 10425
  AND (MESSAGE IS NULL OR MESSAGE <> N'以下序号项元件底数不能小于0 ' + CHAR(13) + CHAR(10) + N'{ROWS}');

UPDATE dbo.MODULE_VALIDATION_RULE
SET MESSAGE = N'未指定位置（哨兵行）必须保持原样，不允许改上级 / 改类型 / 改路径 / 停用：',
    MODIFIED_BY = N'MIGRATION',
    MODIFIED_TIME = SYSDATETIME()
WHERE RULE_ID = 10448
  AND (MESSAGE IS NULL OR MESSAGE <> N'未指定位置（哨兵行）必须保持原样，不允许改上级 / 改类型 / 改路径 / 停用：');

UPDATE dbo.MODULE_VALIDATION_RULE
SET MESSAGE = N'未指定位置（哨兵行）不允许删除：该库别的库存余额与过账都指向它。',
    MODIFIED_BY = N'MIGRATION',
    MODIFIED_TIME = SYSDATETIME()
WHERE RULE_ID = 10449
  AND (MESSAGE IS NULL OR MESSAGE <> N'未指定位置（哨兵行）不允许删除：该库别的库存余额与过账都指向它。');
