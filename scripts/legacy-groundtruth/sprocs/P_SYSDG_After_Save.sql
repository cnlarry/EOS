

-- 用户组权限设置
CREATE PROCEDURE P_SYSDG_After_Save
	@pri_idx nvarchar(1000), @module int
AS
	declare @sql nvarchar(4000)
	--declare @g_idx nchar(10), @m_idx int
	--select @sql = 'select @g_idx=G_IDX, @m_idx=M_IDX from SYSDH  where ' + @pri_idx
	--exec sp_executesql @sql, N'@g_idx nchar(10) output, @m_idx int output', @g_idx output, @m_idx output
	--插入新报表权限（若有报表权限，新报表权全部加上）
	--insert into SYSDH_REPORT(G_IDX, M_IDX, REPORT_ID, PREVIEW_TAG, PRINT_TAG, EXPORT_TAG, DATA_FILTER) 
	--	select m.G_IDX,r.R_M_IDX,r.REPORT_ID,1,1,1,'' 
	--	from  SYSDH m, REPORT r where r.REPORT_ID not in (select REPORT_ID from SYSDH_REPORT where G_IDX=@g_idx)
	--		and r.R_M_IDX in (select M_IDX from SYSDH  where REPORT_TAG=1 and G_IDX=@g_idx)
	--		and m.G_IDX=@g_idx

	delete from SYSDH where M_IDX  not in(select M_IDX from MODULES)

	delete from SYSDH_REPORT where M_IDX  not in(select M_IDX from MODULES) or REPORT_ID not in(select REPORT_ID from REPORT)

