

--入库单
CREATE PROCEDURE P_MOC_PRODUCT_IN_After_Save
	@pri_idx nvarchar(1000), @module int
AS
	declare @sql nvarchar(4000),@msg varchar(500)
	declare @err varchar(4000)
	declare @i int, @serial_no int, @success int
	declare @product_in_type nchar(10), @product_in_no nchar(20)

	select @sql = 'select @product_in_type=product_in_type, @product_in_no=product_in_no from MOC_PRODUCT_IN_M  where  ' + @pri_idx
	exec sp_executesql @sql, N'@product_in_type nchar(10) output, @product_in_no nchar(20) output', @product_in_type output, @product_in_no output

	select * into #temp_m from MOC_PRODUCT_IN_M where PRODUCT_IN_TYPE=@product_in_type and PRODUCT_IN_NO=@product_in_no
	select * into #temp_d from MOC_PRODUCT_IN_D where PRODUCT_IN_TYPE=@product_in_type and PRODUCT_IN_NO=@product_in_no

	if exists(select * from MODULES where M_IDX=@module and ERROR_NO_SAVE=1) begin
	exec P_MOC_PRODUCT_IN_CHECK @product_in_type, @product_in_no, @success output, @msg output
		if @@ERROR<>0 or @success = 0 begin
			RAISERROR(@msg,16, 1)
			goto finally
		end
	end

	 --检查制令号是否存在
	declare cursor_count cursor for
		select SERIAL_NO from #temp_d t where not exists(select * from MOC_PRODUCE_M c where c.PRODUCE_TYPE=t.PRODUCE_TYPE and c.PRODUCE_NO=t.PRODUCE_NO)
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
		select @msg = '以下序号项制令单不存在 ' + char(13) +'%s'
		RAISERROR(@msg,16, 1, @err)
		goto finally
	end

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

	--结束
	finally:
		drop table #temp_m
		drop table #temp_d

