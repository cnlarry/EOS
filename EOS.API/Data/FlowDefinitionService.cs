using System.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 流程定义维护（模块 2101「表单流程设计」，替代旧 的受控 C# 实现）。
/// 维护 WFFORM（流程主）+ WFFORM_FLOW（步骤）两表；保存为全量重建。
/// 安全边界：
/// - 只允许给"具备批核能力"的工作台模块配置流程（WorkflowStates.HasApproveCapability：
/// 静态登记批核过程 / MODULES.UPDATE_SP 非空 / 效果引擎接管 / 自动批核 / 已配置流程，
/// 且主表有 CONFIRM_TAG 物理列）；
/// - EXEC_PERSON/权限串/必签名单全部按 SYSDL 真实用户校验（禁止写入不存在的审批人）；
/// - EXEC_CONDITION/PERSON_CONDITION/AUTO_EXEC_CONDITION 保存前经 DataFilterParser 按主表
/// 物理列白名单预解析（不可解析即拒绝保存，与引擎启动时同源护栏，绝不拼接 SQL）；
/// - 已存在在途流程实例（WF_MONITOR WF_STATE='0'）时禁止删除定义（防孤儿实例）。
/// </summary>
public sealed class FlowDefinitionService(
    DbConnectionFactory connections,
    WorkbenchAuditWriter auditWriter,
    ILogger<FlowDefinitionService> logger)
{
    /// <summary>
    /// 模块批核能力三开关（自动批核 / 效果引擎接管 / 已配置流程）。三列一律显式 CAST 成 bit：
    /// `CASE … THEN 1 ELSE 0 END` 的结果类型是 int，读端按 `GetBoolean` 取会抛
    /// `InvalidCastException`（流程设计器保存曾因此整链接 500）。两处查询共用本常量，
    /// 读端固定按 0..2 序取这三列，禁止在调用点各写一份。
    /// </summary>
    internal const string ApproveCapabilityColumns =
        "CONVERT(bit, ISNULL(m.AUTO_APPROVE,0)), CONVERT(bit, ISNULL(m.EFFECT_ENGINE_TAG,0)), "
        + "CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM dbo.WFFORM wf WITH (NOLOCK) WHERE wf.WF_M_IDX=m.M_IDX) THEN 1 ELSE 0 END)";

    public sealed record FlowStepDefinition(
        string SortNo,
        string Desc,
        string[] People,
        string? ExecCondition,
        string[]? PersonConditions,
        string[]? ApprovePowers,
        string[]? ForwardPowers,
        string? AutoExecCondition,
        bool IsAutoExec,
        bool IsSign,
        int PassPercent,
        bool IsEffect,
        bool PreMustUnder,
        bool CanSirAgency,
        string[]? MustSigners,
        string? Remark);

    /// <summary>
    /// 模块流程清单：已配置流程（flows）+ 具备批核能力的工作台模块池（eligible，含已配置者）。
    /// </summary>
    public async Task<(IReadOnlyList<object> Flows, IReadOnlyList<object> Eligible)> GetDefinitionsAsync(CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);

        var eligible = new List<object>();
        await using (var command = new SqlCommand($"""
            SELECT m.M_IDX, LTRIM(RTRIM(ISNULL(m.M_DESC,''))), LTRIM(RTRIM(ISNULL(m.MASTER_TABLE,''))),
                   LTRIM(RTRIM(ISNULL(m.M_URL,''))),
                   {ApproveCapabilityColumns}
            FROM dbo.MODULES m WITH (NOLOCK)
            WHERE LTRIM(RTRIM(ISNULL(m.MASTER_TABLE,'')))<>''
            ORDER BY m.M_IDX;
            """, connection))
        {
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var moduleId = reader.GetInt32(0);
                if (!ModuleRouteValidator.IsWorkbenchUrl(reader.GetString(3)))
                    continue;
                // 批核能力：效果引擎接管 / 自动批核 / 已配置流程（遗留批核过程字段已物理删除）
                var hasWorkflow = WorkflowStates.HasApproveCapability(
                    reader.GetBoolean(4),
                    reader.GetBoolean(5),
                    reader.GetBoolean(6));
                if (!hasWorkflow)
                    continue;
                eligible.Add(new
                {
                    ModuleId = moduleId,
                    Title = reader.GetString(1),
                    MasterTable = reader.GetString(2),
                    AutoApprove = reader.GetBoolean(4),
                });
            }
        }

        // 主表必须有 CONFIRM_TAG（流程末步落确认），无该列的表剔除（如日志流水表）
        var withConfirm = new List<object>();
        foreach (var candidate in eligible)
        {
            var table = (string)candidate.GetType().GetProperty("MasterTable")!.GetValue(candidate)!;
            if (!await WorkbenchSql.ColumnExistsAsync(connection, null, table, "CONFIRM_TAG", token))
                continue;
            withConfirm.Add(candidate);
        }
        eligible = withConfirm;

        var flows = new List<object>();
        await using (var command = new SqlCommand("""
            SELECT wf.WF_M_IDX,
                   LTRIM(RTRIM(ISNULL((SELECT M_DESC FROM dbo.MODULES WHERE M_IDX=wf.WF_M_IDX),''))),
                   LTRIM(RTRIM(ISNULL(wf.FLOW_NAME,''))), LTRIM(RTRIM(ISNULL(wf.REMARK,''))),
                   (SELECT COUNT_BIG(1) FROM dbo.WFFORM_FLOW f WITH (NOLOCK) WHERE f.WF_M_IDX=wf.WF_M_IDX),
                   LTRIM(RTRIM(ISNULL(wf.CONFIRM_PERSON,''))),
                   CONVERT(varchar(19), wf.CONFIRM_DATE, 120)
            FROM dbo.WFFORM wf WITH (NOLOCK)
            ORDER BY wf.WF_M_IDX;
            """, connection))
        {
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                flows.Add(new
                {
                    ModuleId = reader.GetInt32(0),
                    Title = reader.GetString(1),
                    FlowName = reader.GetString(2),
                    Remark = reader.GetString(3),
                    StepCount = reader.GetInt64(4),
                    UpdatedBy = reader.GetString(5),
                    UpdatedAt = reader.IsDBNull(6) ? null : reader.GetString(6),
                });
            }
        }
        return (flows, eligible);
    }

    /// <summary>单模块流程详情（WFFORM + WFFORM_FLOW 全量步骤）；未配置返回 null。</summary>
    public async Task<object?> GetFlowAsync(int moduleId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);

        string? title;
        string? flowName;
        string? flowRemark;
        string? masterTable;
        string? confirmPerson;
        string? confirmDate;
        await using (var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(ISNULL(m.M_DESC,''))), LTRIM(RTRIM(ISNULL(m.MASTER_TABLE,''))),
                   LTRIM(RTRIM(ISNULL(wf.FLOW_NAME,''))), LTRIM(RTRIM(ISNULL(wf.REMARK,''))),
                   LTRIM(RTRIM(ISNULL(wf.CONFIRM_PERSON,''))),
                   CONVERT(varchar(19), wf.CONFIRM_DATE, 120)
            FROM dbo.WFFORM wf WITH (NOLOCK)
            LEFT JOIN dbo.MODULES m WITH (NOLOCK) ON m.M_IDX=wf.WF_M_IDX
            WHERE wf.WF_M_IDX=@ModuleId;
            """, connection))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
                return null;
            title = reader.GetString(0);
            masterTable = reader.GetString(1);
            flowName = reader.GetString(2);
            flowRemark = reader.GetString(3);
            confirmPerson = reader.GetString(4);
            confirmDate = reader.IsDBNull(5) ? null : reader.GetString(5);
        }

        var steps = new List<object>();
        await using (var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(ISNULL(SORT_NO,''))), LTRIM(RTRIM(ISNULL(SUBFLOW_DESC,''))),
                   LTRIM(RTRIM(ISNULL(EXEC_PERSON,''))), LTRIM(RTRIM(ISNULL(EXEC_CONDITION,''))),
                   LTRIM(RTRIM(ISNULL(PERSON_CONDITION,''))), LTRIM(RTRIM(ISNULL(PERSON_APP_POWER,''))),
                   LTRIM(RTRIM(ISNULL(PERSON_FORWARD_POWER,''))), LTRIM(RTRIM(ISNULL(AUTO_EXEC_CONDITION,''))),
                   ISNULL(CAST(IS_AUTO_EXEC AS int),0), ISNULL(CAST(IS_SIGN AS int),0),
                   ISNULL(CAST(PASS_PERCENT AS int),0), ISNULL(CAST(IS_EFFECT AS int),0),
                   ISNULL(CAST(PRE_MUST_UNDER AS int),0), ISNULL(CAST(CAN_SIR_AGENCY AS int),0),
                   LTRIM(RTRIM(ISNULL(MUST_SIGNER,''))), LTRIM(RTRIM(ISNULL(REMARK,'')))
            FROM dbo.WFFORM_FLOW WITH (NOLOCK)
            WHERE WF_M_IDX=@ModuleId ORDER BY SORT_NO;
            """, connection))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                steps.Add(new
                {
                    SortNo = reader.GetString(0),
                    Desc = reader.GetString(1),
                    People = SplitSemicolon(reader.GetString(2)),
                    ExecCondition = reader.GetString(3),
                    PersonConditions = SplitSemicolon(reader.GetString(4)),
                    ApprovePowers = SplitSemicolon(reader.GetString(5)),
                    ForwardPowers = SplitSemicolon(reader.GetString(6)),
                    AutoExecCondition = reader.GetString(7),
                    IsAutoExec = reader.GetInt32(8) == 1,
                    IsSign = reader.GetInt32(9) == 1,
                    PassPercent = reader.GetInt32(10),
                    IsEffect = reader.GetInt32(11) == 1,
                    PreMustUnder = reader.GetInt32(12) == 1,
                    CanSirAgency = reader.GetInt32(13) == 1,
                    MustSigners = SplitSemicolon(reader.GetString(14)),
                    Remark = reader.GetString(15),
                });
            }
        }
        return new
        {
            ModuleId = moduleId,
            Title = title,
            MasterTable = masterTable,
            FlowName = flowName,
            Remark = flowRemark,
            UpdatedBy = confirmPerson,
            UpdatedAt = confirmDate,
            Steps = steps,
        };
    }

    /// <summary>
    /// 保存流程定义（全量重建：WFFORM 单行 + WFFORM_FLOW 全部步骤，单事务）。
    /// 校验不通过返回带错误码的 RecordSaveResult（不落任何数据）。
    /// </summary>
    public async Task<RecordSaveResult> SaveFlowAsync(
        int moduleId,
        string? flowName,
        string? remark,
        IReadOnlyList<FlowStepDefinition> steps,
        string userId,
        string employeeName,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(flowName))
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "FLOW_NAME_REQUIRED", "流程名称不能为空。");
        if (flowName.Trim().Length > 100)
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "FLOW_NAME_TOO_LONG", "流程名称不能超过 100 字符。");
        if (steps is null || steps.Count == 0)
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "FLOW_NO_STEPS", "至少需要配置一个审批步骤。");

        await using var connection = connections.Create();
        await connection.OpenAsync(token);

        // 模块 + 批核能力校验
        string? title;
        string? masterTable;
        await using (var moduleCommand = new SqlCommand($"""
            SELECT LTRIM(RTRIM(ISNULL(m.M_DESC,''))), LTRIM(RTRIM(ISNULL(m.MASTER_TABLE,''))),
                   LTRIM(RTRIM(ISNULL(m.M_URL,''))),
                   {ApproveCapabilityColumns}
            FROM dbo.MODULES m WITH (NOLOCK) WHERE m.M_IDX=@ModuleId;
            """, connection))
        {
            moduleCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await using var reader = await moduleCommand.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
                return RecordSaveResult.Failed(RecordAccessStatus.NotFound, "MODULE_NOT_FOUND", "模块不存在。");
            title = reader.GetString(0);
            masterTable = reader.GetString(1);
            var moduleUrl = reader.GetString(2);
            if (!ModuleRouteValidator.IsWorkbenchUrl(moduleUrl))
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "MODULE_NOT_WORKBENCH", "只能给通用工作台模块配置审批流程。");
            var hasWorkflow = WorkflowStates.HasApproveCapability(
                reader.GetBoolean(3),
                reader.GetBoolean(4),
                reader.GetBoolean(5));
            if (!hasWorkflow)
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "MODULE_NO_APPROVE", "该模块未配置批核能力（无效果链、无自动批核），不能配置流程。");
        }
        if (string.IsNullOrWhiteSpace(masterTable) || !WorkbenchSql.Identifier.IsMatch(masterTable))
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "INVALID_MASTER_TABLE", "模块主表无效。");
        if (!await WorkbenchSql.ColumnExistsAsync(connection, null, masterTable, "CONFIRM_TAG", token))
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "MASTER_NO_CONFIRM_TAG",
                "模块主表缺少 CONFIRM_TAG 列，不能配置流程。");

        // 步骤基础校验：SORT_NO 格式/唯一、描述、审批人
        var normalized = new List<(string SortNo, FlowStepDefinition Step)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var sortNo = NormalizeSortNo(step.SortNo);
            if (sortNo is null)
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "INVALID_SORT_NO",
                    $"步骤 {step.SortNo} 的序号无效（应为 1~999 的数字）。");
            if (!seen.Add(sortNo))
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "DUPLICATE_SORT_NO",
                    $"步骤序号 {sortNo} 重复。");
            if (string.IsNullOrWhiteSpace(step.Desc))
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "STEP_DESC_REQUIRED",
                    $"步骤 {sortNo} 缺少步骤名称。");
            if (step.Desc.Trim().Length > 50)
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "STEP_DESC_TOO_LONG",
                    $"步骤 {sortNo} 名称不能超过 50 字符。");
            if (step.People is null || step.People.Length == 0)
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "STEP_NO_PEOPLE",
                    $"步骤 {sortNo} 至少需要一个审批人。");
            if (step.PassPercent is < 0 or > 100)
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "INVALID_PASS_PERCENT",
                    $"步骤 {sortNo} 的会签通过阈值必须为 0~100。");
            if (step.IsSign && step.PassPercent <= 0)
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "SIGN_NEED_PERCENT",
                    $"会签步骤 {sortNo} 必须设置通过阈值（PASS_PERCENT）。");
            normalized.Add((sortNo, step));
        }

        // 审批人/权限串/必签名单真实存在性（SYSDL），一次性批量校验
        var allPeople = normalized
            .SelectMany(pair => pair.Step.People)
            .Concat(normalized.SelectMany(pair => pair.Step.ApprovePowers ?? Array.Empty<string>()))
            .Concat(normalized.SelectMany(pair => pair.Step.ForwardPowers ?? Array.Empty<string>()))
            .Concat(normalized.SelectMany(pair => pair.Step.MustSigners ?? Array.Empty<string>()))
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var knownUsers = await LoadKnownUsersAsync(connection, allPeople, token);
        var unknown = allPeople.Where(person => !knownUsers.Contains(person, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0)
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "UNKNOWN_PERSON",
                $"以下审批人不存在（SYSDL）：{string.Join("、", unknown)}。");

        // 每步条件预解析（EXEC/PERSON/AUTO），主表物理列白名单，不可解析即拒绝保存
        var masterColumns = await LoadMasterColumnsAsync(connection, masterTable, token);
        foreach (var (sortNo, step) in normalized)
        {
            foreach (var (condition, kind) in new[]
            {
                (step.ExecCondition, "执行条件"),
                (step.AutoExecCondition, "自动执行条件"),
            })
            {
                if (string.IsNullOrWhiteSpace(condition)) continue;
                if (!DataFilterParser.TryParse(condition, masterTable, masterColumns, out _, out _))
                    return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "FLOW_CONDITION_UNSUPPORTED",
                        $"步骤 {sortNo} 的{kind}无法安全解析，已拒绝保存。");
            }
            var personConditions = step.PersonConditions ?? Array.Empty<string>();
            for (var i = 0; i < personConditions.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(personConditions[i])) continue;
                if (!DataFilterParser.TryParse(personConditions[i], masterTable, masterColumns, out _, out _))
                    return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "FLOW_CONDITION_UNSUPPORTED",
                        $"步骤 {sortNo} 审批人 {step.People[i]} 的动态条件无法安全解析，已拒绝保存。");
            }
            var mustSigners = step.MustSigners ?? Array.Empty<string>();
            if (mustSigners.Length > 0 && !step.IsSign)
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "MUST_SIGN_NOT_SIGN",
                    $"步骤 {sortNo} 只有会签步骤（IS_SIGN=1）才能设置必签名单。");
            var mustNotInPeople = mustSigners
                .Where(person => !step.People.Contains(person, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (mustNotInPeople.Length > 0)
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "MUST_SIGN_NOT_IN_PEOPLE",
                    $"步骤 {sortNo} 的必签人不在审批人列表中：{string.Join("、", mustNotInPeople)}。");
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            // 有在途实例时禁止覆盖（防在途任务引用的步骤语义被改坏）
            var activeCount = await CountActiveFlowsAsync(connection, transaction, moduleId, token);
            if (activeCount > 0)
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "FLOW_ACTIVE_CANNOT_EDIT",
                    $"模块存在 {activeCount} 个在途审批流程，不能修改流程定义；请先处理完在途流程。");

            // WFFORM upsert
            var flowNameValue = flowName.Trim();
            var remarkValue = remark?.Trim() ?? string.Empty;
            var updated = await new SqlCommand("""
                UPDATE dbo.WFFORM SET FLOW_NAME=@FlowName, REMARK=@Remark,
                    CONFIRM_PERSON=@Person, CONFIRM_DATE=GETDATE()
                WHERE WF_M_IDX=@ModuleId;
                """, connection, transaction)
                .Apply(cmd =>
                {
                    cmd.Parameters.Add("@FlowName", SqlDbType.NVarChar, 100).Value = flowNameValue;
                    cmd.Parameters.Add("@Remark", SqlDbType.NVarChar, 400).Value = remarkValue;
                    cmd.Parameters.Add("@Person", SqlDbType.NVarChar, 50).Value = employeeName.Trim();
                    cmd.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
                })
                .ExecuteNonQueryAsync(token);
            if (updated == 0)
            {
                await new SqlCommand("""
                    INSERT INTO dbo.WFFORM (WF_M_IDX, FLOW_NAME, REMARK, CREATE_PERSON, CREATE_DATE, CONFIRM_TAG, CONFIRM_PERSON, CONFIRM_DATE)
                    VALUES (@ModuleId, @FlowName, @Remark, @Person, GETDATE(), 1, @Person, GETDATE());
                    """, connection, transaction)
                    .Apply(cmd =>
                    {
                        cmd.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
                        cmd.Parameters.Add("@FlowName", SqlDbType.NVarChar, 100).Value = flowNameValue;
                        cmd.Parameters.Add("@Remark", SqlDbType.NVarChar, 400).Value = remarkValue;
                        cmd.Parameters.Add("@Person", SqlDbType.NVarChar, 50).Value = employeeName.Trim();
                    })
                    .ExecuteNonQueryAsync(token);
            }

            // 全量重建步骤
            await new SqlCommand("DELETE FROM dbo.WFFORM_FLOW WHERE WF_M_IDX=@ModuleId;", connection, transaction)
                .Apply(cmd => cmd.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId)
                .ExecuteNonQueryAsync(token);
            const string stepSql = """
                INSERT INTO dbo.WFFORM_FLOW
                    (WF_M_IDX, SORT_NO, SUBFLOW_DESC, EXEC_PERSON, EXEC_CONDITION, PERSON_CONDITION,
                     PERSON_APP_POWER, PERSON_FORWARD_POWER, AUTO_EXEC_CONDITION, IS_AUTO_EXEC, IS_SIGN,
                     PASS_PERCENT, IS_EFFECT, PRE_MUST_UNDER, CAN_SIR_AGENCY, MUST_SIGNER, REMARK)
                VALUES
                    (@ModuleId, @SortNo, @Desc, @People, @ExecCondition, @PersonCondition,
                     @ApprovePower, @ForwardPower, @AutoExecCondition, @IsAutoExec, @IsSign,
                     @PassPercent, @IsEffect, @PreMustUnder, @CanSirAgency, @MustSigners, @Remark);
                """;
            foreach (var (sortNo, step) in normalized)
            {
                await using var stepCommand = new SqlCommand(stepSql, connection, transaction);
                stepCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
                stepCommand.Parameters.Add("@SortNo", SqlDbType.Char, 3).Value = sortNo;
                stepCommand.Parameters.Add("@Desc", SqlDbType.VarChar, 50).Value = step.Desc.Trim();
                stepCommand.Parameters.Add("@People", SqlDbType.VarChar, 2000).Value = JoinSemicolon(step.People);
                stepCommand.Parameters.Add("@ExecCondition", SqlDbType.VarChar, 500).Value = (object?)(step.ExecCondition?.Trim()) ?? DBNull.Value;
                stepCommand.Parameters.Add("@PersonCondition", SqlDbType.VarChar, 2000).Value = JoinSemicolon(step.PersonConditions);
                stepCommand.Parameters.Add("@ApprovePower", SqlDbType.VarChar, 2000).Value = JoinSemicolon(step.ApprovePowers);
                stepCommand.Parameters.Add("@ForwardPower", SqlDbType.VarChar, 2000).Value = JoinSemicolon(step.ForwardPowers);
                stepCommand.Parameters.Add("@AutoExecCondition", SqlDbType.VarChar, 500).Value = (object?)(step.AutoExecCondition?.Trim()) ?? DBNull.Value;
                stepCommand.Parameters.Add("@IsAutoExec", SqlDbType.Bit).Value = step.IsAutoExec;
                stepCommand.Parameters.Add("@IsSign", SqlDbType.Bit).Value = step.IsSign;
                stepCommand.Parameters.Add("@PassPercent", SqlDbType.Int).Value = step.PassPercent;
                stepCommand.Parameters.Add("@IsEffect", SqlDbType.Bit).Value = step.IsEffect;
                stepCommand.Parameters.Add("@PreMustUnder", SqlDbType.Bit).Value = step.PreMustUnder;
                stepCommand.Parameters.Add("@CanSirAgency", SqlDbType.Bit).Value = step.CanSirAgency;
                stepCommand.Parameters.Add("@MustSigners", SqlDbType.VarChar, 2000).Value = JoinSemicolon(step.MustSigners);
                stepCommand.Parameters.Add("@Remark", SqlDbType.VarChar, 400).Value = (object?)(step.Remark?.Trim()) ?? DBNull.Value;
                await stepCommand.ExecuteNonQueryAsync(token);
            }

            await auditWriter.WriteEventAsync(connection, transaction, 2101, moduleId.ToString(),
                "FLOW_SAVE", $"保存流程定义「{flowNameValue}」（模块 {moduleId} {title}）", userId,
                "WORKFLOW_DESIGN", 1, null, token);
            await transaction.CommitAsync(token);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }

        logger.LogInformation("流程定义保存 module={ModuleId} steps={Steps} user={User}",
            moduleId, normalized.Count, userId);
        return RecordSaveResult.Success([moduleId.ToString()]);
    }

    /// <summary>删除流程定义（无在途实例时才允许；历史完成/撤回实例不阻塞删除定义）。</summary>
    public async Task<RecordSaveResult> DeleteFlowAsync(int moduleId, string userId, string employeeName, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var exists = await new SqlCommand(
                "SELECT TOP 1 1 FROM dbo.WFFORM WITH (NOLOCK) WHERE WF_M_IDX=@ModuleId;", connection, transaction)
                .Apply(cmd => cmd.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId)
                .ExecuteScalarAsync(token) is not null;
            if (!exists)
                return RecordSaveResult.Failed(RecordAccessStatus.NotFound, "FLOW_NOT_FOUND", "该模块未配置流程。");
            var activeCount = await CountActiveFlowsAsync(connection, transaction, moduleId, token);
            if (activeCount > 0)
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed, "FLOW_ACTIVE_CANNOT_DELETE",
                    $"模块存在 {activeCount} 个在途审批流程，不能删除流程定义。");

            await new SqlCommand("DELETE FROM dbo.WFFORM_FLOW WHERE WF_M_IDX=@ModuleId;", connection, transaction)
                .Apply(cmd => cmd.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId)
                .ExecuteNonQueryAsync(token);
            await new SqlCommand("DELETE FROM dbo.WFFORM WHERE WF_M_IDX=@ModuleId;", connection, transaction)
                .Apply(cmd => cmd.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId)
                .ExecuteNonQueryAsync(token);
            await auditWriter.WriteEventAsync(connection, transaction, 2101, moduleId.ToString(),
                "FLOW_DELETE", $"删除流程定义（模块 {moduleId}）", userId, "WORKFLOW_DESIGN", 1, null, token);
            await transaction.CommitAsync(token);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
        logger.LogInformation("流程定义删除 module={ModuleId} user={User}", moduleId, userId);
        return RecordSaveResult.Success([moduleId.ToString()]);
    }

    /// <summary>活跃用户检索（设计器人员选择数据源；SYSDL 启用账号 + SYSDN 姓名）。</summary>
    public async Task<IReadOnlyList<object>> GetPeopleAsync(string? keyword, CancellationToken token)
    {
        var kw = (keyword ?? string.Empty).Trim();
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var result = new List<object>();
        await using var command = new SqlCommand("""
            SELECT TOP 200 LTRIM(RTRIM(S.USER_ID)), LTRIM(RTRIM(ISNULL(N.EMP_NAME,''))),
                   LTRIM(RTRIM(ISNULL(N.DEPT_ID,''))), S.ACTIVE_TAG
            FROM dbo.SYSDL S WITH (NOLOCK)
            LEFT JOIN dbo.SYSDN N WITH (NOLOCK) ON N.EMP_ID=S.EMP_ID
            WHERE S.ACTIVE_TAG=1
              AND (@Kw=N'' OR S.USER_ID LIKE @Like OR N.EMP_NAME LIKE @Like)
            ORDER BY S.USER_ID;
            """, connection);
        command.Parameters.Add("@Kw", SqlDbType.NVarChar, 100).Value = kw;
        command.Parameters.Add("@Like", SqlDbType.NVarChar, 200).Value = $"%{kw}%";
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            result.Add(new
            {
                UserId = reader.GetString(0),
                Name = reader.GetString(1),
                DeptId = reader.GetString(2),
                Active = reader.IsDBNull(3) || reader.GetBoolean(3),
            });
        }
        return result;
    }

    private static async Task<int> CountActiveFlowsAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT COUNT_BIG(1) FROM dbo.WF_MONITOR WITH (NOLOCK) WHERE WF_M_IDX=@ModuleId AND WF_STATE='0';",
            connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        return Convert.ToInt32(await command.ExecuteScalarAsync(token));
    }

    private static async Task<HashSet<string>> LoadKnownUsersAsync(
        SqlConnection connection, IReadOnlyList<string> users, CancellationToken token)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (users.Count == 0)
            return result;
        const int chunk = 500;
        for (var offset = 0; offset < users.Count; offset += chunk)
        {
            var batch = users.Skip(offset).Take(chunk).ToArray();
            var inClause = string.Join(",", batch.Select((_, index) => $"@u{index}"));
            await using var command = new SqlCommand(
                $"SELECT LTRIM(RTRIM(USER_ID)) FROM dbo.SYSDL WITH (NOLOCK) WHERE USER_ID IN ({inClause});", connection);
            for (var i = 0; i < batch.Length; i++)
                command.Parameters.Add($"@u{i}", SqlDbType.NVarChar, 50).Value = batch[i];
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                result.Add(reader.GetString(0));
        }
        return result;
    }

    private static async Task<HashSet<string>> LoadMasterColumnsAsync(
        SqlConnection connection, string masterTable, CancellationToken token)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand(
            "SELECT c.name FROM sys.columns c JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V') JOIN sys.schemas s ON o.schema_id=s.schema_id WHERE s.name=N'dbo' AND o.name=@Table ORDER BY c.column_id;",
            connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = masterTable;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) columns.Add(reader.GetString(0));
        return columns;
    }

    private static string? NormalizeSortNo(string? sortNo)
    {
        if (string.IsNullOrWhiteSpace(sortNo))
            return null;
        var trimmed = sortNo.Trim();
        if (!int.TryParse(trimmed, out var number) || number < 1 || number > 999)
            return null;
        return number.ToString("D3");
    }

    private static string JoinSemicolon(IReadOnlyList<string>? values)
        => values is null || values.Count == 0
            ? string.Empty
            : string.Join(";", values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()));

    private static string[] SplitSemicolon(string value)
        => value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

internal static class SqlCommandExtensions
{
    public static SqlCommand Apply(this SqlCommand command, Action<SqlCommand> configure)
    {
        configure(command);
        return command;
    }
}
