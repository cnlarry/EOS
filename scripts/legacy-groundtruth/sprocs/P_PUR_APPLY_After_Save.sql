

-- 申购单
CREATE PROCEDURE P_PUR_APPLY_After_Save
	@pri_idx nvarchar(1000), @module int
AS
	declare @sql nvarchar(4000),@msg varchar(500)
	declare @err varchar(4000), @pro_no nchar(30), @pro_more nchar(30), @qty decimal(18, 8), @qty_more decimal(18, 8)
	declare @i int, @serial_no int
	declare @apply_type nchar(10), @apply_no nchar(20)
	declare @produce_no nvarchar(20), @order_no nvarchar(20), @produce_count nvarchar(300), @order_count nvarchar(300)

	select @sql = 'select @apply_type=apply_type, @apply_no=apply_no from PUR_APPLY_M  where ' + @pri_idx
	exec sp_executesql @sql, N'@apply_type nchar(10) output, @apply_no nchar(20) output', @apply_type output, @apply_no output

	select * into #temp_m from PUR_APPLY_M where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no
	select * into #temp_d from PUR_APPLY_D where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no


 /*   --检查厂商是否存在
	declare cursor_count cursor for
		select SERIAL_NO from #temp_d t where not exists(select * from SUPPLIER s where s.SUPPLIER_ID=t.SUPPLIER_ID and s.BUSINESS_TAG=1) 
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
		select @msg = '以下序号项厂商编号不存在或者已停止交易 ' + char(13) +'%s'
		RAISERROR(@msg,16, 1, @err)
		goto finally
	end
*/
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

	--更新汇总应购、请购数量
	select APPLY_TYPE, APPLY_NO, 0 as SERIAL_NO, PRO_NO, sum(REQUIRE_QTY) REQUIRE_QTY into #tmp_more from PUR_APPLY_MORE where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no and PRO_NO not in(select PRO_NO from PUR_APPLY_D where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no) group by  APPLY_TYPE, APPLY_NO, PRO_NO

	select @i=max(SERIAL_NO) from PUR_APPLY_D where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no
	select @i=isnull(@i, 0)

	update #tmp_more set @i=@i+1, SERIAL_NO=@i

	insert into PUR_APPLY_D(APPLY_TYPE, APPLY_NO, SERIAL_NO, PRO_NO, DEPOT_ID, QTY,  UNIT_ID) 
		select t.APPLY_TYPE, t.APPLY_NO, t.SERIAL_NO, t.PRO_NO, p.DEPOT_ID, t.REQUIRE_QTY, p.UNIT_ID
		from #tmp_more t left join PRODUCT p on t.PRO_NO=p.PRO_NO

	update PUR_APPLY_D set REQUIRE_QTY=0 where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no

	update PUR_APPLY_D set REQUIRE_QTY=s.REQUIRE_QTY,LOST_QTY=s.LOST_QTY 
		from (select APPLY_TYPE, APPLY_NO, PRO_NO, sum(REQUIRE_QTY) REQUIRE_QTY,sum(LOST_QTY) LOST_QTY from PUR_APPLY_MORE where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no group by APPLY_TYPE, APPLY_NO, PRO_NO)s 
		where PUR_APPLY_D.APPLY_TYPE=s.APPLY_TYPE and PUR_APPLY_D.APPLY_NO=s.APPLY_NO and PUR_APPLY_D.PRO_NO=s.PRO_NO and PUR_APPLY_D.APPLY_TYPE=@apply_type and PUR_APPLY_D.APPLY_NO=@apply_no
		
	--从PUR_APPLY_MORE中更新客户单号、客户品号、预交日期、客户代号、订单号	
	update PUR_APPLY_D set ORDER_TYPE=d.ORDER_TYPE,ORDER_NO=d.ORDER_NO,ORDER_SERIAL_NO=d.SERIAL_NO,CLIENT_PRO_NO=d.CLIENT_PRO_NO,CLIENT_ORDER_NO=d.CLIENT_ORDER_NO,USED_DATE=d.PRE_SEND_DATE,CLIENT_ID=c.CLIENT_ID from COP_ORDER_M c,COP_ORDER_D d,PUR_APPLY_MORE m where m.APPLY_TYPE=@apply_type and m.APPLY_NO=@apply_no and m.ORDER_TYPE=d.ORDER_TYPE and m.ORDER_NO=d.ORDER_NO and m.ORDER_SERIAL_NO=d.SERIAL_NO and c.ORDER_TYPE=d.ORDER_TYPE and c.ORDER_NO=d.ORDER_NO and m.APPLY_TYPE=PUR_APPLY_D.APPLY_TYPE and m.APPLY_NO=PUR_APPLY_D.APPLY_NO and m.PRO_NO=PUR_APPLY_D.PRO_NO
	if isnull(@err,'')<>'' begin
		select @msg = '更新汇总应购数量失败'
		RAISERROR(@msg, 16, 1)
		goto finally
	end

	--分配申购数量
	update PUR_APPLY_MORE set QTY = 0 where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no
	select @pro_no = ''
	declare cur_tmp cursor for
		select SERIAL_NO, PRO_NO, REQUIRE_QTY from PUR_APPLY_MORE where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no order by PRO_NO, SERIAL_NO
	open cur_tmp
	fetch next from cur_tmp into @serial_no, @pro_more, @qty_more
	while @@FETCH_STATUS = 0  begin
		if @pro_no<>@pro_more
			select @pro_no=@pro_more, @qty = QTY from PUR_APPLY_D where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no and PRO_NO=@pro_more
		if @qty > @qty_more
			update PUR_APPLY_MORE set QTY = @qty_more, @qty = @qty - @qty_more where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no and SERIAL_NO=@serial_no
		else if @qty > 0 begin
			update PUR_APPLY_MORE set QTY = @qty where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no and SERIAL_NO=@serial_no
			select @qty = @qty - @qty_more
		end
		fetch next from cur_tmp into @serial_no, @pro_more, @qty_more
	end
	close cur_tmp
	deallocate cur_tmp
	if @@ERROR<>0 begin
		select @msg = '分配申购数量失败'
		RAISERROR(@msg, 16, 1)
		goto finally
	end

--更新采购订单号、生产单号
	select @order_count = ''
	declare cur_tmp cursor for
		select distinct ORDER_NO from PUR_APPLY_MORE where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no order by ORDER_NO
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
		update PUR_APPLY_M set ORDER_NO=@order_count where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no 
	if @@ERROR<>0 begin
		select @msg = '更新采购订单号失败'
		RAISERROR(@msg, 16, 1)
		goto finally
	end

	--更新采购订单号、生产单号
	select @produce_count = ''
	declare cur_tmp cursor for
		select distinct PRODUCE_NO from PUR_APPLY_MORE where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no order by PRODUCE_NO
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
		update PUR_APPLY_M set PRODUCE_NO=@produce_count where  APPLY_TYPE=@apply_type and APPLY_NO=@apply_no 
	if @@ERROR<>0 begin
		select @msg = '更新采购生产单号失败'
		RAISERROR(@msg, 16, 1)
		goto finally
	end
 	
	--delete from PUR_APPLY_D where  APPLY_TYPE=@apply_type and APPLY_NO=@apply_no and APPLY_NO in (select APPLY_NO from PUR_APPLY_M  where PRODUCE_NO=''  and APPLY_TYPE=@apply_type and APPLY_NO=@apply_no)
	--delete from PUR_APPLY_M where PRODUCE_NO=''  and APPLY_TYPE=@apply_type and APPLY_NO=@apply_no 

finally:
	drop table #temp_m
	drop table #temp_d

