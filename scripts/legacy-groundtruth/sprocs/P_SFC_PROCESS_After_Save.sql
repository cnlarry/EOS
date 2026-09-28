

--产品制程
CREATE PROCEDURE P_SFC_PROCESS_After_Save
    @pri_idx nvarchar(1000), @module int
AS
    declare @sql nvarchar(4000),@msg varchar(500)
    declare @err varchar(4000)
    declare @i int, @serial_no int
    declare @pro_no nchar(30)

    select @sql = 'select @pro_no=PRO_NO from SFC_PROCESS_M  where ' + @pri_idx
    exec sp_executesql @sql, N'@pro_no nchar(30) output', @pro_no output

    select * into #temp_m from SFC_PROCESS_M where PRO_NO=@pro_no


    --检验产品编号是否存在
    if not exists(select * from PRODUCT p, #temp_m t where p.PRO_NO=t.PRO_NO) begin
        select @msg = '产品编号不存在 ' + char(13) +'%s'
        RAISERROR(@msg,16, 1, @err)
        goto finally
    end

    --检验产品编号是否使用固定时间时
    if exists(select * from SFC_PROCESS_D t where @pro_no=t.PRO_NO and t.STANDARD_TIME_TAG=1 and t.STANDARD_TIME=0) begin
        select @msg = '产品编号使用固定时间时，固定时间不能为0 ' + char(13) +'%s'
        RAISERROR(@msg,16, 1, @err)
        goto finally
    end

    --结束
    finally:
        drop table #temp_m

