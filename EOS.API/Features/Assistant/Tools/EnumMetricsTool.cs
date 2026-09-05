using System.Data;
using System.Text;
using System.Text.Json;
using EOS.API.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// enum_metrics：返回系统全部已定义的度量口径清单（指标名/定义/来源表/维度/业务域）。
/// 供 Agent 回答「销售额是什么口径」「有哪些可查询的指标」等，复用最小语义层。
/// 只读系统元数据（REPORT_METRIC），不取业务数据、不执行指标计算。
/// </summary>
public sealed class EnumMetricsTool(DbConnectionFactory connections) : AssistantToolBase
{
    public const string ToolName = "enum_metrics";

    public override string Name => ToolName;

    public override AssistantToolRisk Risk => AssistantToolRisk.Read;

    public override string Description =>
        "查询系统中已定义的度量口径清单（指标名、口径定义、来源表、可用维度、业务域）。"
        + "当用户询问某业务指标（如销售额、库存数量）的准确定义时使用本工具。";

    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "domain": { "type": "string", "description": "可选：按业务域过滤（销售/采购/库存/生产/财务/人事）" },
            "keyword": { "type": "string", "description": "可选：按指标名/描述关键字过滤" }
          }
        }
        """;

    public override async Task<ToolExecutionResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken token)
    {
        var domain = arguments.GetStringArg("domain");
        var keyword = arguments.GetStringArg("keyword");

        var sql = """
            SELECT METRIC_ID, METRIC_NAME, DEFINITION, SOURCE_TABLE,
                   LTRIM(RTRIM(ISNULL(DIMENSION_KEYS,''))) AS DIMENSION_KEYS,
                   LTRIM(RTRIM(ISNULL(DOMAIN,''))) AS DOMAIN,
                   VERSION, LTRIM(RTRIM(ISNULL(DESCRIPTION,''))) AS DESCRIPTION,
                   LTRIM(RTRIM(ISNULL(CONFIRM_STATUS,'CANDIDATE'))) AS CONFIRM_STATUS
            FROM dbo.REPORT_METRIC WITH (NOLOCK)
            WHERE (@Domain = '' OR LTRIM(RTRIM(DOMAIN)) = @Domain)
              AND (@Keyword = '' OR METRIC_NAME LIKE '%' + @Keyword + '%'
                   OR METRIC_ID LIKE '%' + @Keyword + '%' OR DESCRIPTION LIKE '%' + @Keyword + '%')
            ORDER BY DOMAIN, METRIC_ID;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Domain", SqlDbType.NVarChar, 50).Value = domain ?? string.Empty;
        command.Parameters.Add("@Keyword", SqlDbType.NVarChar, 100).Value = keyword ?? string.Empty;
        await using var reader = await command.ExecuteReaderAsync(token);
        var sb = new StringBuilder();
        var count = 0;
        while (await reader.ReadAsync(token))
        {
            count++;
            sb.Append(reader.GetString(0)).Append(" = ").Append(reader.GetString(1))
              .Append("（定义：").Append(reader.GetString(2)).Append('）');
            if (!reader.IsDBNull(3)) sb.Append("　来源表：").Append(reader.GetString(3));
            if (!reader.IsDBNull(4) && !string.IsNullOrWhiteSpace(reader.GetString(4)))
                sb.Append("　维度：").Append(reader.GetString(4));
            if (!reader.IsDBNull(5) && !string.IsNullOrWhiteSpace(reader.GetString(5)))
                sb.Append("　域：").Append(reader.GetString(5));
            if (!reader.IsDBNull(7) && !string.IsNullOrWhiteSpace(reader.GetString(7)))
                sb.Append("　说明：").Append(reader.GetString(7));
            // Only business-confirmed metrics are computable via resolve_metric;
            // candidates are listed for definition lookup but refuse computation.
            var confirmed = string.Equals(reader.GetString(8), "CONFIRMED", StringComparison.OrdinalIgnoreCase);
            sb.Append(confirmed ? "　[已确认，可用 resolve_metric 计算]" : "　[候选口径，业务未确认，不可计算]");
            sb.AppendLine();
        }
        if (count == 0) return ToolExecutionResult.Success("未找到匹配的度量口径。");
        return ToolExecutionResult.Success($"共 {count} 条口径：\n" + sb.ToString().TrimEnd());
    }
}