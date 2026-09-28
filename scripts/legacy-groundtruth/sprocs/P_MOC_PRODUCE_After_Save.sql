

--制令单

CREATE PROCEDURE P_MOC_PRODUCE_After_Save
	@pri_idx nvarchar(1000), @module int
AS
	declare @sql nvarchar(4000),@msg varchar(500)
	declare @err varchar(4000)
	declare @i int, @serial_no int, @success int
	declare @produce_type nchar(10), @produce_no nchar(20)

	select @sql = 'select @produce_type=produce_type, @produce_no=produce_no from MOC_PRODUCE_M  where ' + @pri_idx
	exec sp_executesql @sql, N'@produce_type nchar(10) output, @produce_no nchar(20) output', @produce_type output, @produce_no output

	select * into #temp_m from MOC_PRODUCE_M where PRODUCE_TYPE=@produce_type and PRODUCE_NO=@produce_no
	select * into #temp_d from MOC_PRODUCE_D where PRODUCE_TYPE=@produce_type and PRODUCE_NO=@produce_no

	if exists(select * from MODULES where M_IDX=@module and ERROR_NO_SAVE=1) begin
	exec P_MOC_PRODUCE_CHECK @produce_type, @produce_no, @success output, @msg output
		if @@ERROR<>0 or @success = 0 begin
			RAISERROR(@msg,16, 1)
			goto finally
		end
	end

	 --检查订单号是否存在
	if exists(select * from  MOC_PRODUCE_M where PRODUCE_TYPE=@produce_type and PRODUCE_NO=@produce_no and ORDER_NO<>'') begin
		if not exists(select * from COP_ORDER_D d, #temp_m t where d.ORDER_TYPE=t.ORDER_TYPE and d.ORDER_NO=t.ORDER_NO and d.SERIAL_NO=t.ORDER_SERIAL_NO) begin
			select @msg = '订单不存在  ' + char(13)
			RAISERROR(@msg,16, 1)
			goto finally
		end
	end

	--检验料件编号是否存在
	declare cursor_count cursor for
		select SERIAL_NO from #temp_d d where not exists(select * from PRODUCT p where p.PRO_NO=d.PRO_NO) 
	open cursor_count
	fetch next from cursor_count 
		into @serial_no
	select @i=0,@err=''
	while @@FETCH_STATUS = 0
	begin
		select @i=@i+1
		if @i>10  begin select @err = @err + '...........' break end
		select @err = @err + str(@serial_no)
		fetch next from cursor_count 
			into @serial_no 
	end
	close cursor_count
	deallocate cursor_count
	if isnull(@err,'')<>'' begin
		select @msg = '以下序号项产品编号不存在 ' + char(13) +'%s'
		RAISERROR(@msg,16, 1, @err)
		goto finally
	end
/*	--检查下生产单数量 不能大于 订单未下生产单数量
	if exists(select * from COP_ORDER_D od, MOC_PRODUCE_M pr where od.ORDER_TYPE=pr.ORDER_TYPE and od.ORDER_NO=pr.ORDER_NO and od.SERIAL_NO=pr.ORDER_SERIAL_NO and pr.PRODUCE_TYPE=@produce_type and pr.PRODUCE_NO=@produce_no and (od.PLAN_QTY<od.FINISHED_PLAN_QTY+pr.QTY or od.PLAN_SPARE_QTY<od.FINISHED_PLAN_SPARE_QTY+pr.SPARE_QTY)) begin
		RAISERROR ('生产数量或备品生产数量超出订单数量', 16, 1)
		goto finally
	end
*/
	--更新明细表订单号
	update MOC_PRODUCE_D set ORDER_TYPE=m.ORDER_TYPE, ORDER_NO=m.ORDER_NO, ORDER_SERIAL_NO=m.ORDER_SERIAL_NO 
		from MOC_PRODUCE_M m 
		where MOC_PRODUCE_D.PRODUCE_TYPE=m.PRODUCE_TYPE and MOC_PRODUCE_D.PRODUCE_NO=m.PRODUCE_NO and m.PRODUCE_TYPE=@produce_type and m.PRODUCE_NO=@produce_no

	--结束
	finally:
		drop table #temp_m
		drop table #temp_d

