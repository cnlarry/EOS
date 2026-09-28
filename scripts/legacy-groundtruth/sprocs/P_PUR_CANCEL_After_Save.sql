
-- 退料单
CREATE PROCEDURE P_PUR_CANCEL_After_Save
    @pri_idx nvarchar(1000), @module int
AS
    declare @sql nvarchar(4000),@msg varchar(500)
    declare @err varchar(4000)
    declare @i int, @serial_no int, @p_qty float, @r_qty float, @s_qty float, @p_spare_qty float, @r_spare_qty float, @s_spare_qty float
    declare @cancel_type nchar(10), @cancel_no nchar(20)

    select @sql = 'select @cancel_type=cancel_type, @cancel_no=cancel_no from PUR_CANCEL_M  where ' + @pri_idx
    exec sp_executesql @sql, N'@cancel_type nchar(10) output, @cancel_no nchar(20) output', @cancel_type output, @cancel_no output

    select * into #temp_m from PUR_CANCEL_M where CANCEL_TYPE=@cancel_type and CANCEL_NO=@cancel_no
    select * into #temp_d from PUR_CANCEL_D where CANCEL_TYPE=@cancel_type and CANCEL_NO=@cancel_no

    --有采购单编号的，检查采购单与厂商是否相符
    declare cursor_count cursor for
        select d.SERIAL_NO from PUR_PURCHASE_M o, #temp_m m,  #temp_d d where o.PURCHASE_TYPE=d.PURCHASE_TYPE and o.PURCHASE_NO=d.PURCHASE_NO  and m.CANCEL_TYPE=d.CANCEL_TYPE and m.CANCEL_NO=d.CANCEL_NO
            and o.SUPPLIER_ID<>m.SUPPLIER_ID
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
        select @msg = '以下序号项收料单与采购单厂商不符 ' + char(13) +'%s'
        RAISERROR(@msg,16, 1, @err)
        goto finally
    end

    --检验采购单是否存在
    declare cursor_count cursor for
        select SERIAL_NO from #temp_d d where ISNULL(d.PURCHASE_TYPE,'')<>'' and not exists(select * from PUR_PURCHASE_M o where o.PURCHASE_TYPE=d.PURCHASE_TYPE and o.PURCHASE_NO=d.PURCHASE_NO) 
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
        select @msg = '以下序号项采购订单不存在 ' + char(13) +'%s'
        RAISERROR(@msg,16, 1, @err)
        goto finally
    end

    --检验采购订单序号与产品编号是否相符
    declare cursor_count cursor for
        select SERIAL_NO from #temp_d d where ISNULL(d.PURCHASE_TYPE,'')<>'' and not exists(select * from PUR_PURCHASE_D o where o.PURCHASE_TYPE=d.PURCHASE_TYPE and o.PURCHASE_NO=d.PURCHASE_NO and o.SERIAL_NO=d.PURCHASE_SERIAL_NO and o.PRO_NO=d.PRO_NO) 
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
        select @msg = '以下序号项采购订单序号与产品编号不相符 ' + char(13) +'%s'
        RAISERROR(@msg,16, 1, @err)
        goto finally
    end

    --检验送货单序号与产品编号是否相符
    declare cursor_count cursor for
        select SERIAL_NO from #temp_d d where ISNULL(d.RECEIVE_TYPE,'')<>'' and not exists(select * from PUR_RECEIVE_D o where o.RECEIVE_TYPE=d.RECEIVE_TYPE and o.RECEIVE_NO=d.RECEIVE_NO and o.SERIAL_NO=d.RECEIVE_SERIAL_NO and o.PRO_NO=d.PRO_NO) 
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
        select @msg = '以下序号项送货单序号与产品编号不相符 ' + char(13) +'%s'
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

    --检验产品是否需要批号
    declare cursor_count cursor for
        select SERIAL_NO from #temp_d d 
            where isnull(BATCH_NO,'')='' and exists(select * from PRODUCT p where p.PRO_NO=d.PRO_NO and p.MANAGE_BATCH=1) 
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

    --检验退货是否大于送货
    select PURCHASE_TYPE, PURCHASE_NO, SERIAL_NO, QTY, SPARE_QTY, cast(0 as float) as RECEIVE_QTY, cast(0 as float) as RECEIVE_SPARE_QTY, cast(0 as float) as RETURN_QTY , cast(0 as float) as RETURN_SPARE_QTY  
        into #temp_purchase 
        from PUR_PURCHASE_D 
        where exists(select * from #temp_d where PURCHASE_TYPE=PUR_PURCHASE_D.PURCHASE_TYPE and PURCHASE_NO=PUR_PURCHASE_D.PURCHASE_NO and PURCHASE_SERIAL_NO=PUR_PURCHASE_D.SERIAL_NO)

    insert into #temp_purchase(PURCHASE_TYPE, PURCHASE_NO, SERIAL_NO, QTY, RECEIVE_QTY, RETURN_QTY, SPARE_QTY, RECEIVE_SPARE_QTY, RETURN_SPARE_QTY)
        select PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO, 0, SUM(QTY), 0, 0, SUM(SPARE_QTY), 0
        from PUR_RECEIVE_D
        where exists(select * from #temp_d where PURCHASE_TYPE=PUR_RECEIVE_D.PURCHASE_TYPE and PURCHASE_NO=PUR_RECEIVE_D.PURCHASE_NO and PURCHASE_SERIAL_NO=PUR_RECEIVE_D.PURCHASE_SERIAL_NO)
        group by PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO

    insert into #temp_purchase(PURCHASE_TYPE, PURCHASE_NO, SERIAL_NO, QTY, RECEIVE_QTY, RETURN_QTY, SPARE_QTY, RECEIVE_SPARE_QTY, RETURN_SPARE_QTY)
        select PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO, 0, 0, SUM(QTY), 0, 0, SUM(SPARE_QTY)
        from PUR_CANCEL_D
        where exists(select * from #temp_d where PURCHASE_TYPE=PUR_CANCEL_D.PURCHASE_TYPE and PURCHASE_NO=PUR_CANCEL_D.PURCHASE_NO and PURCHASE_SERIAL_NO=PUR_CANCEL_D.PURCHASE_SERIAL_NO)
        group by PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO

    declare cursor_count cursor for
        select d.SERIAL_NO, SUM(p.QTY) as PUR_QTY, SUM(p.RETURN_QTY) as REC_QTY,  SUM(p.RETURN_QTY) as RET_QTY , SUM(p.SPARE_QTY) as PUR_SPARE_QTY, SUM(p.RECEIVE_SPARE_QTY) as REC_SPARE_QTY,  SUM(p.RETURN_SPARE_QTY) as RET_SPARE_QTY  
            from #temp_d d, #temp_purchase p
            where d.PURCHASE_TYPE=p.PURCHASE_TYPE and d.PURCHASE_NO=p.PURCHASE_NO and d.PURCHASE_SERIAL_NO=p.SERIAL_NO
            group by  d.SERIAL_NO
            having SUM(isnull(p.RETURN_QTY,0)) > SUM(isnull(p.RECEIVE_QTY, 0)) or SUM(isnull(p.RETURN_SPARE_QTY,0)) > SUM(isnull(p.RECEIVE_SPARE_QTY, 0))
			
    open cursor_count
    fetch next from cursor_count 
        into @serial_no, @p_qty, @s_qty, @r_qty, @p_spare_qty, @s_spare_qty, @r_spare_qty
    select @i=0,@err=''
    while @@FETCH_STATUS = 0
    begin
        select @i=@i+1
        if @i>10  begin select @err = @err + '...........' break end
        select @err = @err + cast(@serial_no as char(5)) + cast(@p_qty as char(12)) + cast(@s_qty as char(12)) + cast(@r_qty as char(12)) + cast(@p_spare_qty as char(12)) + cast(@s_spare_qty as char(12)) + cast(@r_spare_qty as char(12))
        fetch next from cursor_count 
            into @serial_no, @p_qty, @s_qty, @r_qty, @p_spare_qty, @s_spare_qty, @r_spare_qty
    end
    close cursor_count
    deallocate cursor_count
    drop table #temp_purchase
    if isnull(@err,'')<>'' begin
        select @msg = '以下序号项退料数量大于收料' + char(13) +' 序号  采购数量   已收  退料   采购备品   已收备品  退备品' + char(13) +'%s'
        RAISERROR(@msg,16, 1, @err)
        goto finally
    end

    finally:
        drop table #temp_m
        drop table #temp_d

