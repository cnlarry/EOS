

-- 备货返仓单
CREATE PROCEDURE P_CURR_After_Save
    @pri_idx nvarchar(1000), @module int
AS
    declare @sql nvarchar(4000),@msg varchar(500)
    declare @err varchar(4000)
    declare @curr_id nchar(10), @base_curr nchar(10), @is_base bit, @curr_rate float

    select @sql = 'select @curr_id=CURR_ID, @is_base=IS_BASE, @curr_rate=CURR_RATE from CURR  where ' + @pri_idx
    exec sp_executesql @sql, N'@curr_id nchar(10) output, @is_base bit output, @curr_rate float output', @curr_id output, @is_base output, @curr_rate output

    if @is_base=1 begin
        select @base_curr = CURR_ID from CURR where CURR_ID != @curr_id and IS_BASE=1
        if(isnull(@base_curr,'') != '') begin
            select @msg = '已将币别 [' + rtrim(@base_curr) + '] 设为本位币，不能存在两种本位币'
            RAISERROR(@msg,16, 1, @err)
            goto finally
        end
        if(isnull(@curr_rate,0) != 1) begin
            select @msg = '本位币汇率只能为1'
            RAISERROR(@msg,16, 1)
            goto finally
        end
    end

    finally:

