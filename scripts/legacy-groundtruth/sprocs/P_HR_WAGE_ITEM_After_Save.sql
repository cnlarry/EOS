

-- 工资项目
CREATE PROCEDURE P_HR_WAGE_ITEM_After_Save
	@pri_idx nvarchar(1000), @module int
AS

	update FIELDS set IS_VISIBLE=0 where T_ID='HR_WAGE_D'  and F_ID like 'WAGE_ITEM%'

	update FIELDS set IS_VISIBLE=w.IS_USED, F_DESC=w.WAGE_NAME, DISPLAY_FORMAT=w.DISPLAY_FORMAT, F_REMARK=w.SQL_REMARK 
		from HR_WAGE w 
		where FIELDS.T_ID='HR_WAGE_D' AND FIELDS.F_ID=w.WAGE_FIELD

	finally:

