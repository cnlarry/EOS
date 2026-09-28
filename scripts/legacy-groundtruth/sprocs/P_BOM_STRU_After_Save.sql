

-- BOM
CREATE PROCEDURE P_BOM_STRU_After_Save
	@pri_idx nvarchar(1000), @module int
AS
	declare @sql nvarchar(4000),@msg varchar(500)
	declare @err varchar(4000)
	declare @i int, @serial_no int
	declare @pro_no nchar(30)
	declare @errCode nvarchar(50)
	declare @ok int

	select @sql = 'select @pro_no=pro_no from BOM_STRU_M  where ' + @pri_idx
	exec sp_executesql @sql, N'@pro_no nchar(30) output', @pro_no output

	--检查品号是否存在
	if not exists(select * from PRODUCT where PRO_NO=@pro_no) begin
		select @msg = '产品编号不存在。 '
		RAISERROR(@msg,16, 1)
		goto finally
	end

	-- 检验元件编号是否存在
	declare cursor_count cursor for
		select SERIAL_NO from BOM_STRU_D d where PRO_NO=@pro_no and not exists(select * from PRODUCT p where p.PRO_NO=d.ELEMENT_PRO_NO) 
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
		select @msg = '以下序号项元件编号不存在 ' + char(13) +'%s'
		RAISERROR(@msg,16, 1, @err)
		goto finally
	end

	-- 检验底数是否小于0
	declare cursor_count cursor for
		select SERIAL_NO from BOM_STRU_D  where BASE_QTY<=0 
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
		select @msg = '以下序号项元件底数不能小于0 ' + char(13) +'%s'
		RAISERROR(@msg,16, 1, @err)
		goto finally
	end

	--检查BOM是否循环引用
	exec P_BOM_CHECK @pro_no,  @ok output, @errCode output
	if @ok!=1 begin
		select @msg = '以下元件在BOM结构中循环使用 ' + char(13) +'%s'
		RAISERROR(@msg,16, 1, @errCode)
		goto finally
	end
	
	update bom_stru_m set P_LENGTH_OLD = d.P_LENGTH, P_WIDTH_OLD = d.P_WIDTH FROM product d WHERE bom_stru_m.PRO_NO=d.PRO_NO and bom_stru_m.PRO_NO=@pro_no
	finally:

