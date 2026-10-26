/*
 * 补齐迁移 451 漏掉的两处字段元数据引用。
 *
 * 1) FIELDS.BROWSE_URL 的列占位符：`~/Admin/MagEmployee.aspx?IDX=EMP_ID={CONFIRM_PERSON}` 这类
 *    "点开看这个人"的跳转串里写的是**列名占位符**，运行期按字段代号替换。列改名后占位符仍是旧名，
 *    点经办人链接会取不到值（静默变成空参数），故一并按精确 token 替换。
 * 2) F_REMARK 的中文别称：建立/修改/批核/结案四组在旧系统里叫"确认人 / 确认日期 / 确认状态"，
 *    字段名已收敛为 APPROVED_* / FINISHED_*，备注里继续写"确认"会与字段名对不上。
 *    只做**整串精确匹配**，不碰自由文本备注。
 *
 * 历史审计（AUDIT_FIELD_CHANGE.FIELD_NAME、AUDIT_EVENT.DETAIL_JSON）与非当前快照按 ADR-043 保持原样。
 */

SET NOCOUNT ON;

-- 1) 跳转串占位符：同一套旧名 → 新名（顺序与迁移 451 的映射一致）
UPDATE dbo.FIELDS
SET BROWSE_URL = REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                 REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                 BROWSE_URL,
                 N'CREATE_PERSON', N'CREATED_BY'),
                 N'CREATE_DATE', N'CREATED_TIME'),
                 N'CREATE_BY', N'CREATED_BY'),
                 N'CREATED_AT', N'CREATED_TIME'),
                 N'CREATED_DATE', N'CREATED_TIME'),
                 N'LAST_UPDATE_BY', N'MODIFIED_BY'),
                 N'LAST_UPDATE_DATE', N'MODIFIED_TIME'),
                 N'LAST_MODIFIED_BY', N'MODIFIED_BY'),
                 N'LAST_MODIFIED_AT', N'MODIFIED_TIME'),
                 N'UPDATED_BY', N'MODIFIED_BY'),
                 N'UPDATED_AT', N'MODIFIED_TIME'),
                 N'CONFIRM_PERSON', N'APPROVED_BY'),
                 N'CONFIRM_DATE', N'APPROVED_TIME'),
                 N'CONFIRM_TAG', N'APPROVED'),
                 N'FINISHED_PERSON', N'FINISHED_BY'),
                 N'FINISHED_DATE', N'FINISHED_TIME'),
                 N'FINISHED_TAG', N'FINISHED'),
                 N'CONFIRMED_BY', N'APPROVED_BY'),
                 N'CONFIRMED_DATE', N'APPROVED_TIME')
WHERE BROWSE_URL IS NOT NULL
  AND (CHARINDEX(N'CONFIRM_', BROWSE_URL) > 0
    OR CHARINDEX(N'CREATE_', BROWSE_URL) > 0
    OR CHARINDEX(N'LAST_UPDATE_', BROWSE_URL) > 0
    OR CHARINDEX(N'LAST_MODIFIED_', BROWSE_URL) > 0
    OR CHARINDEX(N'UPDATED_', BROWSE_URL) > 0
    OR CHARINDEX(N'FINISHED_PERSON', BROWSE_URL) > 0
    OR CHARINDEX(N'FINISHED_DATE', BROWSE_URL) > 0
    OR CHARINDEX(N'CONFIRMED_', BROWSE_URL) > 0);

-- 2) 备注里的中文别称：只改生命周期列上的整串精确匹配
UPDATE dbo.FIELDS SET F_REMARK = N'批核状态' WHERE F_ID = N'APPROVED'     AND F_REMARK = N'确认状态';
UPDATE dbo.FIELDS SET F_REMARK = N'批核人'   WHERE F_ID = N'APPROVED_BY'  AND F_REMARK = N'确认人';
UPDATE dbo.FIELDS SET F_REMARK = N'批核时间' WHERE F_ID = N'APPROVED_TIME' AND F_REMARK = N'确认日期';
UPDATE dbo.FIELDS SET F_REMARK = N'结案状态' WHERE F_ID = N'FINISHED'      AND F_REMARK = N'确认状态';
UPDATE dbo.FIELDS SET F_REMARK = N'结案人'   WHERE F_ID = N'FINISHED_BY'   AND F_REMARK = N'确认人';
UPDATE dbo.FIELDS SET F_REMARK = N'结案时间' WHERE F_ID = N'FINISHED_TIME' AND F_REMARK = N'确认日期';
