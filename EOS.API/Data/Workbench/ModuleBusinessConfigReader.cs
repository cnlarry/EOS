using System.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Workbench;

/// <summary>
/// 模块业务动作与校验规则的**只读**入口。
///
/// 为什么不直接复用 <see cref="ModuleBusinessConfigRepository"/>：配置仓库同时依赖单据动作
/// 注册表（写路径要校验按钮处理器）与定义构建器（保存后标脏/校验），而定义构建器又要读这
/// 两段配置——把仓库注入构建器就形成 `构建器 → 仓库 → 动作注册表 → 处理器 → 构建器` 的环，
/// DI 在启动时直接拒绝构造。读取只需要一个连接，所以单列这一处。
/// </summary>
public sealed class ModuleBusinessConfigReader(DbConnectionFactory connections)
{
    /// <summary>模块不存在返回 null；存在则返回动作（含公式行）与校验规则。</summary>
    public async Task<ModuleBusinessConfigDto?> GetAsync(int moduleId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        if (!await ModuleExistsAsync(connection, moduleId, token))
        {
            return null;
        }

        var actions = new List<BusinessActionDto>();
        var actionIndex = new Dictionary<long, int>();
        await using (var actionCommand = new SqlCommand(
            """
            SELECT ACTION_ID,EVENT_CODE,SEQ,EFFECT_KEY,EFFECT_NAME,ENABLED,FAIL_MODE,
                   CONDITION_STRUCT,PARAM_STRUCT,REVERSE_STRUCT,REMARK,SOURCE_REF,
                   LABEL,CONFIRM_TAG
            FROM dbo.MODULE_BUSINESS_ACTION WITH (NOLOCK)
            WHERE M_IDX=@ModuleId
            ORDER BY EVENT_CODE,SEQ;
            """, connection))
        {
            actionCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await using var reader = await actionCommand.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var actionId = reader.GetInt64(0);
                var action = new BusinessActionDto(
                    reader.GetInt32(2),
                    GetString(reader, 1) ?? string.Empty,
                    GetString(reader, 3) ?? string.Empty,
                    GetString(reader, 4),
                    reader.GetBoolean(5),
                    GetString(reader, 6) ?? "BLOCK",
                    GetString(reader, 7),
                    GetString(reader, 8),
                    GetString(reader, 9),
                    GetString(reader, 10),
                    GetString(reader, 11),
                    new List<BusinessActionOpDto>(),
                    GetString(reader, 12),
                    reader.GetBoolean(13));
                actionIndex.Add(actionId, actions.Count);
                actions.Add(action);
            }
        }

        await using (var opCommand = new SqlCommand(
            """
            SELECT o.ACTION_ID,o.OP_SEQ,o.TARGET_TABLE,o.TARGET_FIELD,o.OP_CODE,o.SOURCE_SCOPE,
                   o.SOURCE_TABLE,o.SOURCE_FIELD,o.SOURCE_AGG,o.SOURCE_CONSTANT,
                   o.SOURCE_TERMS_STRUCT,o.MATCH_STRUCT,o.CONDITION_STRUCT,o.REMARK
            FROM dbo.MODULE_BUSINESS_ACTION_OP o WITH (NOLOCK)
            INNER JOIN dbo.MODULE_BUSINESS_ACTION a WITH (NOLOCK) ON a.ACTION_ID=o.ACTION_ID
            WHERE a.M_IDX=@ModuleId
            ORDER BY a.EVENT_CODE,a.SEQ,o.OP_SEQ;
            """, connection))
        {
            opCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await using var reader = await opCommand.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (!actionIndex.TryGetValue(reader.GetInt64(0), out var index))
                    continue;
                var op = new BusinessActionOpDto(
                    reader.GetInt32(1),
                    GetString(reader, 2) ?? string.Empty,
                    GetString(reader, 3) ?? string.Empty,
                    GetString(reader, 4) ?? string.Empty,
                    GetString(reader, 5) ?? string.Empty,
                    GetString(reader, 6),
                    GetString(reader, 7),
                    GetString(reader, 8),
                    GetString(reader, 9),
                    GetString(reader, 10),
                    GetString(reader, 11),
                    GetString(reader, 12),
                    GetString(reader, 13));
                var current = actions[index];
                actions[index] = current with { Ops = (current.Ops ?? []).Append(op).ToList() };
            }
        }

        var rules = new List<ModuleValidationRuleDto>();
        await using (var ruleCommand = new SqlCommand(
            """
            SELECT SEQ,STAGE,VALIDATION_KEY,ENABLED,PARAM_STRUCT,MESSAGE,REMARK,SOURCE_REF
            FROM dbo.MODULE_VALIDATION_RULE WITH (NOLOCK)
            WHERE M_IDX=@ModuleId
            ORDER BY STAGE,SEQ;
            """, connection))
        {
            ruleCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await using var reader = await ruleCommand.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                rules.Add(new ModuleValidationRuleDto(
                    reader.GetInt32(0),
                    GetString(reader, 1) ?? string.Empty,
                    GetString(reader, 2) ?? string.Empty,
                    reader.GetBoolean(3),
                    GetString(reader, 4),
                    GetString(reader, 5),
                    GetString(reader, 6),
                    GetString(reader, 7)));
            }
        }

        return new ModuleBusinessConfigDto(moduleId, actions, rules);
    }

    private static async Task<bool> ModuleExistsAsync(SqlConnection connection, int moduleId, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT CASE WHEN EXISTS(SELECT 1 FROM dbo.MODULES WHERE M_IDX=@ModuleId) THEN 1 ELSE 0 END;",
            connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        return Convert.ToInt32(await command.ExecuteScalarAsync(token)) == 1;
    }

    private static string? GetString(SqlDataReader reader, int index)
        => reader.IsDBNull(index) ? null : reader.GetString(index).Trim();
}
