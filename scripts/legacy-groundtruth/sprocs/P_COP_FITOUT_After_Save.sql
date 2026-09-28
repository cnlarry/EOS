

-- 备货单
CREATE PROCEDURE P_COP_FITOUT_After_Save
	@pri_idx nvarchar(1000), @module int
AS
	declare @sql nvarchar(4000),@msg varchar(2000)
	declare @err varchar(4000)
	declare @i int, @serial_no int, @success int
	declare @fitout_type nchar(10), @fitout_no nchar(20)

	select @sql = 'select @fitout_type=fitout_type, @fitout_no=fitout_no from COP_FITOUT_M  where ' + @pri_idx
	exec sp_executesql @sql, N'@fitout_type nchar(10) output, @fitout_no nchar(20) output', @fitout_type output, @fitout_no output

	select * into #temp_m from COP_FITOUT_M where FITOUT_TYPE=@fitout_type and FITOUT_NO=@fitout_no
	select * into #temp_d from COP_FITOUT_D where FITOUT_TYPE=@fitout_type and FITOUT_NO=@fitout_no

	if exists(select * from MODULES where M_IDX=@module and ERROR_NO_SAVE=1) begin
		exec P_COP_FITOUT_CHECK @fitout_type, @fitout_no, @success output, @msg output
		if @@ERROR<>0 or @success = 0 begin
			RAISERROR(@msg,16, 1)
			goto finally
		end
		if exists(select * from SYSSS where FITOUT_TAG=1) begin
			--检查是否够库存出库、有批号的是否对应批号够出库
			select @sql =  'select m.FITOUT_TYPE, m.FITOUT_NO, m.FITOUT_DATE, d.SERIAL_NO, d.PRO_NO, d.DEPOT_ID, d.QTY + d.SPARE_QTY, d.UNIT_ID, d.PRICE, d.CURR_ID, d.CURR_RATE, d.AMOUNT, d.BATCH_NO from (select * from COP_FITOUT_M where FITOUT_TYPE='''+@fitout_type +''' and FITOUT_NO='''+@fitout_no+ ''' ) m, COP_FITOUT_D d  where m.FITOUT_TYPE=d.FITOUT_TYPE and m.FITOUT_NO=d.FITOUT_NO'
			exec @success = P_UPDATE_PRO_DEPOT_CHECK @sql, @success output, @msg output
			if @@ERROR<>0 or @success = 0 begin
				RAISERROR(@msg,16, 1)
				goto finally
			end
		end
	end


	--有订单编号的，检查订单与客户是否相符
	declare cursor_count cursor for
		select d.SERIAL_NO from COP_ORDER_M o, #temp_m m,  #temp_d d where o.ORDER_TYPE=d.ORDER_TYPE and o.ORDER_NO=d.ORDER_NO  and m.FITOUT_TYPE=d.FITOUT_TYPE and m.FITOUT_NO=d.FITOUT_NO
			and o.CLIENT_ID<>m.CLIENT_ID
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
		select @msg = '以下序号项送货单与订单客户不符 ' + char(13) +'%s'
		RAISERROR(@msg,16, 1, @err)
		goto finally
	end

	--检验订单是否存在
	declare cursor_count cursor for
		select SERIAL_NO from #temp_d d where ISNULL(d.ORDER_TYPE,'')<>'' and not exists(select * from COP_ORDER_M o where o.ORDER_TYPE=d.ORDER_TYPE and o.ORDER_NO=d.ORDER_NO) 
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
		select @msg = '以下序号项订单不存在 ' + char(13) +'%s'
		RAISERROR(@msg,16, 1, @err)
		goto finally
	end

	--检验订单序号与产品编号是否相符
	declare cursor_count cursor for
		select SERIAL_NO from #temp_d d where ISNULL(d.ORDER_TYPE,'')<>'' and not exists(select * from COP_ORDER_D o where o.ORDER_TYPE=d.ORDER_TYPE and o.ORDER_NO=d.ORDER_NO and o.SERIAL_NO=d.ORDER_SERIAL_NO and o.PRO_NO=d.PRO_NO) 
	open cursor_count

	fetch next from cursor_count into @serial_no
	select @i=0,@err=''
	while @@FETCH_STATUS = 0
	begin
		select @i=@i+1
		if @i>10  begin select @err = @err + '...........' break end
		select @err = @err + str(@serial_no)
		fetch next from cursor_count into @serial_no 
	end

	close cursor_count
	deallocate cursor_count
	if isnull(@err,'')<>'' begin
		select @msg = '以下序号项订单序号与产品编号不相符 ' + char(13) +'%s'
		RAISERROR(@msg,16, 1, @err)
		goto finally
	end

	--检验产品编号是否存在
	declare cursor_count cursor for
		select SERIAL_NO from #temp_d d where not exists(select * from PRODUCT p where p.PRO_NO=d.PRO_NO) 
	open cursor_count

	fetch next from cursor_count into @serial_no
	select @i=0,@err=''
	while @@FETCH_STATUS = 0
	begin
		select @i=@i+1
		if @i>10  begin select @err = @err + '...........' break end
		select @err = @err + str(@serial_no)
		fetch next from cursor_count into @serial_no 
	end

	close cursor_count
	deallocate cursor_count
	if isnull(@err,'')<>'' begin
		select @msg = '以下序号项产品编号不存在 ' + char(13) +'%s'
		RAISERROR(@msg,16, 1, @err)
		goto finally
	end

	--检验产品是需要批号
	declare cursor_count cursor for
		select SERIAL_NO from #temp_d d where isnull(BATCH_NO,'')='' and exists(select * from PRODUCT p where p.PRO_NO=d.PRO_NO and p.MANAGE_BATCH=1) 
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
		select @msg = '以下序号项需要输入批号! ' + char(13) +'%s'
		RAISERROR(@msg,16, 1, @err)
		goto finally
	end

	finally:
		drop table #temp_m
		drop table #temp_d

