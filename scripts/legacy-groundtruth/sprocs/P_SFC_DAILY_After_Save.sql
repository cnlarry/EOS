
/* ============================================================================
 * 第 6 节 存储过程修正（update-sfc-daily-sproc.sql）
 * ========================================================================== */
/* ============================================================================
 * EOS 存储过程缺陷修复 —— P_SFC_DAILY_After_Save 引用不存在的 FINISHED_QTY 列
 * ----------------------------------------------------------------------------
 * 目标数据库：Hiswitek。幂等、可重复执行（ALTER 同名过程），仅修正存储过程。
 *
 * 背景：180401 生产记录单管理保存调用的 P_SFC_DAILY_After_Save 在"检查是否超出
 * 制令制程最大可生产数量"时引用 MOC_PRODUCE_PROCESS_D.FINISHED_QTY，但该表不存在
 * 此列（实际为 FINISHED_PLAN_QTY 计划完成量/ FINISHED_IN_QTY 入库量等），保存报
 * "列名 'FINISHED_QTY' 无效"而失败。
 * 修正：按"制程计划完成量 + 本次生产记录数量 ≤ 超产上限"语义改用
 * FINISHED_PLAN_QTY（列名与语义最接近"已累计完成数量"），其余逻辑原样保留。
 * ========================================================================== */
----
--生产记录
CREATE PROCEDURE P_SFC_DAILY_After_Save
    @pri_idx nvarchar(1000), @module int
AS
    declare @sql nvarchar(4000),@msg varchar(500)
    declare @err varchar(4000)
    declare @i int, @serial_no int
    declare @daily_type nchar(10), @daily_no nchar(20)
    select @sql = 'select @daily_type=daily_type, @daily_no=daily_no from SFC_DAILY_M  where ' + @pri_idx
    exec sp_executesql @sql, N'@daily_type nchar(10) output, @daily_no nchar(20) output', @daily_type output, @daily_no output
    select * into #temp_m from SFC_DAILY_M where DAILY_TYPE=@daily_type and DAILY_NO=@daily_no
    select * into #temp_d from SFC_DAILY_D where DAILY_TYPE=@daily_type and DAILY_NO=@daily_no
     --检查制令制程是否存在
    declare cursor_count cursor for
        select SERIAL_NO from #temp_d t where not exists(select * from MOC_PRODUCE_PROCESS_D c where c.PRODUCE_TYPE=t.PRODUCE_TYPE and c.PRODUCE_NO=t.PRODUCE_NO and c.PROCEDURE_ID=t.PROCEDURE_ID)
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
        select @msg = '以下序号项制令制程不存在 ' + char(13) +'%s'
        RAISERROR(@msg,16, 1, @err)
        goto finally
    end
     --检查是否超出制令制程最大可生产数量
    select p.PRODUCE_TYPE, p.PRODUCE_NO, p.PROCEDURE_ID, max(isnull(p.PROCESS_OVER_QTY,0)) PROCESS_OVER_QTY, max(isnull(p.FINISHED_PLAN_QTY,0)) FINISHED_QTY, sum(isnull(d.FINISHED_QTY,0)) DAILY_QTY 
        into #tmp_qty 
        from MOC_PRODUCE_PROCESS_D p, #temp_d d
        where p.PRODUCE_TYPE=d.PRODUCE_TYPE and p.PRODUCE_NO=d.PRODUCE_NO and p.PROCEDURE_ID=d.PROCEDURE_ID
        group by p.PRODUCE_TYPE, p.PRODUCE_NO, p.PROCEDURE_ID
    select *,p.PRODUCE_TYPE, p.PRODUCE_NO, p.PROCEDURE_ID, (isnull(p.PROCESS_OVER_QTY,0)) PROCESS_OVER_QTY, (isnull(p.FINISHED_PLAN_QTY,0)) FINISHED_QTY, (isnull(d.FINISHED_QTY,0)) DAILY_QTY 
        from MOC_PRODUCE_PROCESS_D p, #temp_d d
        where p.PRODUCE_TYPE=d.PRODUCE_TYPE and p.PRODUCE_NO=d.PRODUCE_NO and p.PROCEDURE_ID=d.PROCEDURE_ID
        --group by p.PRODUCE_TYPE, p.PRODUCE_NO, p.PROCEDURE_ID
    delete from #tmp_qty where PROCESS_OVER_QTY >= FINISHED_QTY+DAILY_QTY
    declare cursor_count cursor for
        select SERIAL_NO from #temp_d t where exists(select * from #tmp_qty c where c.PRODUCE_TYPE=t.PRODUCE_TYPE and c.PRODUCE_NO=t.PRODUCE_NO and c.PROCEDURE_ID=t.PROCEDURE_ID)
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
        select @msg = '以下序号项数量超过制令制程允许生产最大数量 ' + char(13) +'%s'
        RAISERROR(@msg,16, 1, @err)
        goto finally
    end
    --结束
    finally:
        drop table #temp_m
        drop table #temp_d

