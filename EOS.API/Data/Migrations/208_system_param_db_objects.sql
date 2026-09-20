/*------------------------------------------------------------------
  系统参数纵向化的库对象侧收尾（承接 208 的表改造）：

  1) 新增 dbo.f_sys_param(ownerModule, paramKey)：参数取值在 SQL 侧的唯一定义点——
     返回 ISNULL(PARAM_VALUE, DEFAULT_VALUE)；返回 NULL 表示参数不存在，或取值与默认值皆空。
     调用方用 ISNULL(CAST(... AS int), 0) 兜底，保持"开关缺失即关闭"的 fail-closed 语义；
  2) P_HRM_WAGE_CALC：工资计算的最后一条遗留过程，原文按 HR_SETUP.DIMISSION_NO_WAGE 判定
     "离职当月不保存工资"，改为按键取参数，其余逻辑逐字不动；
  3) 退役 dbo.f_get_rest_days / dbo.f_get_work_days：考勤日历天数计算已由服务层实现，
     这两个函数在库内无任何引用、代码与脚本中亦无调用，属死对象。

  断言：改写后的过程定义里不再出现旧表名；两函数确认已不存在；参数取值函数与备份表逐键一致。
------------------------------------------------------------------*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/* 1. 参数取值函数：NULL ⇒ DEFAULT_VALUE，仍为 NULL 则返回 NULL */
CREATE OR ALTER FUNCTION dbo.f_sys_param (@owner_module int, @param_key nvarchar(64))
RETURNS nvarchar(4000)
AS
BEGIN
    RETURN (SELECT TOP 1 ISNULL(p.PARAM_VALUE, p.DEFAULT_VALUE)
            FROM dbo.SYSSS p
            WHERE p.OWNER_MODULE = @owner_module AND p.PARAM_KEY = @param_key);
END
GO

/* 2. 工资计算过程：只把"离职当月不保存工资"的开关判定改为按键取参数 */
CREATE OR ALTER PROCEDURE dbo.P_HRM_WAGE_CALC
	(@wage_type NCHAR(10),@wage_no NCHAR(20), @emp_ids VARCHAR(1000), @calc_mode char(1), @if_secrecy bit=0)
AS
	DECLARE @year CHAR(4),@month CHAR(2),@dept_id NCHAR(10)
	DECLARE @sql VARCHAR(8000)
	DECLARE @round INT
	SELECT @year=SUBSTRING(COUNT_MONTH,1,4), @month=SUBSTRING(COUNT_MONTH,5,6), @dept_id=LTRIM(RTRIM(ISNULL(DEPT_ID,''))) FROM HRM_WAGE_M WHERE WAGE_TYPE=@wage_type AND WAGE_NO=@wage_no

	DECLARE @ymsql VARCHAR(1000), @filter_sql VARCHAR(1000), @serial_no int
	DECLARE @wage_field VARCHAR(50), @source_sql VARCHAR(4000), @source_exp VARCHAR(2000)

	SELECT @ymsql='', @filter_sql='', @serial_no=1, @emp_ids=ISNULL(@emp_ids,''), @calc_mode=ISNULL(@calc_mode,'A')

	SELECT @ymsql =  'declare @wage_type NCHAR(10),@wage_no NCHAR(20),@dept_id varchar(10),@emp_ids varchar(1000),@year int,@month int, @if_secrecy bit, @serial_no int  
		 select @wage_type='''+@wage_type+''', @wage_no='''+@wage_no+''', @year='+@year+', @month='+@month+',@dept_id='''+@dept_id+''',@emp_ids='''+@emp_ids+''',@if_secrecy='+cast(@if_secrecy as char(1))+', @serial_no=0 '
	BEGIN TRAN
	IF @calc_mode='A' BEGIN --计算整单
		--部门条件语句
		IF ISNULL(@dept_id,'') != ''
			SELECT @filter_sql = ' AND source.EMP_ID in(select EMP_ID from HR_EMPLOYEE where DEPT_ID in (select DEPT_ID from dbo.f_get_under_depts(@dept_id))) '

		PRINT '删除单据中有而不需要计算薪资的员工'
		SELECT @sql = ' DELETE HRM_WAGE_D where WAGE_TYPE=@wage_type and WAGE_NO=@wage_no 
			and EMP_ID in (select EMP_ID from HR_EMPLOYEE where isnull(IF_SECRECY,0)<>@if_secrecy or isnull(IF_COUNT,0)=0 or (year(IN_DATE)*100+month(IN_DATE)>@year*100+@month) or (year(DIMISSION_DATE)*100+month(DIMISSION_DATE)<@year*100+@month) '
		IF ISNULL(@dept_id,'') != ''
			SELECT @sql = @sql + ' or DEPT_ID not in(select DEPT_ID from  dbo.f_get_under_depts(@dept_id))) '
		ELSE
			SELECT @sql = @sql +')'
		PRINT(@ymsql + @sql)
		EXEC(@ymsql+@sql)
		IF @@ERROR<>0
			GOTO ErrorHandler

		PRINT '插入单据中没有而需要计算薪资的新员工'
		SELECT @sql = ' SELECT @wage_type AS WAGE_TYPE, @wage_no as  WAGE_NO, @serial_no as SERIAL_NO, EMP_ID into #tmp FROM HR_EMPLOYEE source 
			WHERE IF_COUNT=1' + @filter_sql + ' and isnull(IF_SECRECY,0)=@if_secrecy and (year(IN_DATE)*100+month(IN_DATE)<=@year*100+@month) and (DIMISSION_DATE is null or year(DIMISSION_DATE)*100+month(DIMISSION_DATE)>=@year*100+@month) and EMP_ID not in (select EMP_ID from HRM_WAGE_M m, HRM_WAGE_D d where m.WAGE_TYPE=d.WAGE_TYPE and m.WAGE_NO=d.WAGE_NO and m.COUNT_MONTH=''' + @year + @month + ''') ORDER BY DEPT_ID,EMP_ID
			select @serial_no=max(SERIAL_NO) from HRM_WAGE_D where WAGE_TYPE=@wage_type and WAGE_NO=@wage_no
			select @serial_no = isnull(@serial_no, 0)
			update #tmp set @serial_no=@serial_no+1, SERIAL_NO=@serial_no  
			INSERT INTO HRM_WAGE_D(WAGE_TYPE, WAGE_NO,SERIAL_NO,EMP_ID)
			SELECT WAGE_TYPE, WAGE_NO,SERIAL_NO,EMP_ID FROM #tmp drop table #tmp ' 

		PRINT(@ymsql + @sql)
		EXEC(@ymsql+@sql)
		IF @@ERROR<>0
			GOTO ErrorHandler

		--PRINT '删除原记录'
		--SELECT @sql = 'DELETE HRM_WAGE_D  WHERE WAGE_TYPE=@wage_type AND WAGE_NO=@wage_no'
		--PRINT(@ymsql + @sql)
		--EXEC(@ymsql + @sql)
		--IF @@ERROR<>0
		--	GOTO ErrorHandler
		--PRINT '插入需计算薪资的员工'
		--SELECT @sql = 'SELECT @wage_type AS WAGE_TYPE, @wage_no as  WAGE_NO, @serial_no as SERIAL_NO, EMP_ID into #tmp FROM HR_EMPLOYEE source 
		--		WHERE IF_COUNT=1' + @filter_sql + ' update #tmp set @serial_no=@serial_no+1, SERIAL_NO=@serial_no  
		--	INSERT INTO HRM_WAGE_D(WAGE_TYPE, WAGE_NO,SERIAL_NO,EMP_ID)
		--   SELECT WAGE_TYPE, WAGE_NO,SERIAL_NO,EMP_ID FROM #tmp drop table #tmp ' 
		--PRINT(@ymsql + @sql)
		--EXEC(@ymsql+@sql)
		--IF @@ERROR<>0
		--	GOTO ErrorHandler
	END
	ELSE BEGIN --计算指定人员
		--人员条件
		SELECT @filter_sql = ' AND source.EMP_ID in('''+REPLACE(@emp_ids,',',''',''')+''') '
		PRINT '删除单据中有而不需要计算薪资的员工'
		SELECT @sql = 'DELETE HRM_WAGE_D where WAGE_TYPE=@wage_type and WAGE_NO=@wage_no  
			and EMP_ID in('''+REPLACE(@emp_ids,',',''',''')+''')  and EMP_ID in (select EMP_ID from HR_EMPLOYEE where isnull(IF_SECRECY,0)<>@if_secrecy or isnull(IF_COUNT,0)=0 or (year(IN_DATE)*100+month(IN_DATE)>@year*100+@month) or (year(DIMISSION_DATE)*100+month(DIMISSION_DATE)<@year*100+@month)) '
		PRINT(@ymsql + @sql)
		EXEC(@ymsql+@sql)
		IF @@ERROR<>0
			GOTO ErrorHandler
		--SELECT @sql = 'DELETE HRM_WAGE_D  WHERE WAGE_TYPE=@wage_type AND WAGE_NO=@wage_no' + ' AND EMP_ID in('''+REPLACE(@emp_ids,',',''',''')+''') '
		--PRINT(@ymsql + @sql)
		--EXEC(@ymsql + @sql)
		--IF @@ERROR<>0
		--	GOTO ErrorHandler
	END

	IF @@ERROR<>0
		GOTO ErrorHandler

	--删除员工基本资料中没有的员工
	delete from HRM_WAGE_D where WAGE_TYPE=@wage_type AND WAGE_NO=@wage_no and EMP_ID not in (select EMP_ID from HR_EMPLOYEE)

	--删除当月离职员工
	if ISNULL(CAST(dbo.f_sys_param(180213, N'DIMISSION_NO_WAGE') AS int),0)=1
		delete from HRM_WAGE_D where WAGE_TYPE=@wage_type AND WAGE_NO=@wage_no and EMP_ID in (select EMP_ID from HR_EMPLOYEE where year(DIMISSION_DATE)*100+month(DIMISSION_DATE)>=@year*100+@month)

	PRINT '计算薪资项目'
	DECLARE cur_wage CURSOR FOR SELECT ISNULL(SOURCE_SQL, ''), LTRIM(RTRIM(ISNULL(SOURCE_EXP, ''))), ISNULL(WAGE_FIELD,'') FROM HRM_WAGE WHERE ISNULL(IS_USED, 0)=1 ORDER BY CALC_ORDER
	OPEN cur_wage
	FETCH NEXT FROM cur_wage INTO @source_sql, @source_exp, @wage_field
	WHILE @@FETCH_STATUS = 0 BEGIN
		SELECT @round = (CASE WHEN LEN(display_format)>0 THEN PATINDEX('%.%',reverse(display_format)) ELSE 3 END) FROM fields WHERE T_ID='HRM_WAGE_D' and F_ID=@wage_field
		IF @round>0  BEGIN
			SELECT @round=@round-1
		END
		IF @source_exp !='' BEGIN
			SELECT @sql ='update HRM_WAGE_D set '+@wage_field +'=0  where WAGE_TYPE=@wage_type and WAGE_NO=@wage_no ' + REPLACE(@filter_sql, 'source.', '') 
				+' update HRM_WAGE_D set '+@wage_field+'=round(' + @source_exp + ',' + STR(@round) + ') where WAGE_TYPE=@wage_type and WAGE_NO=@wage_no ' + REPLACE(@filter_sql, 'source.', '')
		END
		ELSE  IF @source_sql !='' BEGIN
			SELECT @sql ='update HRM_WAGE_D set '+@wage_field +'=0 where WAGE_TYPE=@wage_type and WAGE_NO=@wage_no '  +  REPLACE(@filter_sql, 'source.', '') 
				+' update HRM_WAGE_D set '+@wage_field+'=round(source.VALUE,' + STR(@round) + ')  from (' + @source_sql + ') source
				where HRM_WAGE_D.EMP_ID=source.EMP_ID and WAGE_TYPE=@wage_type and WAGE_NO=@wage_no '
			   + @filter_sql
		END
		PRINT(@ymsql + @sql)
		EXEC(@ymsql +@sql)
		IF @@ERROR<>0 BEGIN
			CLOSE cur_wage
			DEALLOCATE cur_wage
			GOTO ErrorHandler
		END
		FETCH NEXT FROM cur_wage INTO @source_sql, @source_exp, @wage_field
	END
	CLOSE cur_wage
	DEALLOCATE cur_wage
	UPDATE HRM_WAGE_M SET LAST_UPDATE_DATE=cast(getdate() as smalldatetime) WHERE WAGE_TYPE=@wage_type AND WAGE_NO=@wage_no
--结束
IF @@ERROR<>0   AND @@TRANCOUNT <> 0
	ROLLBACK TRAN
ELSE
	COMMIT TRAN
ErrorHandler:
	IF @@TRANCOUNT <> 0
		ROLLBACK TRAN
GO

/* 3. 退役考勤日历天数计算的两个函数（无引用、无调用方） */
DROP FUNCTION IF EXISTS dbo.f_get_rest_days;
DROP FUNCTION IF EXISTS dbo.f_get_work_days;
GO

/* 4. 断言 */
IF OBJECT_ID(N'dbo.f_sys_param', N'FN') IS NULL
    THROW 51000, N'系统参数纵向化：取值函数 f_sys_param 未建立。', 1;

IF CHARINDEX(N'HR_SETUP', ISNULL(OBJECT_DEFINITION(OBJECT_ID(N'dbo.P_HRM_WAGE_CALC')), N'')) > 0
    THROW 51000, N'系统参数纵向化：工资计算过程仍引用旧表 HR_SETUP。', 1;

IF OBJECT_ID(N'dbo.f_get_rest_days', N'FN') IS NOT NULL OR OBJECT_ID(N'dbo.f_get_work_days', N'FN') IS NOT NULL
    THROW 51000, N'系统参数纵向化：考勤日历天数函数未退役。', 1;

IF dbo.f_sys_param(180213, N'DIMISSION_NO_WAGE') IS NOT NULL
    THROW 51000, N'系统参数纵向化：离职当月不保存工资的取值与迁移前不一致。', 1;

IF dbo.f_sys_param(180662, N'SAT_REST_DAY') <> N'1'
    THROW 51000, N'系统参数纵向化：零行源表的默认值兜底失效。', 1;

IF dbo.f_sys_param(180213, N'__NOT_A_PARAM__') IS NOT NULL
    THROW 51000, N'系统参数纵向化：未知参数键未返回 NULL。', 1;

/* 与备份表逐键一致（备份表由 208 建立，收口前一直保留） */
IF OBJECT_ID(N'bak.HR_SETUP_BAK_20260920', N'U') IS NOT NULL
BEGIN
    DECLARE @cmp nvarchar(max) = N'';
    SELECT @cmp = @cmp + CASE WHEN @cmp = N'' THEN N'' ELSE N' UNION ALL ' END
               + N'SELECT N''' + PARAM_KEY + N''' AS K, dbo.f_sys_param(180213, N''' + PARAM_KEY
               + N''') AS V, CONVERT(nvarchar(4000), [' + PARAM_KEY + N']) AS W FROM bak.HR_SETUP_BAK_20260920'
    FROM dbo.SYSSS WHERE OWNER_MODULE = 180213;

    SET @cmp = N'IF EXISTS (SELECT 1 FROM (' + @cmp + N') t WHERE ISNULL(t.V, N''~'') <> ISNULL(t.W, N''~''))'
             + N' THROW 51000, N''系统参数纵向化：参数取值函数与备份表逐键比对不一致。'', 1;';
    EXEC sp_executesql @cmp;
END

PRINT N'系统参数纵向化（库对象）：f_sys_param 就位，工资计算过程改按键取参数，两个考勤天数函数已退役。';
GO
