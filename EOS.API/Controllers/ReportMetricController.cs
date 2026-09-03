using System.Data;
using EOS.API.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace EOS.API.Controllers;

/// <summary>
/// 最小语义层口径清单：度量口径定义一次、机器可读、多处消费。
/// 供报表与 Agent 共同消费；不自建查询引擎、不引入外部语义层产品。
/// 度量定义本身属开发态（§1），随 DbUp 迁移版本化落地。
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/report-metrics")]
public sealed class ReportMetricController(DbConnectionFactory connections) : ControllerBase
{
    /// <summary>口径清单：列出全部已定义的度量口径。</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT METRIC_ID, METRIC_NAME, DEFINITION, SOURCE_TABLE,
                   LTRIM(RTRIM(ISNULL(DIMENSION_KEYS,''))) AS DIMENSION_KEYS,
                   LTRIM(RTRIM(ISNULL(DOMAIN,''))) AS DOMAIN,
                   VERSION, LTRIM(RTRIM(ISNULL(DESCRIPTION,''))) AS DESCRIPTION
            FROM dbo.REPORT_METRIC WITH (NOLOCK)
            ORDER BY DOMAIN, METRIC_ID;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<object>();
        while (await reader.ReadAsync(token))
        {
            result.Add(new
            {
                metricId = reader.GetString(0),
                metricName = reader.GetString(1),
                definition = reader.GetString(2),
                sourceTable = reader.IsDBNull(3) ? null : reader.GetString(3),
                dimensionKeys = reader.IsDBNull(4) || string.IsNullOrWhiteSpace(reader.GetString(4))
                    ? Array.Empty<string>()
                    : reader.GetString(4).Split(',').Select(k => k.Trim()).ToArray(),
                domain = reader.IsDBNull(5) || string.IsNullOrWhiteSpace(reader.GetString(5)) ? null : reader.GetString(5),
                version = reader.GetInt32(6),
                description = reader.IsDBNull(7) || string.IsNullOrWhiteSpace(reader.GetString(7)) ? null : reader.GetString(7),
            });
        }
        return Ok(result);
    }
}