using EOS.API.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Features.Assistant.Metrics;

public interface IMetricExecutor
{
    /// <summary>Runs a compiled metric plan and returns its scalar value; null = no rows.</summary>
    Task<decimal?> ExecuteAsync(MetricPlan plan, CancellationToken token);
}

public sealed class MetricExecutor(DbConnectionFactory connections) : IMetricExecutor
{
    public async Task<decimal?> ExecuteAsync(MetricPlan plan, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(plan.Sql, connection);
        foreach (var parameter in plan.Parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        var result = await command.ExecuteScalarAsync(token);
        return result is null or DBNull ? null : Convert.ToDecimal(result);
    }
}
