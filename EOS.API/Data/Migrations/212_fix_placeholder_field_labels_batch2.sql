/*------------------------------------------------------------------
  字段元数据订正（第二批）：
    A. 批核状态位退出复合格——状态位被配成「批核人」等字段的从字段（FORM_CELL_ROLE=2）后，
       表单只渲染主字段标签，状态位永远出不了自己的标签（PRODUCT 已由迁移 212 修正，本迁移
       处理其余 7 张表；按同类形态一次性收口，含 FINISHED_TAG）。
    B. 把 F_DESC 是占位文本 '&nbsp;' 的 36 行订正为真实字段名——同一批脚本把「无说明」写成
       HTML 空实体，读取侧的「空则回落字段代号」兜底同样拦不住（非空），标签会显示字面量。

  命名依据（逐条可考，不用猜测）：
    · LISTREPORT.T_SQL                 同表 T_CONDITION=条件 / T_CONDITION_DESC=条件描述，
                                       与该页「保存SQL语句及条件语句」的注释一致
    · MOC_GET_SHOWSUM.*                MOC_GET_SHOWSUM 是 V_MOC_GET_SHOWSUM 的同形视图
                                       （明细键 GET_TYPE,GET_NO,PRO_NO），
                                       列名与 MOC_GET_D 一一对应，直接取 MOC_GET_D 的既有中文名
    · MODULES.FILTER                   菜单维护页的「主表过滤条件」
    · PRODUCT.WORK_ALL                 同域 MOC_PRODUCE_M.WORK_ALL=生产工序（虚拟列取自 PRODUCT）
    · SYSQL_DEFAULT.{T_ID,T_ID_R,F_ID,F_IDX}
                                       姊妹表 SYSQL_FIELDS 同名列：表名 / 字段所属表 / 字段 / 顺序
    · SYSQR.R_M_IDX                    F_REMARK 自带「模块ID」；同族 SYSQR_DA/SYSQR_DEFAULT.M_IDX=模块ID
    · SYSQR_USER.F_TAG                 该位即条件行的复选框勾选态
    · TASK.*                           TASK 是「工作任务记录」SYS_WORK_TASK 的前身（同一套控件语义），
                                       逐列对齐该表既有中文名
    · WF_FUNCTION_INFO.*               用 function_key 作取值、function_expression 作显示、remark 作说明
    · WF_MYTASK.CAN_SIR_AGENCY         同表 APPROVE_POWER=可审批 / FORWARD_POWER=可解批 的命名口径
    · COP_PACK_D.BOX_CUBAGE_PCS        同表装箱列全是印唛英文表头，本列无可考出处
                                       ⇒ 按既有兜底口径回落字段代号

  幂等：A 段以"仍在复合格内"为命中条件，B 段以 F_DESC='&nbsp;' 为命中条件，重复执行不再命中。
------------------------------------------------------------------*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 51300, @GUARD, 1;

DECLARE @Now DATETIME = SYSDATETIME();
DECLARE @By NVARCHAR(20) = N'EOS-FIELD-LABEL';

/* ---------- A. 状态位退出复合格 ---------- */
UPDATE dbo.FIELDS
   SET FORM_CELL_ROLE = 0,
       FORM_CELL_GROUP = NULL,
       LAST_UPDATE_BY = @By,
       LAST_UPDATE_DATE = @Now
 WHERE LTRIM(RTRIM(F_ID)) IN (N'CONFIRM_TAG', N'FINISHED_TAG')
   AND ISNULL(FORM_CELL_ROLE, 0) <> 0;
PRINT N'== 状态位退出复合格 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

/* ---------- B. 占位文本 '&nbsp;' 订正为真实字段名 ---------- */
UPDATE f
   SET f.F_DESC = v.Label,
       f.LAST_UPDATE_BY = @By,
       f.LAST_UPDATE_DATE = @Now
  FROM dbo.FIELDS f
  JOIN (VALUES
        (N'MODULES',           N'FILTER',           N'主表过滤条件'),
        (N'COP_PACK_D',        N'BOX_CUBAGE_PCS',   N'BOX_CUBAGE_PCS'),
        (N'LISTREPORT',        N'T_SQL',            N'SQL语句'),
        (N'MOC_GET_SHOWSUM',   N'GET_TYPE',         N'领料单别'),
        (N'MOC_GET_SHOWSUM',   N'GET_NO',           N'领料单号'),
        (N'MOC_GET_SHOWSUM',   N'PRO_NO',           N'品号'),
        (N'MOC_GET_SHOWSUM',   N'QTY',              N'应领数量'),
        (N'MOC_GET_SHOWSUM',   N'RETURN_QTY',       N'应退数量'),
        (N'MOC_GET_SHOWSUM',   N'RETURNED_QTY',     N'已退数量'),
        (N'MOC_GET_SHOWSUM',   N'SEND_QTY',         N'发料数量'),
        (N'PRO_PROCESS_SORT',  N'PROCESS_SORT',     N'工序排序'),
        (N'PRODUCT',           N'WORK_ALL',         N'生产工序'),
        (N'SYS_FILE',          N'REMARK',           N'备注'),
        (N'SYSQL_DEFAULT',     N'T_ID',             N'表名'),
        (N'SYSQL_DEFAULT',     N'T_ID_R',           N'字段所属表'),
        (N'SYSQL_DEFAULT',     N'F_ID',             N'字段'),
        (N'SYSQL_DEFAULT',     N'F_IDX',            N'顺序'),
        (N'SYSQR',             N'R_M_IDX',          N'模块ID'),
        (N'SYSQR_USER',        N'F_TAG',            N'是否启用'),
        (N'TASK',              N'TASK_ID',          N'任务编号'),
        (N'TASK',              N'TASK_NAME',        N'任务名称'),
        (N'TASK',              N'TASK_DATE',        N'任务日期'),
        (N'TASK',              N'TASK_TYPE',        N'任务类别'),
        (N'TASK',              N'PLAN_START_TIME',  N'预计开始时间'),
        (N'TASK',              N'PLAN_END_TIME',    N'预计完成时间'),
        (N'TASK',              N'FACT_START_TIME',  N'实际开始时间'),
        (N'TASK',              N'FACT_END_TIME',    N'完成时间'),
        (N'TASK',              N'TEST_STEP',        N'测试操作步骤'),
        (N'TASK',              N'TEST_QUESTION',    N'测试发现问题'),
        (N'TASK',              N'WHYS',             N'问题原因'),
        (N'TASK',              N'METHOD',           N'解决方法'),
        (N'TASK',              N'SUCCESS_TAG',      N'完成状态'),
        (N'WF_FUNCTION_INFO',  N'function_key',     N'功能键'),
        (N'WF_FUNCTION_INFO',  N'function_expression', N'功能表达式'),
        (N'WF_FUNCTION_INFO',  N'remark',           N'备注'),
        (N'WF_MYTASK',         N'CAN_SIR_AGENCY',   N'可由上级代理'))
       AS v(TableId, FieldId, Label)
    ON LTRIM(RTRIM(f.T_ID)) = v.TableId
   AND LTRIM(RTRIM(f.F_ID)) = v.FieldId
 WHERE f.F_DESC = N'&nbsp;';
PRINT N'== 占位字段名订正 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

/* ---------- 收口断言 ---------- */
IF EXISTS (SELECT 1 FROM dbo.FIELDS
           WHERE LTRIM(RTRIM(F_ID)) IN (N'CONFIRM_TAG', N'FINISHED_TAG') AND ISNULL(FORM_CELL_ROLE, 0) <> 0)
    THROW 51301, N'字段元数据：仍有状态位被配成从字段，其标签不可见。', 1;

IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE F_DESC = N'&nbsp;')
    THROW 51302, N'字段元数据：仍有 F_DESC 为 ''&nbsp;'' 的占位行。', 1;

IF EXISTS (SELECT 1 FROM dbo.FIELDS
           WHERE LTRIM(RTRIM(T_ID)) IN (N'TASK', N'SYSQL_DEFAULT', N'MOC_GET_SHOWSUM', N'WF_FUNCTION_INFO')
             AND NULLIF(LTRIM(RTRIM(F_DESC)), N'') IS NULL)
    THROW 51303, N'字段元数据：本批订正的表存在无字段名的行。', 1;

PRINT N'字段元数据订正（第二批）完成：状态位标签可见，占位字段名已还原。';
GO
