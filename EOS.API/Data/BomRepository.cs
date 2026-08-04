using System.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

public sealed class BomRepository(IConfiguration configuration)
{
    private const int MaximumLimit = 200;

    public async Task<IReadOnlyList<BomMasterRow>> SearchAsync(
        string searchField,
        string keyword,
        int limit,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, MaximumLimit);
        keyword = keyword.Trim();
        var filter = GetFilter(searchField, keyword);
        var sql = $"""
            SELECT TOP (@Limit)
                   RTRIM(m.PRO_NO) AS PRO_NO,
                   RTRIM(ISNULL(pm.PRO_NAME, N'')) AS PRO_NAME,
                   RTRIM(ISNULL(pm.PRO_SPEC, N'')) AS PRO_SPEC,
                   ISNULL(m.ATTENTION, N'') AS ATTENTION,
                   ISNULL(m.CONFIRM_TAG, 0) AS CONFIRM_TAG,
                   RTRIM(ISNULL(m.CONFIRM_PERSON, N'')) AS CONFIRM_PERSON,
                   m.CONFIRM_DATE,
                   RTRIM(ISNULL(m.LAST_UPDATE_BY, N'')) AS LAST_UPDATE_BY,
                   m.LAST_UPDATE_DATE,
                   ISNULL(pm.P_WIDTH, 0) + ISNULL(pm.P_LENGTH, 0)
                       - ISNULL(m.P_WIDTH_OLD, 0) - ISNULL(m.P_LENGTH_OLD, 0) AS PARAMETER_DIFFERENCE,
                   ISNULL(m.REMARK, N'') AS REMARK
            FROM dbo.BOM_STRU_M AS m
            LEFT JOIN dbo.PRODUCT AS pm ON pm.PRO_NO = m.PRO_NO
            WHERE 1 = 1 {filter}
            ORDER BY m.PRO_NO ASC;
            """;

        await using var connection = CreateConnection();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Limit", SqlDbType.Int).Value = limit;
        if (keyword.Length > 0)
        {
            command.Parameters.Add("@Keyword", SqlDbType.NVarChar, 202).Value = $"%{keyword}%";
        }

        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<BomMasterRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new BomMasterRow(
                reader.GetString("PRO_NO"),
                reader.GetString("PRO_NAME"),
                reader.GetString("PRO_SPEC"),
                reader.GetString("ATTENTION"),
                reader.GetBoolean("CONFIRM_TAG"),
                reader.GetString("CONFIRM_PERSON"),
                reader.GetNullableDateTime("CONFIRM_DATE"),
                reader.GetString("LAST_UPDATE_BY"),
                reader.GetNullableDateTime("LAST_UPDATE_DATE"),
                reader.GetDouble("PARAMETER_DIFFERENCE"),
                reader.GetString("REMARK")));
        }

        return rows;
    }

    public async Task<IReadOnlyList<BomDetailRow>> GetDetailsAsync(
        string proNo,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT d.SERIAL_NO,
                   RTRIM(ISNULL(d.ELEMENT_PRO_NO, N'')) AS ELEMENT_PRO_NO,
                   RTRIM(ISNULL(p.PRO_NAME, N'')) AS ELEMENT_PRO_NAME,
                   RTRIM(ISNULL(p.PRO_SPEC, N'')) AS ELEMENT_PRO_SPEC,
                   d.ELEMENT_QTY,
                   d.LOST_RATE,
                   p.QTY AS DEPOT_QTY,
                   p.MRP_QTY,
                   RTRIM(ISNULL(p.UNIT_ID, N'')) AS UNIT_ID,
                   RTRIM(ISNULL(p.STUFF_ID, N'')) AS STUFF_ID,
                   ISNULL(d.REMARK, N'') AS REMARK
            FROM dbo.BOM_STRU_D AS d
            LEFT JOIN dbo.PRODUCT AS p ON p.PRO_NO = d.ELEMENT_PRO_NO
            WHERE d.PRO_NO = @ProNo
            ORDER BY d.SERIAL_NO ASC;
            """;

        await using var connection = CreateConnection();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ProNo", SqlDbType.NChar, 30).Value = proNo.Trim();
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<BomDetailRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new BomDetailRow(
                reader.GetInt16("SERIAL_NO"),
                reader.GetString("ELEMENT_PRO_NO"),
                reader.GetString("ELEMENT_PRO_NAME"),
                reader.GetString("ELEMENT_PRO_SPEC"),
                reader.GetNullableDouble("ELEMENT_QTY"),
                reader.GetNullableDouble("LOST_RATE"),
                reader.GetNullableDouble("DEPOT_QTY"),
                reader.GetNullableDouble("MRP_QTY"),
                reader.GetString("UNIT_ID"),
                reader.GetString("STUFF_ID"),
                reader.GetString("REMARK")));
        }

        return rows;
    }

    private static string GetFilter(string searchField, string keyword)
    {
        if (keyword.Length == 0)
        {
            return string.Empty;
        }

        return searchField switch
        {
            "proName" => "AND pm.PRO_NAME LIKE @Keyword",
            "proSpec" => "AND pm.PRO_SPEC LIKE @Keyword",
            "elementProNo" => """
                AND EXISTS (
                    SELECT 1
                    FROM dbo.BOM_STRU_D AS dx
                    WHERE dx.PRO_NO = m.PRO_NO
                      AND dx.ELEMENT_PRO_NO LIKE @Keyword
                )
                """,
            _ => "AND m.PRO_NO LIKE @Keyword"
        };
    }

    private SqlConnection CreateConnection()
    {
        var connectionString = configuration.GetConnectionString("ErpDatabase");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:ErpDatabase 未配置。");
        }

        return new SqlConnection(connectionString);
    }
}

internal static class SqlDataReaderExtensions
{
    public static string GetString(this SqlDataReader reader, string name) =>
        reader.GetString(reader.GetOrdinal(name));

    public static bool GetBoolean(this SqlDataReader reader, string name) =>
        reader.GetBoolean(reader.GetOrdinal(name));

    public static short GetInt16(this SqlDataReader reader, string name) =>
        reader.GetInt16(reader.GetOrdinal(name));

    public static double GetDouble(this SqlDataReader reader, string name) =>
        reader.GetDouble(reader.GetOrdinal(name));

    public static DateTime? GetNullableDateTime(this SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
    }

    public static double? GetNullableDouble(this SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }
}
