-- 个人列宽：列宽是操作习惯，与"选择列"同属个人配置，故宽度挂在 SYSQL_FIELDS 上（F_WIDTH），
-- 不新增表。NULL = 该列没有个人宽度，取字段元数据的 DISPLAY_LENGTH（也是所有人的默认宽度），
-- 故本次不做数据迁移：存量观感不变。
ALTER TABLE dbo.SYSQL_FIELDS ADD F_WIDTH int NULL;
