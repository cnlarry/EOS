using System.Data;
using EOS.API.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Features.Assistant.Tools;

public sealed record FlowStepInfo(
    string SortNo, string Desc, string[] People, bool IsSign, int PassPercent,
    string[] MustSigners, bool HasExecCondition);

public sealed record FlowDefinitionInfo(string FlowName, IReadOnlyList<FlowStepInfo> Steps);

public sealed record FlowInstanceInfo(
    long WfId, string State, string StartUser,
    string? CurrentStepNo, string? CurrentStepDesc,
    IReadOnlyList<string> CurrentApprovers, int LogCount);

public sealed record RecordStateInfo(bool Found, bool Confirmed, bool Finished);

/// <summary>
/// Module flow read adapter over existing WF_* facts + record state columns.
/// No parallel fact source: unknown relations stay unknown, never guessed.
/// </summary>
public sealed record ApprovalConfirmInfo(string Description, string? FinishedBy);

public interface IModuleFlowGateway
{
    Task<FlowDefinitionInfo?> GetFlowDefinitionAsync(int moduleId, CancellationToken token);
    Task<FlowInstanceInfo?> GetInstanceAsync(int moduleId, string keyCondition, CancellationToken token);
    Task<RecordStateInfo> GetRecordStateAsync(
        string masterTable, IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues,
        CancellationToken token);

    /// <summary>流程完成确认记录（WF_APPROVE：终审描述与完成人），无记录返回 null。</summary>
    Task<ApprovalConfirmInfo?> GetApprovalConfirmAsync(int moduleId, string keyCondition, CancellationToken token);
}

public sealed class ModuleFlowGateway(DbConnectionFactory connections) : IModuleFlowGateway
{
    public async Task<FlowDefinitionInfo?> GetFlowDefinitionAsync(int moduleId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        string? flowName = null;
        await using (var command = new SqlCommand(
            "SELECT LTRIM(RTRIM(ISNULL(FLOW_NAME,''))) FROM dbo.WFFORM WITH (NOLOCK) WHERE WF_M_IDX=@ModuleId;",
            connection))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            flowName = await command.ExecuteScalarAsync(token) as string;
        }

        if (flowName is null) return null;
        var steps = new List<FlowStepInfo>();
        await using (var command = new SqlCommand(
            """
            SELECT LTRIM(RTRIM(ISNULL(SORT_NO,''))), LTRIM(RTRIM(ISNULL(SUBFLOW_DESC,''))),
                   LTRIM(RTRIM(ISNULL(EXEC_PERSON,''))), ISNULL(CAST(IS_SIGN AS int),0),
                   ISNULL(CAST(PASS_PERCENT AS int),0), LTRIM(RTRIM(ISNULL(MUST_SIGNER,''))),
                   CASE WHEN LTRIM(RTRIM(ISNULL(EXEC_CONDITION,''))) <> ''
                          OR LTRIM(RTRIM(ISNULL(PERSON_CONDITION,''))) <> '' THEN 1 ELSE 0 END
            FROM dbo.WFFORM_FLOW WITH (NOLOCK)
            WHERE WF_M_IDX=@ModuleId ORDER BY SORT_NO;
            """, connection))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                steps.Add(new(
                    reader.GetString(0), reader.GetString(1),
                    SplitPeople(reader.GetString(2)), reader.GetInt32(3) == 1, reader.GetInt32(4),
                    SplitPeople(reader.GetString(5)), reader.GetInt32(6) == 1));
            }
        }

        return steps.Count == 0 ? null : new(flowName, steps);
    }

    public async Task<FlowInstanceInfo?> GetInstanceAsync(int moduleId, string keyCondition, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        long wfId;
        string state;
        string startUser;
        await using (var command = new SqlCommand(
            "SELECT WF_ID, LTRIM(RTRIM(ISNULL(WF_STATE,''))), LTRIM(RTRIM(ISNULL(START_USER,''))) FROM dbo.WF_MONITOR WITH (NOLOCK) WHERE WF_M_IDX=@ModuleId AND KEY_VALUE=@KeyValue;",
            connection))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            command.Parameters.Add("@KeyValue", SqlDbType.VarChar, 200).Value = keyCondition;
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return null;
            wfId = reader.GetInt64(0);
            state = reader.GetString(1);
            startUser = reader.GetString(2);
        }

        string? stepNo = null;
        string? stepDesc = null;
        var approvers = new List<string>();
        await using (var command = new SqlCommand(
            "SELECT LTRIM(RTRIM(ISNULL(SUBFLOW_NO,''))), LTRIM(RTRIM(ISNULL(SUBFLOW_DESC,''))), LTRIM(RTRIM(ISNULL(APPROVER,''))) FROM dbo.WF_MYTASK WITH (NOLOCK) WHERE WF_ID=@WfId AND IS_CURRENT=1 ORDER BY SUBFLOW_NO;",
            connection))
        {
            command.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                stepNo ??= reader.GetString(0);
                stepDesc ??= reader.GetString(1);
                if (reader.GetString(2).Length > 0) approvers.Add(reader.GetString(2));
            }
        }

        int logCount;
        await using (var command = new SqlCommand(
            "SELECT COUNT_BIG(1) FROM dbo.WF_MYTASK_LOG WITH (NOLOCK) WHERE WF_ID=@WfId;", connection))
        {
            command.Parameters.Add("@WfId", SqlDbType.BigInt).Value = wfId;
            logCount = Convert.ToInt32(await command.ExecuteScalarAsync(token));
        }

        return new(wfId, state, startUser, stepNo, stepDesc, approvers, logCount);
    }

    public async Task<ApprovalConfirmInfo?> GetApprovalConfirmAsync(
        int moduleId, string keyCondition, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(
            "SELECT TOP 1 KEY_VALUE_DESC, LAST_UPDATE_BY FROM dbo.WF_APPROVE WITH (NOLOCK) WHERE M_IDX=@ModuleId AND KEY_VALUE=@KeyValue;",
            connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@KeyValue", SqlDbType.VarChar, 200).Value = keyCondition;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new(
            reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim(),
            reader.IsDBNull(1) ? null : reader.GetString(1).Trim());
    }

    public async Task<RecordStateInfo> GetRecordStateAsync(
        string masterTable, IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (!WorkbenchSql.Identifier.IsMatch(masterTable)
            || pkColumns.Count == 0 || pkColumns.Count != keyValues.Count
            || pkColumns.Any(column => !WorkbenchSql.Identifier.IsMatch(column))
            || keyValues.Any(string.IsNullOrEmpty))
        {
            return new(false, false, false);
        }

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var hasConfirm = await WorkbenchSql.ColumnExistsAsync(connection, null, masterTable, "CONFIRM_TAG", token);
        var hasFinished = await WorkbenchSql.ColumnExistsAsync(connection, null, masterTable, "FINISHED_TAG", token);
        var selectList = string.Join(",", new[] { hasConfirm ? "ISNULL(CONFIRM_TAG,0)" : "CAST(0 AS bit)",
            hasFinished ? "ISNULL(FINISHED_TAG,0)" : "CAST(0 AS bit)" });
        var where = string.Join(" AND ", pkColumns.Select((column, index) => $"[{column}]=@P{index}"));
        await using var command = new SqlCommand(
            $"SELECT {selectList} FROM dbo.[{masterTable}] WITH (NOLOCK) WHERE {where};", connection);
        for (var index = 0; index < keyValues.Count; index++)
        {
            command.Parameters.Add($"@P{index}", SqlDbType.NVarChar, 200).Value = keyValues[index];
        }

        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return new(false, false, false);
        return new(true, Convert.ToBoolean(reader.GetValue(0)), Convert.ToBoolean(reader.GetValue(1)));
    }

    private static string[] SplitPeople(string value) =>
        value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
