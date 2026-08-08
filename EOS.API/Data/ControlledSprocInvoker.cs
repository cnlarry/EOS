using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.RegularExpressions;

namespace EOS.API.Data;

public sealed record SprocResult(bool Success, string? Message);

/// <summary>
/// 受控存储过程执行器：仅允许执行 ModuleBusinessMap 白名单内的固定存储过程，
/// 参数由服务端从模块主键元数据构造（列名来自服务端白名单，值做单引号转义），
/// 不信任任何来自客户端的表名/列名/存储过程名/条件片段。
/// 对应旧系统 P_Run_After_Save（AFTERSAVE_SP）与单据批核（P_WF_*）的受控调用。
/// </summary>
public sealed class ControlledSprocInvoker(DbConnectionFactory connections, ILogger<ControlledSprocInvoker> logger)
{
    private static readonly Regex SprocName = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

    /// <summary>
    /// 构造旧系统 SP 需要的主键条件（@pri_idx / @key_value），如 [QUOTE_TYPE]='BJK' AND [QUOTE_NO]='BJK26080001'。
    /// </summary>
    public static string BuildKeyCondition(IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues)
    {
        if (pkColumns.Count != keyValues.Count)
            throw new ArgumentException("主键列与主键值数量不一致。");
        return string.Join(" AND ", pkColumns.Select((column, index) =>
            $"[{column}]='{Escape(keyValues[index])}'"));
    }

    private static string Escape(string value) => value.Replace("'", "''");

    /// <summary>
    /// 保存后执行 AFTERSAVE_SP（旧 P_Run_After_Save 的受控等价）。
    /// </summary>
    public async Task<SprocResult> RunAfterSaveAsync(
        int moduleId,
        string sprocName,
        IReadOnlyList<string> pkColumns,
        IReadOnlyList<string> keyValues,
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken token)
    {
        if (!IsAllowed(sprocName)) return new(false, $"存储过程不在受控白名单内：{sprocName}");
        var keyCondition = BuildKeyCondition(pkColumns, keyValues);
        await using var command = new SqlCommand(sprocName, connection, transaction)
        {
            CommandType = CommandType.StoredProcedure,
        };
        command.Parameters.Add("@pri_idx", SqlDbType.NVarChar, 1000).Value = keyCondition;
        command.Parameters.Add("@module", SqlDbType.Int).Value = moduleId;
        try
        {
            await command.ExecuteNonQueryAsync(token);
            return new(true, null);
        }
        catch (SqlException ex)
        {
            logger.LogWarning("AfterSave 存储过程失败 module={ModuleId} sproc={Sproc} message={Message}",
                moduleId, sprocName, ex.Message);
            return new(false, SanitizeMessage(ex.Message));
        }
    }

    /// <summary>
    /// 批核（approve=true）或解批（approve=false），对应旧 P_WF_&lt;DOC&gt; @key_value, @approve_tag, @msg。
    /// </summary>
    public async Task<SprocResult> RunWorkflowAsync(
        int moduleId,
        string sprocName,
        IReadOnlyList<string> pkColumns,
        IReadOnlyList<string> keyValues,
        bool approve,
        SqlConnection? connection,
        SqlTransaction? transaction,
        CancellationToken token)
    {
        if (!IsAllowed(sprocName)) return new(false, $"存储过程不在受控白名单内：{sprocName}");
        var ownsConnection = connection is null;
        connection ??= connections.Create();
        if (ownsConnection) await connection.OpenAsync(token);
        var keyCondition = BuildKeyCondition(pkColumns, keyValues);
        await using var command = new SqlCommand(sprocName, connection, transaction)
        {
            CommandType = CommandType.StoredProcedure,
        };
        command.Parameters.Add("@key_value", SqlDbType.VarChar, 200).Value = keyCondition;
        command.Parameters.Add("@approve_tag", SqlDbType.Int).Value = approve ? 1 : -1;
        var messageParameter = command.Parameters.Add("@msg", SqlDbType.VarChar, 8000);
        messageParameter.Direction = ParameterDirection.Output;
        var rcParameter = command.Parameters.Add("@rc", SqlDbType.Int);
        rcParameter.Direction = ParameterDirection.ReturnValue;
        try
        {
            await command.ExecuteNonQueryAsync(token);
            var success = rcParameter.Value is int rc && rc == 1;
            var message = messageParameter.Value as string;
            if (!success)
                logger.LogWarning("批核存储过程返回失败 module={ModuleId} sproc={Sproc} rc={Rc} message={Message}",
                    moduleId, sprocName, rcParameter.Value, message);
            return new(success, string.IsNullOrWhiteSpace(message) ? null : SanitizeMessage(message));
        }
        catch (SqlException ex)
        {
            logger.LogWarning("批核存储过程异常 module={ModuleId} sproc={Sproc} message={Message}",
                moduleId, sprocName, ex.Message);
            return new(false, SanitizeMessage(ex.Message));
        }
        finally
        {
            if (ownsConnection) await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// 存储过程名只允许标识符（防注入）；具体来源必须是当前模块的领域规则
    /// （MODULES.UPDATE_SP / AFTERSAVE_SP 元数据或静态白名单），由调用方保证。
    /// </summary>
    private bool IsAllowed(string sprocName) =>
        !string.IsNullOrWhiteSpace(sprocName) && SprocName.IsMatch(sprocName);

    /// <summary>
    /// 存储过程消息可能包含换行/制表符，统一压缩为单行展示文本。
    /// </summary>
    private static string SanitizeMessage(string message) =>
        string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
