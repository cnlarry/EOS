

CREATE PROCEDURE P_Employee_Card_After_Save
	@pri_idx nvarchar(1000), @module int
AS
	declare @sql nvarchar(4000)
		declare @msg varchar(200)
	declare @card_id nvarchar(20), @emp_id nvarchar(10), @begin_date varchar(20), @end_date varchar(20), @err varchar(1000)
	create table #temp(emp_id nchar(10), card_id nchar(20), begin_date datetime, end_date datetime)
	insert into #temp(emp_id, card_id, begin_date, end_date) exec ('select emp_id, card_id, begin_date, end_date from hr_employee_card where '+@pri_idx)

	--判断失效日期是否在生效日期前
	if exists(select * from #temp where end_date<begin_date) begin
		RAISERROR('失于日期应在生效日期后',16, 1)
		goto finally
	end

	select @card_id=CARD_ID, @emp_id=EMP_ID, @begin_date=BEGIN_DATE from #temp 
	--更新旧用户卡到期日
	update HR_EMPLOYEE_CARD set END_DATE=dateadd(day, -1, @begin_date) where CARD_ID=@card_id and EMP_ID<>@emp_id and (END_DATE is null or END_DATE>=@begin_date)

	--判断卡号是否已占用且未到期
	--select @card_id=ltrim(rtrim(m.card_id)), @emp_id=ltrim(rtrim(m.emp_id)),@begin_date=convert(varchar,m.begin_date,112),@end_date=convert(varchar,m.end_date,112) from hr_employee_card m, #temp t
	--	where m.card_id=t.card_id and m.emp_id<>t.emp_id and (t.begin_date between m.begin_date and m.end_date or t.end_date between m.begin_date and m.end_date or m.end_date is null or (t.begin_date <=m.begin_date and t.end_date is null))
	--if isnull(@emp_id,'')<>'' begin
		--select @msg =char(13)+ '卡号:%s 已分配给:%s '+char(13)+' 生效日:%s 到期日:%s'
		--RAISERROR(@msg,16, 1, @card_id, @emp_id, @begin_date,@end_date)
		--goto finally
	--end

	--判断本人用卡时间是否重复
	--select @card_id=ltrim(rtrim(m.card_id)), @emp_id=ltrim(rtrim(m.emp_id)),@begin_date=convert(varchar,m.begin_date,112),@end_date=convert(varchar,m.end_date,112) from hr_employee_card m, #temp t
	--	where m.emp_id=t.emp_id and m.card_id<>t.card_id and (t.begin_date between m.begin_date and m.end_date or t.end_date between m.begin_date and m.end_date)
	--if isnull(@emp_id,'')<>''  begin
	--	select @msg =char(13)+ '当前卡与工号:%s 卡号:%s  生效日:%s 到期日:%s日期重叠'
	--	RAISERROR(@msg,16, 1, @emp_id, @card_id,  @begin_date,@end_date)
	--	goto finally
	--end

	--更新旧卡失效时期
	update hr_employee_card set end_date=dateadd(day,-1,@begin_date)  where emp_id=@emp_id and card_id<>@card_id and (end_date is null or end_date>=@begin_date)

finally:
	drop table #temp

