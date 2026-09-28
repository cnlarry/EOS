

--领料单
CREATE PROCEDURE P_MOC_GET_After_Save
	@pri_idx nvarchar(1000), @module int
AS
	declare @sql nvarchar(4000),@msg varchar(500)
	declare @err varchar(4000), @pro_no nchar(30), @pro_more nchar(30), @qty decimal(18, 8), @qty_more decimal(18, 8)
	declare @i int, @serial_no int, @produce_no nvarchar(20), @order_no nvarchar(20), @produce_count nvarchar(300), @order_count nvarchar(300)
	declare @get_type nchar(10), @get_no nchar(20)

	select @sql = 'select @get_type=get_type, @get_no=get_no from MOC_GET_M  where ' + @pri_idx
	exec sp_executesql @sql, N'@get_type nchar(10) output, @get_no nchar(20) output', @get_type output, @get_no output

	select * into #temp_m from MOC_GET_M where GET_TYPE=@get_type and GET_NO=@get_no
	select * into #temp_d from MOC_GET_D where GET_TYPE=@get_type and GET_NO=@get_no

	 --检查制令号是否存在
--	declare cursor_count cursor for
--		select SERIAL_NO from #temp_d t where not exists(select * from MOC_PRODUCE_D c where c.PRODUCE_TYPE=t.PRODUCE_TYPE and c.PRODUCE_NO=t.PRODUCE_NO and c.SERIAL_NO=t.PRODUCE_SERIAL_NO)
--	open cursor_count
--	fetch next from cursor_count 
--		into @serial_no
--	select @i=0,@err=''
--	while @@FETCH_STATUS = 0
--	begin
--		select @i=@i+1
--		if @i>10  begin select @err = @err + '............' break end
--		select @err = @err + str(@serial_no)
--		fetch next from cursor_count 
--			into @serial_no 
--	end
--	close cursor_count
--	deallocate cursor_count
--	if isnull(@err,'')<>'' begin
--		select @msg = '以下序号项制令单不存在 ' + char(13) +'%s'
--		RAISERROR(@msg,16, 1, @err)
--		goto finally
--	end

	 --检查 发料>领料
--	declare cursor_count cursor for
--		select SERIAL_NO from #temp_d where SEND_QTY<QTY
--	open cursor_count
--	fetch next from cursor_count 
--		into @serial_no
--	select @i=0,@err=''
--	while @@FETCH_STATUS = 0
--	begin
--		select @i=@i+1
--		if @i>10  begin select @err = @err + '...........' break end
--		select @err = @err + str(@serial_no)
--		fetch next from cursor_count 
--			into @serial_no 
--	end
--	close cursor_count
--	deallocate cursor_count
--	if isnull(@err,'')<>'' begin
--		select @msg = '以下序号项发料数量不够 ' + char(13) +'%s'
--		RAISERROR(@msg,16, 1, @err)
--		goto finally
--	end

	 --检查库别是否存在
	declare cursor_count cursor for
		select SERIAL_NO from #temp_d t where not exists(select * from DEPOT c where c.DEPOT_ID=t.DEPOT_ID)
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
		select @msg = '以下序号项库别编号不存在 ' + char(13) +'%s'
		RAISERROR(@msg,16, 1, @err)
		goto finally
	end

	--检验产品编号是否存在
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
		select @msg = '以下序号项需要输入批号 ' + char(13) +'%s'
		RAISERROR(@msg,16, 1, @err)
		goto finally
	end
/*--改了合并领料来源，以下不要了
	--更新汇总应领数量
	select GET_TYPE, GET_NO, 0 as SERIAL_NO, PRO_NO, sum(QTY) QTY into #tmp_more from MOC_GET_MORE where GET_TYPE=@get_type and GET_NO=@get_no and PRO_NO not in(select PRO_NO from MOC_GET_D where GET_TYPE=@get_type and GET_NO=@get_no) group by  GET_TYPE, GET_NO, PRO_NO

	select @i=max(SERIAL_NO) from MOC_GET_D where GET_TYPE=@get_type and GET_NO=@get_no
	select @i=isnull(@i, 0)

	update #tmp_more set @i=@i+1, SERIAL_NO=@i

--	insert into MOC_GET_D(GET_TYPE, GET_NO, SERIAL_NO, PRO_NO, DEPOT_ID, SEND_QTY, UNIT_ID, PRICE, AMOUNT) 
--		select t.GET_TYPE, t.GET_NO, t.SERIAL_NO, t.PRO_NO, p.DEPOT_ID, t.QTY, p.UNIT_ID, p.PRICE, round(p.PRICE * t.QTY , 2)
--		from #tmp_more t left join PRODUCT p on t.PRO_NO=p.PRO_NO
	insert into MOC_GET_D(GET_TYPE, GET_NO, SERIAL_NO, PRO_NO, DEPOT_ID, SEND_QTY, UNIT_ID, PRICE, AMOUNT) 
		select t.GET_TYPE, t.GET_NO, t.SERIAL_NO, t.PRO_NO, p.DEPOT_ID,0, p.UNIT_ID, p.PRICE, round(p.PRICE * t.QTY , 2)
		from #tmp_more t left join PRODUCT p on t.PRO_NO=p.PRO_NO

	update MOC_GET_D set QTY=0 where GET_TYPE=@get_type and GET_NO=@get_no

	update MOC_GET_D set QTY=s.QTY
		from (select GET_TYPE, GET_NO, PRO_NO, sum(REQUIRE_QTY) QTY from MOC_GET_MORE where  GET_TYPE=@get_type and GET_NO=@get_no group by GET_TYPE, GET_NO, PRO_NO)s 
		where MOC_GET_D.GET_TYPE=s.GET_TYPE and MOC_GET_D.GET_NO=s.GET_NO and MOC_GET_D.PRO_NO=s.PRO_NO and MOC_GET_D.GET_TYPE=@get_type and MOC_GET_D.GET_NO=@get_no
	update MOC_GET_D set RETURN_QTY=case  when SEND_QTY>QTY then SEND_QTY-QTY else 0 end where GET_TYPE=@get_type and GET_NO=@get_no

	if isnull(@err,'')<>'' begin
		select @msg = '更新汇总应领数量失败'
		RAISERROR(@msg, 16, 1)
		goto finally
	end

	--分配发料数量
	select @pro_no=''
	update MOC_GET_MORE set QTY = 0 where GET_TYPE=@get_type and GET_NO=@get_no
	declare cur_tmp cursor for
		select SERIAL_NO, PRO_NO, REQUIRE_QTY from MOC_GET_MORE where GET_TYPE=@get_type and GET_NO=@get_no order by PRO_NO, SERIAL_NO
	open cur_tmp
	fetch next from cur_tmp into @serial_no, @pro_more, @qty_more
	while @@FETCH_STATUS = 0  begin
		if @pro_no<>@pro_more
			select @pro_no=@pro_more, @qty = SEND_QTY from MOC_GET_D where GET_TYPE=@get_type and GET_NO=@get_no and PRO_NO=@pro_more
		if @qty > @qty_more
			update MOC_GET_MORE set QTY = @qty_more, @qty = @qty - @qty_more where GET_TYPE=@get_type and GET_NO=@get_no and SERIAL_NO=@serial_no
		else if @qty > 0 begin
			update MOC_GET_MORE set QTY = @qty where GET_TYPE=@get_type and GET_NO=@get_no and SERIAL_NO=@serial_no
			select @qty = @qty - @qty_more
		end
		fetch next from cur_tmp into @serial_no, @pro_more, @qty_more
	end
	close cur_tmp
	deallocate cur_tmp
	if @@ERROR<>0 begin
		select @msg = '分配发料数量失败'
		RAISERROR(@msg, 16, 1)
		goto finally
	end

	--更新领料订单号、生产单号
	select @order_count = ''
	declare cur_tmp cursor for
		select distinct ORDER_NO from MOC_GET_MORE where GET_TYPE=@get_type and GET_NO=@get_no order by ORDER_NO
	open cur_tmp
	fetch next from cur_tmp into @order_no
	while @@FETCH_STATUS = 0  begin
		select @order_count = @order_count + rtrim(@order_no)
		fetch next from cur_tmp into @order_no
		if @@FETCH_STATUS=0
			select @order_count = @order_count + ','
	end
	close cur_tmp
	deallocate cur_tmp
	if @order_count<>''
		update MOC_GET_M set ORDER_NO=@order_count where GET_TYPE=@get_type and GET_NO=@get_no
	if @@ERROR<>0 begin
		select @msg = '更新领料订单号失败'
		RAISERROR(@msg, 16, 1)
		goto finally
	end

	--更新领料订单号、生产单号
	select @produce_count = ''
	declare cur_tmp cursor for
		select distinct PRODUCE_NO from MOC_GET_MORE where GET_TYPE=@get_type and GET_NO=@get_no order by PRODUCE_NO
	open cur_tmp
	fetch next from cur_tmp into @produce_no
	while @@FETCH_STATUS = 0  begin
		select @produce_count = @produce_count + rtrim(@produce_no)
		fetch next from cur_tmp into @produce_no
		if @@FETCH_STATUS=0
			select @produce_count = @produce_count + ','
	end
	close cur_tmp
	deallocate cur_tmp
	if @produce_count<>''
		update MOC_GET_M set PRODUCE_NO=@produce_count where GET_TYPE=@get_type and GET_NO=@get_no
	if @@ERROR<>0 begin
		select @msg = '更新领料生产单号失败'
		RAISERROR(@msg, 16, 1)
		goto finally
	end
*/
--结束
finally:
	drop table #temp_m
	drop table #temp_d

