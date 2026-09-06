using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.RegularExpressions;

using EOS.API.Data.Effects;
using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>
/// 通用工作流审批链。
/// 复用旧 WF_* 表：WFFORM/WFFORM_FLOW（流程定义）、WF_MONITOR（运行实例）、
/// WF_MYTASK（待办任务）、WF_MYTASK_LOG（审批日志）、WF_APPROVE（单据批核历史）。
/// 无流程模块保持"直接批核"（DocumentWorkbenchRepository.WorkflowAsync）不变。
/// 动态条件（EXEC_CONDITION/PERSON_CONDITION/AUTO_EXEC_CONDITION）一律经 DataFilterParser
/// 按主表物理列白名单受控解析（参数化，不可解析即拒绝流程启动），绝不拼接用户输入。
/// 权限：PERSON_APP_POWER/PERSON_FORWARD_POWER 按人解析（空串=全部有权限）。
/// 会签：IS_SIGN=1 步骤按 PASS_PERCENT 计数阈值 + MUST_SIGNER 必签者整步通过。
/// 跳转：同意 + FORWARD_POWER 向前跳（'0'=直接结束，中间未批任务标记跳过）；
/// 驳回可退回指定步骤（jumpNo 为空=第一步），先过解批前置校验（NOT_BACK_FIELDS）。
/// 代理（CAN_SIR_AGENCY）按旧 f_get_sirs 校验。
/// </summary>
public sealed class WorkflowEngine(
    DbConnectionFactory connections,
    ControlledSprocInvoker controlledSprocs,
    WorkbenchAuditWriter auditWriter,
    WorkbenchDefinitionProvider definitionProvider,
    EffectEngineInvoker effectEngine,
    ILogger<WorkflowEngine> logger)
{

    /// <summary>流程步骤定义（WFFORM_FLOW 行，权限串/条件串按旧语义逐人解析）。</summary>
    private sealed record FlowStep(
        string SortNo,
        string Desc,
        string[] People,
        string ExecCondition,
        string[] PersonConditions,
        string[] ApprovePowers,
        string[] ForwardPowers,
        string AutoExecCondition,
        bool IsAutoExec,
        bool IsSign,
        int PassPercent,
        bool IsEffect,
        bool PreMustUnder,
        bool CanSirAgency,
        string[] MustSigners);

    /// <summary>模块是否配置了流程（WFFORM + 至少一步 WFFORM_FLOW）。</summary>
    public static async Task<bool> HasFlowAsync(SqlConnection connection, int moduleId, CancellationToken token)
    {
        const string sql = """
            SELECT TOP 1 1 FROM dbo.WFFORM wf WITH (NOLOCK)
            WHERE wf.WF_M_IDX=@ModuleId
              AND EXISTS (SELECT 1 FROM dbo.WFFORM_FLOW f WITH (NOLOCK) WHERE f.WF_M_IDX=wf.WF_M_IDX);
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        return await command.ExecuteScalarAsync(token) is not null;
    }

    /// <summary>
    /// 单据是否存在在途流程实例（WF_MONITOR WF_STATE='0'）。
    /// keyCondition 必须来自服务端 BuildKeyCondition（服务端权威，非用户输入）。
    /// 供编辑/删除守卫使用：在途流程的单据禁止编辑（改了审批人批的是旧数据）与删除（留孤儿流程实例）。
    /// </summary>
    public static async Task<bool> HasActiveFlowAsync(
        SqlConnection connection, SqlTransaction? transaction,
        int moduleId, string keyCondition, CancellationToken token)
    {
        const string sql = $"""
            SELECT TOP 1 1 FROM dbo.WF_MONITOR WITH (NOLOCK)
            WHERE WF_M_IDX=@ModuleId AND KEY_VALUE=@KeyValue AND WF_STATE='{WorkflowStates.MonitorInProgress}';
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@KeyValue", SqlDbType.VarChar, 200).Value = keyCondition;
        return await command.ExecuteScalarAsync(token) is not null;
    }

    /// <summary>
    /// 启动流程：建/取 WF_MONITOR，删除旧任务，按 WFFORM_FLOW.SORT_NO 重建 WF_MYTASK。
    /// 动态条件（EXEC_CONDITION/PERSON_CONDITION/AUTO_EXEC_CONDITION）经 DataFilterParser
    /// 受控评估：步骤执行条件为真的步骤才生成任务，审批人条件为真的人员才入任务；
    /// 权限串（PERSON_APP_POWER/PERSON_FORWARD_POWER）按人解析，空串=全部有权限；
    /// 首个具备审批权的步骤置为当前；自动执行步骤由 SYSTEM 自动同意。
    /// submitMessage（可选）：送审说明，写入提交送审日志（APPROVE_STATE='A'）并记入审计。
    /// </summary>
    public async Task<RecordSaveResult> StartFlowAsync(
        WorkbenchDefinition definition,
        IReadOnlyList<string> keyValues,
        string employeeName,
        string userId,
        CancellationToken token,
        string? submitMessage = null)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        var keyCondition = ControlledSprocInvoker.BuildKeyCondition(definition.MasterPkOrder, keyValues);

        long wfId;
        string existingState = string.Empty;
        await using (var select = new SqlCommand("""
            SELECT WF_ID, LTRIM(RTRIM(ISNULL(WF_STATE,''))) FROM dbo.WF_MONITOR WITH (UPDLOCK, HOLDLOCK)
            WHERE WF_M_IDX=@ModuleId AND KEY_VALUE=@KeyValue;
            """, connection, transaction))
        {
            select.Parameters.Add("@ModuleId", SqlDbType.Int).Value = definition.ModuleId;
            select.Parameters.Add("@KeyValue", SqlDbType.VarChar, 200).Value = keyCondition;
            await using var existingReader = await select.ExecuteReaderAsync(token);
            if (await existingReader.ReadAsync(token))
            {
                wfId = existingReader.GetInt64(0);
                existingState = existingReader.GetString(1);
            }
            else
            {
                await existingReader.DisposeAsync();
                await using var insert = new SqlCommand("""
                    INSERT INTO dbo.WF_MONITOR (WF_M_IDX, KEY_VALUE, KEY_VALUE_DESC, WF_STATE, UPDATE_SUBFLOW, START_USER, START_DATE)
                    VALUES (@ModuleId, @KeyValue, @KeyValueDesc, '0', '', @StartUser, GETDATE());
                    SELECT CAST(SCOPE_IDENTITY() AS bigint);
                    """, connection, transaction);
                insert.Parameters.Add("@ModuleId", SqlDbType.Int).Value = definition.ModuleId;
                insert.Parameters.Add("@KeyValue", SqlDbType.VarChar, 200).Value = keyCondition;
                insert.Parameters.Add("@KeyValueDesc", SqlDbType.VarChar, 300).Value =
                    keyCondition.Length > 300 ? keyCondition[..300] : keyCondition;
                insert.Parameters.Add("@StartUser", SqlDbType.NVarChar, 50).Value = userId;
                wfId = Convert.ToInt64(await insert.ExecuteScalarAsync(token));
            }
        }

        // 在途守卫：已存在审批中流程实例（WF_STATE='0'）时拒绝重复送审，
        // 防止静默重建审批链导致在途任务丢失/进度被重置。发起人须先撤回（WF_STATE='2'）再重新提交。
        if (existingState == "0")
        {
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "FLOW_IN_PROGRESS",
                "该单据流程正在审批中，如需修改请先撤回后重新送审。");
        }

        // 重新提交/刷新：流程状态复位为在途，发起人刷新为当前送审人（覆盖已撤回 '2' 的复位）
        await using (var reset = new SqlCommand(
            $"UPDATE dbo.WF_MONITOR SET WF_STATE='{WorkflowStates.MonitorInProgress}', START_USER=@UserId, START_DATE=GETDATE() WHERE WF_ID=@WfId;",
            connection, transaction))
        {
            reset.Parameters.Add("@UserId", SqlDbType.NVarChar, 50).Value = userId;
            reset.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
            await reset.ExecuteNonQueryAsync(token);
        }

        await using (var clear = new SqlCommand("DELETE FROM dbo.WF_MYTASK WHERE WF_ID=@WfId;", connection, transaction))
        {
            clear.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
            await clear.ExecuteNonQueryAsync(token);
        }

        // 步骤定义：权限串/条件串按旧语义逐人解析（空权限串=全部有权限）
        var steps = new List<FlowStep>();
        await using (var stepCommand = new SqlCommand("""
            SELECT SORT_NO, LTRIM(RTRIM(ISNULL(SUBFLOW_DESC,''))), LTRIM(RTRIM(ISNULL(EXEC_PERSON,''))),
                   LTRIM(RTRIM(ISNULL(EXEC_CONDITION,''))), LTRIM(RTRIM(ISNULL(PERSON_CONDITION,''))),
                   LTRIM(RTRIM(ISNULL(PERSON_APP_POWER,''))), LTRIM(RTRIM(ISNULL(PERSON_FORWARD_POWER,''))),
                   LTRIM(RTRIM(ISNULL(AUTO_EXEC_CONDITION,''))), ISNULL(CAST(IS_AUTO_EXEC AS int),0),
                   ISNULL(CAST(IS_SIGN AS int),0), ISNULL(CAST(PASS_PERCENT AS int),0),
                   ISNULL(CAST(IS_EFFECT AS int),0), ISNULL(CAST(PRE_MUST_UNDER AS int),0),
                   ISNULL(CAST(CAN_SIR_AGENCY AS int),0), LTRIM(RTRIM(ISNULL(MUST_SIGNER,'')))
            FROM dbo.WFFORM_FLOW WITH (NOLOCK)
            WHERE WF_M_IDX=@ModuleId ORDER BY SORT_NO;
            """, connection, transaction))
        {
            stepCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = definition.ModuleId;
            await using var reader = await stepCommand.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                steps.Add(new FlowStep(
                    reader.GetString(0).Trim(),
                    reader.GetString(1),
                    reader.GetString(2).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                    reader.GetString(3),
                    reader.GetString(4).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                    reader.GetString(5).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                    reader.GetString(6).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                    reader.GetString(7),
                    reader.GetInt32(8) == 1,
                    reader.GetInt32(9) == 1,
                    reader.GetInt32(10),
                    reader.GetInt32(11) == 1,
                    reader.GetInt32(12) == 1,
                    reader.GetInt32(13) == 1,
                    reader.GetString(14).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)));
            }
        }

        if (steps.Count == 0)
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "FLOW_NO_STEPS", "流程未配置审批步骤。");

        // 主表物理列白名单（INFORMATION_SCHEMA，服务端权威）供条件评估使用
        var masterColumns = await LoadMasterColumnsAsync(connection, transaction, definition.MasterTable, token);

        string? firstApproveStep = null;
        const string insertSql = """
            INSERT INTO dbo.WF_MYTASK
                (WF_ID, SUBFLOW_NO, SUBFLOW_DESC, APPROVER, APPROVE_TAG, APPROVE_STATE,
                 IS_AUTO_EXEC, IS_SIGN, IS_MUST_SIGN, PASS_PERCENT, IS_EFFECT, PRE_MUST_UNDER,
                 CAN_SIR_AGENCY, APPROVE_POWER, FORWARD_POWER, IS_CURRENT)
            VALUES
                (@WfId, @SubflowNo, @SubflowDesc, @Approver, 0, '',
                 @IsAutoExec, @IsSign, @IsMustSign, @PassPercent, @IsEffect, @PreMustUnder,
                 @CanSirAgency, @ApprovePower, @ForwardPower, @IsCurrent);
            """;
        foreach (var step in steps)
        {
            // 步骤执行条件（EXEC_CONDITION）：为假则整步不生成任务
            var execResult = await EvaluateConditionAsync(connection, transaction, definition, keyCondition,
                masterColumns, step.ExecCondition, token);
            if (execResult is null)
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "FLOW_CONDITION_UNSUPPORTED",
                    $"步骤 {step.SortNo} 的动态条件无法安全解析，已拒绝启动。");
            if (!execResult.Value)
                continue;

            // 自动执行条件（AUTO_EXEC_CONDITION）：仅 IS_AUTO_EXEC=1 时步骤级评估一次
            bool? stepAuto = null;
            if (step.IsAutoExec && !string.IsNullOrWhiteSpace(step.AutoExecCondition))
            {
                stepAuto = await EvaluateConditionAsync(connection, transaction, definition, keyCondition,
                    masterColumns, step.AutoExecCondition, token);
                if (stepAuto is null)
                    return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "FLOW_CONDITION_UNSUPPORTED",
                        $"步骤 {step.SortNo} 的自动执行条件无法安全解析，已拒绝启动。");
            }

            foreach (var (person, index) in step.People.Select((person, index) => (person, index)))
            {
                // 审批人条件（PERSON_CONDITION）：与 EXEC_PERSON 一一对应；缺省视为无条件
                var personCondition = index < step.PersonConditions.Length ? step.PersonConditions[index] : string.Empty;
                var personResult = await EvaluateConditionAsync(connection, transaction, definition, keyCondition,
                    masterColumns, personCondition, token);
                if (personResult is null)
                    return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "FLOW_CONDITION_UNSUPPORTED",
                        $"步骤 {step.SortNo} 审批人 {person} 的动态条件无法安全解析，已拒绝启动。");
                if (!personResult.Value)
                    continue;

                var approvePower = HasPower(person, step.ApprovePowers);
                var forwardPower = HasPower(person, step.ForwardPowers);
                // 必签名单（MUST_SIGNER）：非空且包含该人；空名单=无必签者（与权限串"空=全部"语义不同）
                var isMustSign = step.IsSign && step.MustSigners.Length > 0
                    && step.MustSigners.Contains(person, StringComparer.OrdinalIgnoreCase);
                var isAutoExec = step.IsAutoExec && (stepAuto ?? true);
                if (approvePower && firstApproveStep is null)
                    firstApproveStep = step.SortNo;

                await using var taskCommand = new SqlCommand(insertSql, connection, transaction);
                taskCommand.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
                taskCommand.Parameters.Add("@SubflowNo", SqlDbType.Char, 3).Value = step.SortNo;
                taskCommand.Parameters.Add("@SubflowDesc", SqlDbType.VarChar, 50).Value = step.Desc;
                taskCommand.Parameters.Add("@Approver", SqlDbType.VarChar, 20).Value = person;
                taskCommand.Parameters.Add("@IsAutoExec", SqlDbType.Bit).Value = isAutoExec;
                taskCommand.Parameters.Add("@IsSign", SqlDbType.Bit).Value = step.IsSign;
                taskCommand.Parameters.Add("@IsMustSign", SqlDbType.Bit).Value = isMustSign;
                taskCommand.Parameters.Add("@PassPercent", SqlDbType.Int).Value = step.PassPercent;
                taskCommand.Parameters.Add("@IsEffect", SqlDbType.Bit).Value = step.IsEffect;
                taskCommand.Parameters.Add("@PreMustUnder", SqlDbType.Bit).Value = step.PreMustUnder;
                taskCommand.Parameters.Add("@CanSirAgency", SqlDbType.Bit).Value = step.CanSirAgency;
                taskCommand.Parameters.Add("@ApprovePower", SqlDbType.Bit).Value = approvePower;
                taskCommand.Parameters.Add("@ForwardPower", SqlDbType.Bit).Value = forwardPower;
                taskCommand.Parameters.Add("@IsCurrent", SqlDbType.Bit).Value = firstApproveStep == step.SortNo;
                await taskCommand.ExecuteNonQueryAsync(token);
            }
        }

        if (firstApproveStep is null)
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "FLOW_NO_APPROVE_POWER",
                "流程步骤没有具备审批权（PERSON_APP_POWER）的人员，已拒绝启动。");

        // 提交送审日志（APPROVE_STATE='A'：发起人送审，MYTASK_ID=0 表示非任务级动作）
        await using (var submitLog = new SqlCommand($"""
            INSERT INTO dbo.WF_MYTASK_LOG (WF_ID, MYTASK_ID, SUBFLOW_NO, SUBFLOW_DESC, APP_EMP_ID,
                APPROVE_DATE, APPROVE_STATE, APPROVE_MSG)
            VALUES (@WfId, 0, '000', N'提交送审', @UserId, GETDATE(), '{WorkflowStates.Submitted}', @Msg);
            """, connection, transaction))
        {
            submitLog.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
            submitLog.Parameters.Add("@UserId", SqlDbType.VarChar, 20).Value = userId;
            submitLog.Parameters.Add("@Msg", SqlDbType.VarChar, 3000).Value =
                (object?)(string.IsNullOrWhiteSpace(submitMessage) ? null : submitMessage) ?? DBNull.Value;
            await submitLog.ExecuteNonQueryAsync(token);
        }
        await auditWriter.WriteEventAsync(connection, transaction, definition.ModuleId,
            string.Join(',', keyValues), "FLOW_SUBMIT",
            string.IsNullOrWhiteSpace(submitMessage) ? "送审（流程启动）" : $"送审（流程启动）：{submitMessage}",
            userId, "WORKBENCH_RECORD", 1, null, token);

        await transaction.CommitAsync(token);
        logger.LogInformation("流程启动 module={ModuleId} key={Key} steps={Steps} firstApprove={First}",
            definition.ModuleId, string.Join(',', keyValues), steps.Count, firstApproveStep);
        // 自动执行：自动执行——当前自动执行任务由 SYSTEM 自动同意（可连跳）
        if (steps.Any(step => step.IsAutoExec))
            await RunAutoExecLoopAsync(wfId, token);
        return RecordSaveResult.SuccessFlowStarted(keyValues);
    }

    /// <summary>
    /// 任务审批（approveState='Y'/'N'，可选 jumpNo）。
    /// 同意：顺序步整步通过；会签步（IS_SIGN=1）按 PASS_PERCENT 计数阈值 + MUST_SIGNER 推进；
    /// 非空 jumpNo 且具备 FORWARD_POWER 时向前跳（'0'=直接结束，中间未批任务标记跳过）；
    /// 驳回：解批前置校验（NOT_BACK_FIELDS）后重置 [jumpNo, 当前] 区间任务并退回目标步（空=第一步）；
    /// 末步/跳转结束：落主表 CONFIRM_TAG + WorkflowSproc 副作用 + WF_APPROVE 历史。
    /// </summary>
    public async Task<(bool Success, string? ErrorCode, string? ErrorMessage, bool FlowFinished, string? Message)> ApproveTaskAsync(
        long myTaskId,
        string userId,
        char approveState,
        string? message,
        string? jumpNo,
        CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);

        long wfId;
        string subflowNo;
        bool isSign;
        bool approvePower;
        bool forwardPower;
        bool hasNext;
        bool finishedDirect = false;
        string subflowDesc;
        string? currentState;
        int passPercent;
        // 第一步：任务审批（更新任务 + 日志 + 推进下一步），先提交释放锁
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
        {
            string approver;
            bool canSirAgency;
            await using (var taskCommand = new SqlCommand("""
                SELECT WF_ID, SUBFLOW_NO, LTRIM(RTRIM(ISNULL(APPROVER,''))), ISNULL(CAN_SIR_AGENCY,0),
                       LTRIM(RTRIM(ISNULL(APPROVE_STATE,''))), LTRIM(RTRIM(ISNULL(SUBFLOW_DESC,''))),
                       ISNULL(APPROVE_POWER,0), ISNULL(FORWARD_POWER,0), ISNULL(IS_SIGN,0),
                       ISNULL(PASS_PERCENT,0)
                FROM dbo.WF_MYTASK WITH (UPDLOCK, HOLDLOCK) WHERE MYTASK_ID=@MyTaskId;
                """, connection, transaction))
            {
                taskCommand.Parameters.Add("@MyTaskId", SqlDbType.BigInt).Value = myTaskId;
                await using var reader = await taskCommand.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                    return (false, "TASK_NOT_FOUND", "审批任务不存在。", false, null);
                wfId = reader.GetInt64(0);
                subflowNo = reader.GetString(1).Trim();
                approver = reader.GetString(2);
                canSirAgency = reader.GetBoolean(3);
                currentState = reader.GetString(4);
                subflowDesc = reader.GetString(5);
                approvePower = reader.GetBoolean(6);
                forwardPower = reader.GetBoolean(7);
                isSign = reader.GetBoolean(8);
                passPercent = reader.GetInt32(9);
            }

            if (!string.IsNullOrEmpty(currentState))
                return (false, "TASK_ALREADY_PROCESSED", "该审批任务已处理，不能重复审批。", false, null);

            var authorized = approver.Equals(userId, StringComparison.OrdinalIgnoreCase) || userId == "SYSTEM";
            if (!authorized && canSirAgency)
            {
                await using var agencyCommand = new SqlCommand("""
                    SELECT TOP 1 1 FROM dbo.f_get_sirs(@Approver) WHERE LTRIM(RTRIM(user_id))=@UserId;
                    """, connection, transaction);
                agencyCommand.Parameters.Add("@Approver", SqlDbType.VarChar, 20).Value = approver;
                agencyCommand.Parameters.Add("@UserId", SqlDbType.VarChar, 20).Value = userId;
                authorized = await agencyCommand.ExecuteScalarAsync(token) is not null;
            }
            if (!authorized)
                return (false, "TASK_NOT_AUTHORIZED", "当前用户不是该审批步骤的审批人，无权处理。", false, null);

            var now = DateTime.Now;
            var effectiveJump = string.IsNullOrWhiteSpace(jumpNo) ? null : jumpNo.Trim();

            if (approveState == WorkflowStates.Approved)
            {
                if (!approvePower)
                    return (false, "NO_APPROVE_POWER", "您没有该步骤的审批权，不能同意。", false, null);

                // 同意 + 跳转：需解批权；目标必须晚于当前步骤（'0'=直接结束）
                if (!string.IsNullOrEmpty(effectiveJump) && effectiveJump != "0")
                {
                    if (!forwardPower)
                        return (false, "NO_FORWARD_POWER", "您没有该步骤的跳转/解批权，不能跳转。", false, null);
                    if (string.CompareOrdinal(effectiveJump, subflowNo) <= 0)
                        return (false, "INVALID_JUMP_TARGET", "跳转目标步骤必须晚于当前步骤（'0' 表示直接结束）。", false, null);
                    if (!await StepHasTasksAsync(connection, transaction, wfId, effectiveJump, token))
                        return (false, "INVALID_JUMP_TARGET", $"跳转目标步骤 {effectiveJump} 不存在待处理任务。", false, null);
                }
                else if (effectiveJump == "0")
                {
                    if (!forwardPower)
                        return (false, "NO_FORWARD_POWER", "您没有该步骤的跳转/解批权，不能直接结束流程。", false, null);
                    finishedDirect = true;
                }

                if (isSign)
                {
                    // 会签步：仅本任务通过
                    await MarkTaskApprovedAsync(connection, transaction, myTaskId, userId, message, now, token);
                    await LogApproveAsync(connection, transaction, wfId, myTaskId, subflowNo, subflowDesc,
                        userId, now, 'Y', message, token);
                }
                else
                {
                    // 顺序步：整步标记通过（其余候选审批人同步通过）
                    await MarkStepApprovedAsync(connection, transaction, wfId, subflowNo, userId, message, now, token);
                    await LogApproveAsync(connection, transaction, wfId, myTaskId, subflowNo, subflowDesc,
                        userId, now, 'Y', message, token);
                }

                // 跳转/直接结束：中间未批任务标记跳过（'S'）并写日志
                if (finishedDirect)
                {
                    await SkipTasksAsync(connection, transaction, wfId, subflowNo, null, false, now, "---跳转跳过---", token);
                    hasNext = false;
                }
                else if (!string.IsNullOrEmpty(effectiveJump))
                {
                    await SkipTasksAsync(connection, transaction, wfId, subflowNo, effectiveJump, false, now, "---跳转跳过---", token);
                    await SetCurrentStepAsync(connection, transaction, wfId, effectiveJump, token);
                    hasNext = true;
                }
                else if (isSign)
                {
                    // 会签：计数阈值 + 无未批必签者才整步通过并推进
                    if (!await SignStepPassedAsync(connection, transaction, wfId, subflowNo, passPercent, token))
                    {
                        await transaction.CommitAsync(token);
                        return (true, null, null, false, "会签已记录，等待其余审批人达到通过阈值后整步通过。");
                    }
                    await SkipTasksAsync(connection, transaction, wfId, subflowNo, subflowNo, true, now,
                        "---执行会签条件跳过---", token);
                    hasNext = await SetNextStepAsync(connection, transaction, wfId, subflowNo, token);
                }
                else
                {
                    hasNext = await SetNextStepAsync(connection, transaction, wfId, subflowNo, token);
                }
            }
            else // 驳回
            {
                if (!approvePower)
                    return (false, "NO_APPROVE_POWER", "您没有该步骤的审批权，不能驳回。", false, null);

                // 退回目标：jumpNo 非空且非 '0' 时为目标步；空/'0'=第一步
                string? target;
                if (string.IsNullOrEmpty(effectiveJump) || effectiveJump == "0")
                {
                    target = await GetFirstStepNoAsync(connection, transaction, wfId, token);
                }
                else
                {
                    target = effectiveJump;
                }
                if (target is null)
                    return (false, "INVALID_JUMP_TARGET", "流程没有可退回的步骤。", false, null);
                if (string.CompareOrdinal(target, subflowNo) > 0)
                    return (false, "INVALID_JUMP_TARGET", "驳回退回目标步骤必须不晚于当前步骤。", false, null);

                // 解批前置校验
                var noBack = await CheckNotBackFieldsAsync(connection, transaction, wfId, token);
                if (noBack is not null)
                    return (false, noBack.Value.ErrorCode, noBack.Value.ErrorMessage, false, null);

                // 重置 [target, 当前] 区间任务为未处理并退回目标步
                await ResetStepRangeAsync(connection, transaction, wfId, target, subflowNo, token);
                await LogApproveAsync(connection, transaction, wfId, myTaskId, subflowNo, subflowDesc,
                    userId, now, 'N', message, token);
                await SetCurrentStepAsync(connection, transaction, wfId, target, token);
                await transaction.CommitAsync(token);
                return (true, null, null, false, $"已驳回，流程退回至步骤 {target}。");
            }

            await transaction.CommitAsync(token);
        }

        if (finishedDirect || !hasNext)
            return await CompleteFlowAsync(connection, wfId, userId, token);
        await RunAutoExecLoopAsync(wfId, token);
        return (true, null, null, false, "审批通过，流程进入下一步。");
    }

    /// <summary>
    /// 流程完成（末步/跳转结束）：主表确认 + WorkflowSproc 副作用 + WF_APPROVE 历史。
    /// 任务锁已释放；legacy SP 仍走独立连接（既有行为），效果引擎开启时
    /// 动作链与状态更新在同一事务内执行。
    /// </summary>
    private async Task<(bool Success, string? ErrorCode, string? ErrorMessage, bool FlowFinished, string? Message)> CompleteFlowAsync(
        SqlConnection connection, long wfId, string userId, CancellationToken token)
    {
        int moduleId;
        string masterTable;
        string keyCondition;
        string? updateSproc;
        string? title;
        WorkbenchDefinition? baseline = null;
        IReadOnlyList<string>? engineKeyValues = null;
        await using (var monitorCommand = new SqlCommand("""
            SELECT WF_M_IDX, KEY_VALUE
            FROM dbo.WF_MONITOR WITH (NOLOCK) WHERE WF_ID=@WfId;
            """, connection))
        {
            monitorCommand.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
            await using var reader = await monitorCommand.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
                return (false, "FLOW_NOT_FOUND", "流程实例不存在。", false, null);
            moduleId = reader.GetInt32(0);
            keyCondition = reader.GetString(1);
        }
        await using (var moduleCommand = new SqlCommand("""
            SELECT LTRIM(RTRIM(ISNULL(MASTER_TABLE,''))), LTRIM(RTRIM(ISNULL(UPDATE_SP,''))),
                   LTRIM(RTRIM(ISNULL(M_DESC,'')))
            FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId;
            """, connection))
        {
            moduleCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await using var reader = await moduleCommand.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
                return (false, "MODULE_NOT_FOUND", "模块不存在。", false, null);
            masterTable = reader.GetString(0);
            updateSproc = reader.IsDBNull(1) ? null : reader.GetString(1);
            title = reader.GetString(2);
        }

        var hasBaseline = definitionProvider.TryGetBaseline(moduleId, out baseline, out _);
        var engineEnabled = hasBaseline && baseline is not null && effectEngine.IsEnabledFor(baseline);
        if (engineEnabled)
        {
            engineKeyValues = ParseKeyValues(keyCondition);
            if (engineKeyValues.Count == 0)
                return (false, "WORKFLOW_FAILED", "主键值无法从流程实例解析，效果引擎拒绝执行。", false, null);
        }
        else if (!string.IsNullOrWhiteSpace(updateSproc))
        {
            var sprocResult = await controlledSprocs.RunWorkflowAsync(
                moduleId, updateSproc, Array.Empty<string>(), Array.Empty<string>(), true, token, keyCondition);
            if (!sprocResult.Success)
                return (false, "WORKFLOW_FAILED", sprocResult.Message ?? "末步业务处理失败。", false, null);
        }

        await using var finalTransaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var state = new SqlCommand(
                $"UPDATE dbo.WF_MONITOR SET WF_STATE='{WorkflowStates.MonitorCompleted}' WHERE WF_ID=@WfId;", connection, finalTransaction))
            {
                state.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
                await state.ExecuteNonQueryAsync(token);
            }
            await using var confirm = new SqlCommand(
                $"UPDATE dbo.[{masterTable}] SET CONFIRM_PERSON=@Person, CONFIRM_DATE=GETDATE(), CONFIRM_TAG=1 " +
                $"WHERE ISNULL(CONFIRM_TAG,0)=0 AND {keyCondition};", connection, finalTransaction);
            confirm.Parameters.Add("@Person", SqlDbType.NVarChar, 50).Value = userId;
            var affected = await confirm.ExecuteNonQueryAsync(token);
            if (affected == 0)
                return (false, "WORKFLOW_STATE_CONFLICT", "记录不存在或已批核，无法重复批核。", false, null);

            if (engineEnabled)
            {
                var (_, engineError) = await effectEngine.TryRunAsync(
                    connection, finalTransaction, baseline!, EffectEvent.ApproveEffect, engineKeyValues!, userId, token);
                if (engineError is not null)
                {
                    await finalTransaction.RollbackAsync(token);
                    return (false, "WORKFLOW_FAILED", engineError, false, null);
                }
            }

            await using var history = new SqlCommand("""
                INSERT INTO dbo.WF_APPROVE (M_IDX, KEY_VALUE, KEY_VALUE_DESC)
                VALUES (@ModuleId, @KeyValue, @KeyValueDesc);
                """, connection, finalTransaction);
            history.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            history.Parameters.Add("@KeyValue", SqlDbType.VarChar, 200).Value = keyCondition;
            history.Parameters.Add("@KeyValueDesc", SqlDbType.VarChar, 300).Value =
                (title ?? string.Empty) + " 流程审批完成";
            await history.ExecuteNonQueryAsync(token);
            await finalTransaction.CommitAsync(token);
        }
        catch
        {
            await finalTransaction.RollbackAsync(token);
            throw;
        }
        logger.LogInformation("流程末步通过 module={ModuleId} key={Key}", moduleId, keyCondition);
        return (true, null, null, true, "流程审批完成，单据已确认。");
    }

    /// <summary>
    /// 受控条件评估（EXEC_CONDITION/PERSON_CONDITION/AUTO_EXEC_CONDITION）：
    /// 空条件=true；经 DataFilterParser 按主表物理列白名单参数化解析，输出 EXISTS 判定；
    /// 解析失败返回 null（调用方必须拒绝启动，绝不拼接 SQL）。
    /// </summary>
    private static async Task<bool?> EvaluateConditionAsync(
        SqlConnection connection, SqlTransaction transaction, WorkbenchDefinition definition,
        string keyCondition, IReadOnlySet<string> masterColumns, string condition, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(condition)) return true;
        if (!DataFilterParser.TryParse(condition, definition.MasterTable, masterColumns, out var predicate, out var parameters))
            return null;
        await using var command = new SqlCommand(
            $"SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.[{definition.MasterTable}] WITH (NOLOCK) " +
            $"WHERE {keyCondition} AND ({predicate})) THEN 1 ELSE 0 END;", connection, transaction);
        for (var i = 0; i < parameters.Count; i++)
            command.Parameters.AddWithValue($"@df{i}", parameters[i] ?? DBNull.Value);
        var result = await command.ExecuteScalarAsync(token);
        return result is not null && Convert.ToInt32(result) == 1;
    }

    /// <summary>主表物理列白名单（sys.columns 目录视图，服务端权威），供条件评估使用。</summary>
    private static async Task<HashSet<string>> LoadMasterColumnsAsync(
        SqlConnection connection, SqlTransaction transaction, string masterTable, CancellationToken token)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand(
            "SELECT c.name FROM sys.columns c JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V') JOIN sys.schemas s ON o.schema_id=s.schema_id WHERE s.name=N'dbo' AND o.name=@Table ORDER BY c.column_id;",
            connection, transaction);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = masterTable;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) columns.Add(reader.GetString(0));
        return columns;
    }

    /// <summary>按人权限判定：空权限串=全部有权限。</summary>
    private static bool HasPower(string user, IReadOnlyList<string> powerList)
        => powerList.Count == 0 || powerList.Contains(user, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 将 WF_MONITOR.KEY_VALUE（BuildKeyCondition 产物，格式 [PK]='v' AND ...，主键序，单引号已转义）
    /// 解析回主键值数组（顺序即模块主键序），供前端拼装 /workbench/{m}/view/{keys} 记录浏览链接。
    /// 解析失败返回空数组（调用方降级为不显示浏览入口，不影响审批）。
    /// </summary>
    public static IReadOnlyList<string> ParseKeyValues(string keyValue)
    {
        if (string.IsNullOrWhiteSpace(keyValue))
            return Array.Empty<string>();
        var matches = Regex.Matches(keyValue, @"\[([^\]]+)\]='([^']*)'");
        var values = new List<string>(matches.Count);
        foreach (Match match in matches)
        {
            if (match.Groups.Count < 3)
                continue;
            values.Add(match.Groups[2].Value.Replace("''", "'"));
        }
        return values;
    }

    private static async Task MarkTaskApprovedAsync(
        SqlConnection connection, SqlTransaction transaction, long myTaskId, string userId,
        string? message, DateTime now, CancellationToken token)
    {
        await using var update = new SqlCommand($"""
            UPDATE dbo.WF_MYTASK SET APP_EMP_ID=@UserId, APPROVE_MSG=@Msg, APPROVE_TAG=1,
                APPROVE_DATE=@Now, APPROVE_STATE='{WorkflowStates.Approved}'
            WHERE MYTASK_ID=@MyTaskId AND ISNULL(APPROVE_STATE,'')='';
            """, connection, transaction);
        update.Parameters.Add("@UserId", SqlDbType.VarChar, 20).Value = userId;
        update.Parameters.Add("@Msg", SqlDbType.VarChar, 3000).Value = (object?)message ?? DBNull.Value;
        update.Parameters.Add("@Now", SqlDbType.DateTime).Value = now;
        update.Parameters.Add("@MyTaskId", SqlDbType.BigInt).Value = myTaskId;
        await update.ExecuteNonQueryAsync(token);
    }

    private static async Task MarkStepApprovedAsync(
        SqlConnection connection, SqlTransaction transaction, long wfId, string subflowNo, string userId,
        string? message, DateTime now, CancellationToken token)
    {
        await using var update = new SqlCommand($"""
            UPDATE dbo.WF_MYTASK SET APP_EMP_ID=@UserId, APPROVE_MSG=@Msg, APPROVE_TAG=1,
                APPROVE_DATE=@Now, APPROVE_STATE='{WorkflowStates.Approved}'
            WHERE WF_ID=@WfId AND SUBFLOW_NO=@SubflowNo AND ISNULL(APPROVE_STATE,'')='';
            """, connection, transaction);
        update.Parameters.Add("@UserId", SqlDbType.VarChar, 20).Value = userId;
        update.Parameters.Add("@Msg", SqlDbType.VarChar, 3000).Value = (object?)message ?? DBNull.Value;
        update.Parameters.Add("@Now", SqlDbType.DateTime).Value = now;
        update.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
        update.Parameters.Add("@SubflowNo", SqlDbType.Char, 3).Value = subflowNo;
        await update.ExecuteNonQueryAsync(token);
    }

    private static async Task LogApproveAsync(
        SqlConnection connection, SqlTransaction transaction, long wfId, long myTaskId,
        string subflowNo, string subflowDesc, string userId, DateTime now, char state,
        string? message, CancellationToken token)
    {
        await using var log = new SqlCommand("""
            INSERT INTO dbo.WF_MYTASK_LOG (WF_ID, MYTASK_ID, SUBFLOW_NO, SUBFLOW_DESC, APP_EMP_ID,
                APPROVE_DATE, APPROVE_STATE, APPROVE_MSG)
            VALUES (@WfId, @MyTaskId, @SubflowNo, @SubflowDesc, @UserId, @Now, @State, @Msg);
            """, connection, transaction);
        log.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
        log.Parameters.Add("@MyTaskId", SqlDbType.BigInt).Value = myTaskId;
        log.Parameters.Add("@SubflowNo", SqlDbType.Char, 3).Value = subflowNo;
        log.Parameters.Add("@SubflowDesc", SqlDbType.VarChar, 50).Value = subflowDesc;
        log.Parameters.Add("@UserId", SqlDbType.VarChar, 20).Value = userId;
        log.Parameters.Add("@Now", SqlDbType.DateTime).Value = now;
        log.Parameters.Add("@State", SqlDbType.Char, 1).Value = state.ToString();
        log.Parameters.Add("@Msg", SqlDbType.VarChar, 3000).Value = (object?)message ?? DBNull.Value;
        await log.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// 会签整步通过判定：已通过任务数 ≥ PASS_PERCENT（计数阈值，非数学百分比），
    /// 且不存在未批的必签者（IS_MUST_SIGN=1）。
    /// </summary>
    private static async Task<bool> SignStepPassedAsync(
        SqlConnection connection, SqlTransaction transaction, long wfId, string stepNo,
        int passPercent, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT CASE WHEN @PassPercent <= (SELECT COUNT_BIG(1) FROM dbo.WF_MYTASK
                    WHERE WF_ID=@WfId AND SUBFLOW_NO=@StepNo AND APPROVE_TAG=1)
                AND NOT EXISTS (SELECT 1 FROM dbo.WF_MYTASK
                    WHERE WF_ID=@WfId AND SUBFLOW_NO=@StepNo AND APPROVE_TAG=0 AND IS_MUST_SIGN=1)
                THEN 1 ELSE 0 END;
            """, connection, transaction);
        command.Parameters.Add("@PassPercent", SqlDbType.Int).Value = passPercent;
        command.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
        command.Parameters.Add("@StepNo", SqlDbType.Char, 3).Value = stepNo;
        var result = await command.ExecuteScalarAsync(token);
        return result is not null && Convert.ToInt32(result) == 1;
    }

    /// <summary>
    /// 将 [fromStep, toStep]（toStepExclusive=true 时不含 toStep；toStep=null 到流程结束）
    /// 区间内未批任务标记为跳过（APPROVE_STATE='S'）并写审批日志。
    /// </summary>
    private async Task SkipTasksAsync(
        SqlConnection connection, SqlTransaction transaction, long wfId,
        string? fromStep, string? toStep, bool toStepExclusive, DateTime now,
        string skipMessage, CancellationToken token)
    {
        var range = (fromStep is null ? string.Empty : "SUBFLOW_NO>=@FromStep AND ")
                  + (toStep is null ? string.Empty : toStepExclusive ? "SUBFLOW_NO<=@ToStep AND " : "SUBFLOW_NO<@ToStep AND ");
        if (range.Length == 0)
            range = "1=1 AND ";
        var where = $"WF_ID=@WfId AND {range}ISNULL(APPROVE_STATE,'')=''";
        await using var update = new SqlCommand($"""
            UPDATE dbo.WF_MYTASK SET APP_EMP_ID='', APPROVE_MSG=@SkipMsg, APPROVE_TAG=1,
                APPROVE_DATE=@Now, APPROVE_STATE='{WorkflowStates.Skipped}'
            WHERE {where};
            """, connection, transaction);
        update.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
        if (fromStep is not null) update.Parameters.Add("@FromStep", SqlDbType.Char, 3).Value = fromStep;
        if (toStep is not null) update.Parameters.Add("@ToStep", SqlDbType.Char, 3).Value = toStep;
        update.Parameters.Add("@SkipMsg", SqlDbType.VarChar, 3000).Value = skipMessage;
        update.Parameters.Add("@Now", SqlDbType.DateTime).Value = now;
        var affected = await update.ExecuteNonQueryAsync(token);
        if (affected <= 0)
            return;

        var logWhere = $"WF_ID=@WfId AND {range}APPROVE_STATE='{WorkflowStates.Skipped}' AND APPROVE_MSG=@SkipMsg";
        await using var log = new SqlCommand($"""
            INSERT INTO dbo.WF_MYTASK_LOG (WF_ID, MYTASK_ID, SUBFLOW_NO, SUBFLOW_DESC, APP_EMP_ID,
                APPROVE_DATE, APPROVE_STATE, APPROVE_MSG)
            SELECT @WfId, MYTASK_ID, SUBFLOW_NO, SUBFLOW_DESC, '', @Now, '{WorkflowStates.Skipped}', @SkipMsg
            FROM dbo.WF_MYTASK WITH (NOLOCK)
            WHERE {logWhere};
            """, connection, transaction);
        log.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
        if (fromStep is not null) log.Parameters.Add("@FromStep", SqlDbType.Char, 3).Value = fromStep;
        if (toStep is not null) log.Parameters.Add("@ToStep", SqlDbType.Char, 3).Value = toStep;
        log.Parameters.Add("@SkipMsg", SqlDbType.VarChar, 3000).Value = skipMessage;
        log.Parameters.Add("@Now", SqlDbType.DateTime).Value = now;
        await log.ExecuteNonQueryAsync(token);
    }

    /// <summary>推进到下一个具备审批权且未处理的步骤（无则返回 false=流程完成）。</summary>
    private static async Task<bool> SetNextStepAsync(
        SqlConnection connection, SqlTransaction transaction, long wfId, string afterStep, CancellationToken token)
    {
        await using var nextCommand = new SqlCommand("""
            SELECT TOP 1 SUBFLOW_NO FROM dbo.WF_MYTASK WITH (NOLOCK)
            WHERE WF_ID=@WfId AND APPROVE_POWER=1 AND ISNULL(APPROVE_STATE,'')=''
              AND SUBFLOW_NO>@AfterStep
            ORDER BY SUBFLOW_NO;
            """, connection, transaction);
        nextCommand.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
        nextCommand.Parameters.Add("@AfterStep", SqlDbType.Char, 3).Value = afterStep;
        var next = await nextCommand.ExecuteScalarAsync(token) as string;
        if (string.IsNullOrWhiteSpace(next))
            return false;
        await SetCurrentStepAsync(connection, transaction, wfId, next.Trim(), token);
        return true;
    }

    /// <summary>整流程当前步切换：清空后置目标步骤（该步骤全部任务）为当前。</summary>
    private static async Task SetCurrentStepAsync(
        SqlConnection connection, SqlTransaction transaction, long wfId, string stepNo, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            UPDATE dbo.WF_MYTASK SET IS_CURRENT=0 WHERE WF_ID=@WfId;
            UPDATE dbo.WF_MYTASK SET IS_CURRENT=1 WHERE WF_ID=@WfId AND SUBFLOW_NO=@StepNo;
            """, connection, transaction);
        command.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
        command.Parameters.Add("@StepNo", SqlDbType.Char, 3).Value = stepNo;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<bool> StepHasTasksAsync(
        SqlConnection connection, SqlTransaction transaction, long wfId, string stepNo, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT TOP 1 1 FROM dbo.WF_MYTASK WITH (NOLOCK) WHERE WF_ID=@WfId AND SUBFLOW_NO=@StepNo;
            """, connection, transaction);
        command.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
        command.Parameters.Add("@StepNo", SqlDbType.Char, 3).Value = stepNo;
        return await command.ExecuteScalarAsync(token) is not null;
    }

    /// <summary>流程第一步（最小 SUBFLOW_NO 的任务步骤），供驳回退回默认目标。</summary>
    private static async Task<string?> GetFirstStepNoAsync(
        SqlConnection connection, SqlTransaction transaction, long wfId, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT TOP 1 SUBFLOW_NO FROM dbo.WF_MYTASK WITH (NOLOCK)
            WHERE WF_ID=@WfId ORDER BY SUBFLOW_NO;
            """, connection, transaction);
        command.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
        var result = await command.ExecuteScalarAsync(token) as string;
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    /// <summary>重置 [target, current] 区间任务为未处理（驳回退回）。</summary>
    private static async Task ResetStepRangeAsync(
        SqlConnection connection, SqlTransaction transaction, long wfId,
        string target, string current, CancellationToken token)
    {
        await using var update = new SqlCommand("""
            UPDATE dbo.WF_MYTASK SET APP_EMP_ID='', APPROVE_MSG='', APPROVE_TAG=0,
                APPROVE_DATE=NULL, APPROVE_STATE=''
            WHERE WF_ID=@WfId AND SUBFLOW_NO>=@Target AND SUBFLOW_NO<=@Current;
            """, connection, transaction);
        update.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
        update.Parameters.Add("@Target", SqlDbType.Char, 3).Value = target;
        update.Parameters.Add("@Current", SqlDbType.Char, 3).Value = current;
        await update.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// 驳回前置校验：
    /// MODULES.NOT_BACK_FIELDS(_M) 配置字段存在"已发生业务"值（CAST(字段 AS varchar(100))>'0'）时禁止驳回退回。
    /// </summary>
    private async Task<(string ErrorCode, string ErrorMessage)?> CheckNotBackFieldsAsync(
        SqlConnection connection, SqlTransaction transaction, long wfId, CancellationToken token)
    {
        int moduleId;
        string keyCondition;
        await using (var monitorCommand = new SqlCommand("""
            SELECT WF_M_IDX, KEY_VALUE FROM dbo.WF_MONITOR WITH (NOLOCK) WHERE WF_ID=@WfId;
            """, connection, transaction))
        {
            monitorCommand.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
            await using var reader = await monitorCommand.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
                return null;
            moduleId = reader.GetInt32(0);
            keyCondition = reader.GetString(1);
        }

        string masterTable, detailTable, masterFields, detailFields;
        await using (var moduleCommand = new SqlCommand("""
            SELECT LTRIM(RTRIM(ISNULL(MASTER_TABLE,''))), LTRIM(RTRIM(ISNULL(DETAIL_TABLE,''))),
                   LTRIM(RTRIM(ISNULL(NOT_BACK_FIELDS_M,''))), LTRIM(RTRIM(ISNULL(NOT_BACK_FIELDS,'')))
            FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId;
            """, connection, transaction))
        {
            moduleCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await using var reader = await moduleCommand.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
                return null;
            masterTable = reader.GetString(0);
            detailTable = reader.GetString(1);
            masterFields = reader.GetString(2);
            detailFields = reader.GetString(3);
        }

        var blocked = new List<string>();
        if (!string.IsNullOrWhiteSpace(masterFields) && !string.IsNullOrWhiteSpace(masterTable))
            await CheckNotBackTableAsync(connection, transaction, moduleId, masterTable, masterFields,
                keyCondition, blocked, token);
        if (!string.IsNullOrWhiteSpace(detailFields) && !string.IsNullOrWhiteSpace(detailTable))
            await CheckNotBackTableAsync(connection, transaction, moduleId, detailTable, detailFields,
                keyCondition, blocked, token);

        if (blocked.Count == 0)
            return null;
        return ("NOBACK_BLOCKED",
            $"单据存在已发生的业务数据（{string.Join("、", blocked)}），不能驳回退回。");
    }

    private async Task CheckNotBackTableAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId, string table,
        string fieldsCsv, string keyCondition, List<string> blocked, CancellationToken token)
    {
        var fields = fieldsCsv.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(field => WorkbenchSql.Identifier.IsMatch(field)).ToArray();
        if (fields.Length == 0)
            return;

        // 物理列存在性校验（服务端白名单）：只保留真实存在的列
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var columnCommand = new SqlCommand(
            "SELECT c.name FROM sys.columns c JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V') JOIN sys.schemas s ON o.schema_id=s.schema_id WHERE s.name=N'dbo' AND o.name=@Table ORDER BY c.column_id;",
            connection, transaction))
        {
            columnCommand.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
            await using var reader = await columnCommand.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) existing.Add(reader.GetString(0));
        }
        var valid = fields.Where(field => existing.Contains(field)).ToArray();
        foreach (var missing in fields.Where(field => !existing.Contains(field)))
            logger.LogWarning("驳回前置校验字段物理不存在，已跳过 module={ModuleId} table={Table} field={Field}",
                moduleId, table, missing);
        if (valid.Length == 0)
            return;

        // 字段描述（FIELDS.F_DESC）用于业务提示
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inClause = string.Join(",", valid.Select((_, index) => $"@f{index}"));
        await using (var descCommand = new SqlCommand(
            $"SELECT LTRIM(RTRIM(F_ID)),LTRIM(RTRIM(ISNULL(F_DESC,''))) FROM dbo.FIELDS WITH (NOLOCK) " +
            $"WHERE LTRIM(RTRIM(T_ID))=@Table AND F_ID IN ({inClause});", connection, transaction))
        {
            descCommand.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
            for (var i = 0; i < valid.Length; i++)
                descCommand.Parameters.Add($"@f{i}", SqlDbType.NVarChar, 100).Value = valid[i];
            await using var reader = await descCommand.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) labels[reader.GetString(0)] = reader.GetString(1);
        }

        var predicates = string.Join(" OR ", valid.Select(field => $"CAST([{field}] AS varchar(100))>'0'"));
        await using var checkCommand = new SqlCommand(
            $"SELECT COUNT_BIG(1) FROM dbo.[{table}] WITH (NOLOCK) WHERE {keyCondition} AND ({predicates});",
            connection, transaction);
        var count = Convert.ToInt64(await checkCommand.ExecuteScalarAsync(token));
        if (count > 0)
            blocked.AddRange(valid.Select(field => labels.TryGetValue(field, out var label) && label.Length > 0 ? label : field));
    }

    /// <summary>
    /// 自动执行：当前自动执行任务由 SYSTEM 自动同意，循环直至无自动任务（可连跳多步）。
    /// </summary>
    private async Task RunAutoExecLoopAsync(long wfId, CancellationToken token)
    {
        while (true)
        {
            long? autoTaskId;
            await using (var connection = connections.Create())
            {
                await connection.OpenAsync(token);
                await using var command = new SqlCommand("""
                    SELECT TOP 1 MYTASK_ID FROM dbo.WF_MYTASK WITH (NOLOCK)
                    WHERE WF_ID=@WfId AND IS_CURRENT=1 AND IS_AUTO_EXEC=1 AND ISNULL(APPROVE_STATE,'')='';
                    """, connection);
                command.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
                autoTaskId = await command.ExecuteScalarAsync(token) as long?;
            }
            if (autoTaskId is null)
                return;
            var result = await ApproveTaskAsync(autoTaskId.Value, "SYSTEM", 'Y', "系统自动执行", null, token);
            if (!result.Success)
            {
                logger.LogWarning("自动执行步骤失败 wf={WfId} task={TaskId} code={Code} message={Message}",
                    wfId, autoTaskId.Value, result.ErrorCode, result.ErrorMessage);
                return;
            }
        }
    }

    /// <summary>按单审批历史时间线（WF_MYTASK_LOG + WF_APPROVE）。</summary>
    public async Task<IReadOnlyList<object>> GetHistoryAsync(
        SqlConnection connection,
        int moduleId,
        IReadOnlyList<string> pkColumns,
        IReadOnlyList<string> keyValues,
        CancellationToken token)
    {
        var keyCondition = ControlledSprocInvoker.BuildKeyCondition(pkColumns, keyValues);
        var result = new List<object>();
        await using var command = new SqlCommand("""
            SELECT L.SUBFLOW_NO, L.SUBFLOW_DESC, L.APP_EMP_ID, L.APPROVE_STATE, L.APPROVE_MSG,
                   CONVERT(varchar(19), L.APPROVE_DATE, 120) AS APPROVE_DATE
            FROM dbo.WF_MYTASK_LOG L
            JOIN dbo.WF_MONITOR M ON M.WF_ID=L.WF_ID
            WHERE M.WF_M_IDX=@ModuleId AND M.KEY_VALUE=@KeyValue
            ORDER BY L.APPROVE_DATE;
            """, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@KeyValue", SqlDbType.VarChar, 200).Value = keyCondition;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(new
            {
                Kind = "task",
                Step = reader.GetString(0).Trim(),
                StepDesc = reader.GetString(1),
                Approver = reader.IsDBNull(2) ? null : reader.GetString(2),
                State = reader.GetString(3).Trim(),
                Message = reader.IsDBNull(4) ? null : reader.GetString(4),
                Date = reader.GetString(5),
            });
        await reader.DisposeAsync();
        await using var confirmCommand = new SqlCommand("""
            SELECT KEY_VALUE_DESC, LAST_UPDATE_BY
            FROM dbo.WF_APPROVE WHERE M_IDX=@ModuleId AND KEY_VALUE=@KeyValue;
            """, connection);
        confirmCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        confirmCommand.Parameters.Add("@KeyValue", SqlDbType.VarChar, 200).Value = keyCondition;
        await using var confirmReader = await confirmCommand.ExecuteReaderAsync(token);
        while (await confirmReader.ReadAsync(token))
            result.Add(new
            {
                Kind = "confirm",
                Step = "",
                StepDesc = confirmReader.IsDBNull(0) ? null : confirmReader.GetString(0),
                Approver = confirmReader.IsDBNull(1) ? null : confirmReader.GetString(1),
                State = "Y",
                Message = "流程审批完成，单据已确认",
                Date = "",
            });
        return result;
    }

    /// <summary>当前用户真实待办（WF_MYTASK 未处理任务），含跳转候选步骤与权限位。</summary>
    public async Task<IReadOnlyList<object>> GetMyFlowTasksAsync(
        SqlConnection connection,
        string userId,
        CancellationToken token)
    {
        var rows = new List<(long MyTaskId, long WfId, string Step, string StepDesc, int ModuleId,
            string KeyValue, string Title, bool ApprovePower, bool ForwardPower, bool IsSign, int PassPercent)>();
        await using var command = new SqlCommand("""
            SELECT T.MYTASK_ID, T.WF_ID, T.SUBFLOW_NO, T.SUBFLOW_DESC, M.WF_M_IDX, M.KEY_VALUE,
                   LTRIM(RTRIM(ISNULL((SELECT M_DESC FROM dbo.MODULES WHERE M_IDX=M.WF_M_IDX),''))),
                   ISNULL(T.APPROVE_POWER,0), ISNULL(T.FORWARD_POWER,0), ISNULL(T.IS_SIGN,0),
                   ISNULL(T.PASS_PERCENT,0)
            FROM dbo.WF_MYTASK T WITH (NOLOCK)
            JOIN dbo.WF_MONITOR M WITH (NOLOCK) ON M.WF_ID=T.WF_ID
            WHERE LTRIM(RTRIM(T.APPROVER))=@UserId AND ISNULL(T.APPROVE_STATE,'')=''
              AND ISNULL(T.APPROVE_TAG,0)=0
            ORDER BY T.MYTASK_ID;
            """, connection);
        command.Parameters.Add("@UserId", SqlDbType.VarChar, 20).Value = userId;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            rows.Add((
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2).Trim(),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetBoolean(7),
                reader.GetBoolean(8),
                reader.GetBoolean(9),
                reader.GetInt32(10)));
        await reader.DisposeAsync();

        // 同流程全部步骤（跳转候选，服务端按 WF_MYTASK 实际任务步骤返回）
        var stepsByWf = new Dictionary<long, List<object>>();
        foreach (var wfId in rows.Select(row => row.WfId).Distinct())
        {
            var stepList = new List<object>();
            await using var stepCommand = new SqlCommand("""
                SELECT DISTINCT SUBFLOW_NO, LTRIM(RTRIM(ISNULL(SUBFLOW_DESC,'')))
                FROM dbo.WF_MYTASK WITH (NOLOCK)
                WHERE WF_ID=@WfId ORDER BY SUBFLOW_NO;
                """, connection);
            stepCommand.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
            await using var stepReader = await stepCommand.ExecuteReaderAsync(token);
            while (await stepReader.ReadAsync(token))
                stepList.Add(new { Step = stepReader.GetString(0).Trim(), StepDesc = stepReader.GetString(1) });
            stepsByWf[wfId] = stepList;
        }

        var result = new List<object>();
        foreach (var row in rows)
        {
            result.Add(new
            {
                row.MyTaskId,
                row.WfId,
                row.Step,
                row.StepDesc,
                row.ModuleId,
                row.KeyValue,
                row.Title,
                row.ApprovePower,
                row.ForwardPower,
                row.IsSign,
                row.PassPercent,
                KeyValues = ParseKeyValues(row.KeyValue),
                Steps = stepsByWf.TryGetValue(row.WfId, out var steps)
                    ? (IReadOnlyList<object>)steps
                    : Array.Empty<object>(),
            });
        }
        return result;
    }

    /// <summary>
    /// 发起人撤回在途流程（v2.1）：仅发起人可在流程未完成（WF_STATE='0'）且单据未确认时
    /// 撤回；未处理任务标记 'W'（已撤回）并写日志，主表保持 CONFIRM_TAG=0（可编辑后重新提交）。
    /// </summary>
    public async Task<RecordSaveResult> WithdrawAsync(
        int moduleId,
        IReadOnlyList<string> keyValues,
        string userId,
        string employeeName,
        CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

        // 主表 + 主键列（服务端元数据，白名单），构建受控 keyCondition（参数化/转义，不信任前端条件串）
        string? masterTable;
        await using (var tableCommand = new SqlCommand(
            "SELECT LTRIM(RTRIM(ISNULL(MASTER_TABLE,''))) FROM dbo.MODULES WHERE M_IDX=@ModuleId;",
            connection, transaction))
        {
            tableCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            masterTable = await tableCommand.ExecuteScalarAsync(token) as string;
        }
        if (string.IsNullOrWhiteSpace(masterTable) || !WorkbenchSql.Identifier.IsMatch(masterTable))
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "INVALID_MASTER_TABLE",
                "模块主表无效。");
        var pkColumns = await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection, transaction, masterTable, token);
        if (pkColumns.Count == 0 || pkColumns.Count != keyValues.Count)
            return RecordSaveResult.Failed(RecordAccessStatus.KeyMismatch, "RECORD_KEY_MISMATCH",
                "主键数量与模块主键不匹配。");
        var keyCondition = ControlledSprocInvoker.BuildKeyCondition(pkColumns, keyValues);

        long wfId;
        string state;
        string? startUser;
        await using (var read = new SqlCommand("""
            SELECT WF_ID, LTRIM(RTRIM(ISNULL(WF_STATE,''))), LTRIM(RTRIM(ISNULL(START_USER,'')))
            FROM dbo.WF_MONITOR WITH (UPDLOCK, HOLDLOCK)
            WHERE WF_M_IDX=@ModuleId AND KEY_VALUE=@KeyValue;
            """, connection, transaction))
        {
            read.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            read.Parameters.Add("@KeyValue", SqlDbType.VarChar, 200).Value = keyCondition;
            await using var reader = await read.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
                return RecordSaveResult.Failed(RecordAccessStatus.NotFound, "FLOW_NOT_FOUND",
                    "流程实例不存在或尚未送审。");
            wfId = reader.GetInt64(0);
            state = reader.GetString(1);
            startUser = reader.GetString(2);
        }
        if (!string.Equals(state, "0", StringComparison.Ordinal))
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "FLOW_NOT_ACTIVE",
                state == "2" ? "该流程已撤回，请修改后重新提交。" : "该流程已完成，不能撤回。");
        if (!string.Equals(startUser, userId, StringComparison.OrdinalIgnoreCase))
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "NOT_FLOW_INITIATOR",
                "仅发起人可撤回该流程。");

        // 单据未最终确认（CONFIRM_TAG=0）才能撤回；keyCondition 为服务端 BuildKeyCondition 生成，安全
        await using (var confirm = new SqlCommand(
            $"SELECT TOP 1 ISNULL(CONFIRM_TAG,0) FROM dbo.[{masterTable}] WITH (NOLOCK) WHERE {keyCondition};",
            connection, transaction))
        {
            var tag = await confirm.ExecuteScalarAsync(token);
            if (tag is not null && Convert.ToInt32(tag) == 1)
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "FLOW_ALREADY_FINISHED",
                    "单据已确认，流程已结束，不能撤回。");
        }

        await using (var mark = new SqlCommand($"""
            UPDATE dbo.WF_MYTASK SET APPROVE_STATE='{WorkflowStates.WithdrawnTask}', IS_CURRENT=0, APPROVE_DATE=GETDATE(),
                APPROVE_MSG=@Msg
            WHERE WF_ID=@WfId AND ISNULL(APPROVE_STATE,'')='';
            """, connection, transaction))
        {
            mark.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
            mark.Parameters.Add("@Msg", SqlDbType.VarChar, 3000).Value = employeeName + " 撤回流程";
            await mark.ExecuteNonQueryAsync(token);
        }
        await using (var log = new SqlCommand($"""
            INSERT INTO dbo.WF_MYTASK_LOG (WF_ID, MYTASK_ID, SUBFLOW_NO, SUBFLOW_DESC, APP_EMP_ID,
                APPROVE_DATE, APPROVE_STATE, APPROVE_MSG)
            SELECT WF_ID, MYTASK_ID, SUBFLOW_NO, SUBFLOW_DESC, @UserId, GETDATE(), '{WorkflowStates.WithdrawnTask}', @Msg
            FROM dbo.WF_MYTASK WHERE WF_ID=@WfId AND APPROVE_STATE='{WorkflowStates.WithdrawnTask}';
            """, connection, transaction))
        {
            log.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
            log.Parameters.Add("@UserId", SqlDbType.VarChar, 20).Value = userId;
            log.Parameters.Add("@Msg", SqlDbType.VarChar, 3000).Value = employeeName + " 撤回流程";
            await log.ExecuteNonQueryAsync(token);
        }
        await using (var stateUpdate = new SqlCommand(
            $"UPDATE dbo.WF_MONITOR SET WF_STATE='{WorkflowStates.MonitorWithdrawn}' WHERE WF_ID=@WfId;", connection, transaction))
        {
            stateUpdate.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
            await stateUpdate.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
        logger.LogInformation("流程撤回 module={ModuleId} key={Key} user={User}", moduleId, string.Join(',', keyValues), userId);
        return RecordSaveResult.Success([]);
    }

    /// <summary>当前用户发起的在途流程（v2.1「我发起的」，撤回入口数据源）。</summary>
    public async Task<IReadOnlyList<object>> GetMyStartedAsync(
        SqlConnection connection,
        string userId,
        CancellationToken token)
    {
        var result = new List<object>();
        await using var command = new SqlCommand($"""
            SELECT M.WF_ID, M.WF_M_IDX,
                   LTRIM(RTRIM(ISNULL((SELECT M_DESC FROM dbo.MODULES WHERE M_IDX=M.WF_M_IDX),''))),
                   M.KEY_VALUE, M.KEY_VALUE_DESC, M.START_DATE,
                   LTRIM(RTRIM(ISNULL(T.SUBFLOW_NO,''))), LTRIM(RTRIM(ISNULL(T.SUBFLOW_DESC,'')))
            FROM dbo.WF_MONITOR M WITH (NOLOCK)
            LEFT JOIN dbo.WF_MYTASK T WITH (NOLOCK) ON T.WF_ID=M.WF_ID AND T.IS_CURRENT=1
            WHERE LTRIM(RTRIM(ISNULL(M.START_USER,''))) = @UserId AND M.WF_STATE='{WorkflowStates.MonitorInProgress}'
            ORDER BY M.START_DATE DESC;
            """, connection);
        command.Parameters.Add("@UserId", SqlDbType.VarChar, 20).Value = userId;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(new
            {
                WfId = reader.GetInt64(0),
                ModuleId = reader.GetInt32(1),
                Title = reader.GetString(2),
                KeyValue = reader.GetString(3),
                KeyValues = ParseKeyValues(reader.GetString(3)),
                KeyValueDesc = reader.GetString(4),
                StartDate = reader.IsDBNull(5) ? null : reader.GetValue(5),
                Step = reader.GetString(6),
                StepDesc = reader.GetString(7),
            });
        return result;
    }

    /// <summary>
    /// 流程监控列表（模块 2103）：全部流程实例（在途/完成/撤回），支持按状态/模块/关键字过滤。
    /// OverdueDays 为超时阈值（配置层）：在途天数超过阈值且仍在途 → Overdue=true。
    /// </summary>
    public async Task<IReadOnlyList<object>> GetMonitorAsync(
        SqlConnection connection, string status, int moduleId, string? keyword,
        int overdueDays, CancellationToken token)
    {
        var result = new List<object>();
        await using var command = new SqlCommand("""
            SELECT M.WF_ID, M.WF_M_IDX,
                   LTRIM(RTRIM(ISNULL((SELECT M_DESC FROM dbo.MODULES WHERE M_IDX=M.WF_M_IDX),''))),
                   LTRIM(RTRIM(ISNULL(M.KEY_VALUE_DESC,''))), LTRIM(RTRIM(ISNULL(M.KEY_VALUE,''))),
                   LTRIM(RTRIM(ISNULL(M.START_USER,''))), M.START_DATE,
                   LTRIM(RTRIM(ISNULL(T.SUBFLOW_NO,''))), LTRIM(RTRIM(ISNULL(T.SUBFLOW_DESC,''))),
                   LTRIM(RTRIM(ISNULL(M.WF_STATE,'')))
            FROM dbo.WF_MONITOR M WITH (NOLOCK)
            LEFT JOIN dbo.WF_MYTASK T WITH (NOLOCK) ON T.WF_ID=M.WF_ID AND T.IS_CURRENT=1
            WHERE (@Status='' OR LTRIM(RTRIM(ISNULL(M.WF_STATE,'')))=@Status)
              AND (@ModuleId=0 OR M.WF_M_IDX=@ModuleId)
              AND (@Kw='' OR M.KEY_VALUE_DESC LIKE @Like OR M.KEY_VALUE LIKE @Like OR M.START_USER LIKE @Like
                   OR EXISTS (SELECT 1 FROM dbo.MODULES MD WITH (NOLOCK) WHERE MD.M_IDX=M.WF_M_IDX AND MD.M_DESC LIKE @Like))
            ORDER BY M.START_DATE DESC;
            """, connection);
        command.Parameters.Add("@Status", SqlDbType.VarChar, 1).Value = status;
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@Kw", SqlDbType.NVarChar, 200).Value = keyword?.Trim() ?? string.Empty;
        command.Parameters.Add("@Like", SqlDbType.NVarChar, 210).Value = $"%{(keyword ?? string.Empty).Trim()}%";
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var startDate = reader.IsDBNull(6) ? (DateTime?)null : reader.GetDateTime(6);
            var ageDays = startDate is null ? (double?)null : Math.Max(0, (DateTime.Now - startDate.Value).TotalDays);
            var stateValue = reader.GetString(9);
            result.Add(new
            {
                WfId = reader.GetInt64(0),
                ModuleId = reader.GetInt32(1),
                Title = reader.GetString(2),
                KeyValueDesc = reader.GetString(3),
                KeyValue = reader.GetString(4),
                KeyValues = ParseKeyValues(reader.GetString(4)),
                StartUser = reader.GetString(5),
                StartDate = startDate,
                AgeDays = ageDays,
                Step = reader.GetString(7),
                StepDesc = reader.GetString(8),
                State = stateValue,
                StateLabel = stateValue switch
                {
                    "0" => "在途",
                    "1" => "已完成",
                    "2" => "已撤回",
                    _ => stateValue,
                },
                Overdue = stateValue == "0" && ageDays.HasValue && ageDays.Value > overdueDays,
            });
        }
        return result;
    }

    /// <summary>流程实例明细（模块 2103 监控详情）：monitor + 全部任务 + 审批日志时间线。</summary>
    public async Task<object?> GetMonitorDetailAsync(SqlConnection connection, long wfId, CancellationToken token)
    {
        object? monitor = null;
        await using (var monitorCommand = new SqlCommand("""
            SELECT M.WF_ID, M.WF_M_IDX,
                   LTRIM(RTRIM(ISNULL((SELECT M_DESC FROM dbo.MODULES WHERE M_IDX=M.WF_M_IDX),''))),
                   LTRIM(RTRIM(ISNULL(M.KEY_VALUE_DESC,''))), LTRIM(RTRIM(ISNULL(M.KEY_VALUE,''))),
                   LTRIM(RTRIM(ISNULL(M.START_USER,''))), M.START_DATE,
                   LTRIM(RTRIM(ISNULL(M.WF_STATE,'')))
            FROM dbo.WF_MONITOR M WITH (NOLOCK) WHERE M.WF_ID=@WfId;
            """, connection))
        {
            monitorCommand.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
            await using var reader = await monitorCommand.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
                return null;
            var startDate = reader.IsDBNull(6) ? (DateTime?)null : reader.GetDateTime(6);
            monitor = new
            {
                WfId = reader.GetInt64(0),
                ModuleId = reader.GetInt32(1),
                Title = reader.GetString(2),
                KeyValueDesc = reader.GetString(3),
                KeyValue = reader.GetString(4),
                KeyValues = ParseKeyValues(reader.GetString(4)),
                StartUser = reader.GetString(5),
                StartDate = startDate,
                AgeDays = startDate is null ? (double?)null : Math.Max(0, (DateTime.Now - startDate.Value).TotalDays),
                State = reader.GetString(7),
            };
        }

        var tasks = new List<object>();
        await using (var taskCommand = new SqlCommand("""
            SELECT MYTASK_ID, LTRIM(RTRIM(ISNULL(SUBFLOW_NO,''))), LTRIM(RTRIM(ISNULL(SUBFLOW_DESC,''))),
                   LTRIM(RTRIM(ISNULL(APPROVER,''))), LTRIM(RTRIM(ISNULL(APPROVE_STATE,''))),
                   LTRIM(RTRIM(ISNULL(APPROVE_MSG,''))),
                   CONVERT(varchar(19), APPROVE_DATE, 120),
                   ISNULL(APPROVE_POWER,0), ISNULL(IS_CURRENT,0), ISNULL(IS_SIGN,0),
                   ISNULL(IS_MUST_SIGN,0), ISNULL(IS_AUTO_EXEC,0), ISNULL(PASS_PERCENT,0)
            FROM dbo.WF_MYTASK WITH (NOLOCK)
            WHERE WF_ID=@WfId ORDER BY SUBFLOW_NO, MYTASK_ID;
            """, connection))
        {
            taskCommand.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
            await using var reader = await taskCommand.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                tasks.Add(new
                {
                    MyTaskId = reader.GetInt64(0),
                    Step = reader.GetString(1),
                    StepDesc = reader.GetString(2),
                    Approver = reader.GetString(3),
                    State = reader.GetString(4),
                    Message = reader.GetString(5),
                    ApproveDate = reader.IsDBNull(6) ? null : reader.GetString(6),
                    ApprovePower = reader.GetBoolean(7),
                    IsCurrent = reader.GetBoolean(8),
                    IsSign = reader.GetBoolean(9),
                    IsMustSign = reader.GetBoolean(10),
                    IsAutoExec = reader.GetBoolean(11),
                    PassPercent = reader.GetInt32(12),
                });
            }
        }

        var logs = new List<object>();
        await using (var logCommand = new SqlCommand("""
            SELECT LTRIM(RTRIM(ISNULL(SUBFLOW_NO,''))), LTRIM(RTRIM(ISNULL(SUBFLOW_DESC,''))),
                   LTRIM(RTRIM(ISNULL(APP_EMP_ID,''))), LTRIM(RTRIM(ISNULL(APPROVE_STATE,''))),
                   LTRIM(RTRIM(ISNULL(APPROVE_MSG,''))), CONVERT(varchar(19), APPROVE_DATE, 120)
            FROM dbo.WF_MYTASK_LOG WITH (NOLOCK)
            WHERE WF_ID=@WfId ORDER BY APPROVE_DATE;
            """, connection))
        {
            logCommand.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
            await using var reader = await logCommand.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                logs.Add(new
                {
                    Step = reader.GetString(0),
                    StepDesc = reader.GetString(1),
                    Approver = reader.GetString(2),
                    State = reader.GetString(3),
                    Message = reader.GetString(4),
                    Date = reader.IsDBNull(5) ? null : reader.GetString(5),
                });
            }
        }
        return new { Monitor = monitor, Tasks = tasks, Logs = logs };
    }
}
