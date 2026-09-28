

--仓库调拔单
CREATE PROCEDURE P_INV_OCCUR_SCRAP_After_Save
    @pri_idx nvarchar(1000), @module int
AS
    declare @sql nvarchar(4000),@msg varchar(500)
    declare @err varchar(4000)
    declare @i int, @serial_no int
    declare @occur_type nchar(10), @occur_no nchar(20)

    select @sql = 'select @occur_type=occur_type, @occur_no=occur_no from INV_OCCUR_SCRAP_M  where ' + @pri_idx
    exec sp_executesql @sql, N'@occur_type nchar(10) output, @occur_no nchar(20) output', @occur_type output, @occur_no output

    select * into #temp_m from INV_OCCUR_SCRAP_M where OCCUR_TYPE=@occur_type and OCCUR_NO=@occur_no
    select * into #temp_d from INV_OCCUR_SCRAP_D where OCCUR_TYPE=@occur_type and OCCUR_NO=@occur_no

     --检查入库别是否存在
    declare cursor_count cursor for
        select SERIAL_NO from #temp_d t where not exists(select * from DEPOT c where c.DEPOT_ID=t.IN_DEPOT_ID)
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
        select @msg = '以下序号项入库别编号不存在 ' + char(13) +'%s'
        RAISERROR(@msg,16, 1, @err)
        goto finally
    end

    --检查出库别是否存在
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
        select @msg = '以下序号项出库别编号不存在 ' + char(13) +'%s'
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

