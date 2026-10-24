using System.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Reports;

/// <summary>
/// 公司表数据源：模块 110101（公司基本资料）的主表 <c>COMPANY</c>，打印/报表的第三张数据表。
///
/// <para>
/// 一张报表涉及三张表：主表（单据表头）、明细表、公司表。主表/明细表随单据走，公司表是
/// **开单主体**的配置，由报表显式绑定（<c>REPORT.COMPANY_ID</c>，迁移 449；存量与新建报表一律
/// 落系统默认公司编号 <c>DEFAULT</c>）。这里**不做"猜一个公司"的兜底**：编号为空或指向不存在的
/// 记录时返回 null，由渲染侧按"公司字段全空"处理——数据错了要看得见，不能被代码悄悄补齐。
/// </para>
///
/// <para>
/// 字段白名单与主表/明细表同一口径：只认 <c>FIELDS</c> 登记且物理存在的列
/// （<see cref="WorkbenchSql.GetPhysicalColumnsAsync"/>），保存校验与渲染共用这一份清单。
/// 表名与列名都来自这份服务端白名单，值全部参数化。
/// </para>
/// </summary>
public static class CompanyPrintData
{
    /// <summary>公司基本资料模块（110101）的主表。</summary>
    public const string Table = "COMPANY";

    /// <summary>系统默认公司编号（公司主档唯一记录上的编号，见迁移 096）。</summary>
    public const string DefaultCompanyId = "DEFAULT";

    /// <summary>公司表可引用字段（FIELDS 登记 ∩ 物理列，按字段维护顺序）。</summary>
    public static async Task<IReadOnlyList<ReportContractColumn>> ReadFieldsAsync(
        SqlConnection connection, CancellationToken token)
    {
        var physical = await WorkbenchSql.GetPhysicalColumnsAsync(connection, null, Table, token);
        const string sql = """
            SELECT LTRIM(RTRIM(f.F_ID)),
                   COALESCE(NULLIF(LTRIM(RTRIM(f.F_DESC)),''), LTRIM(RTRIM(f.F_ID))),
                   LTRIM(RTRIM(ISNULL(f.F_TYPE,'nvarchar')))
            FROM dbo.FIELDS f
            WHERE f.T_ID=@Table AND COALESCE(f.IS_VISIBLE,1)=1 AND COALESCE(f.IS_VIRTUAL,0)=0
            ORDER BY COALESCE(f.VERIFY_INDEX,999), f.F_ID;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = Table;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<ReportContractColumn>();
        while (await reader.ReadAsync(token))
        {
            var key = reader.GetString(0);
            if (!physical.Contains(key)) continue;
            result.Add(new ReportContractColumn(key, reader.GetString(1), reader.GetString(2)));
        }
        return result;
    }

    /// <summary>公司记录下拉（报表定义绑定公司、设计器展示当前公司）。</summary>
    public static async Task<IReadOnlyList<CompanyOption>> ReadOptionsAsync(
        SqlConnection connection, CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(COMPANY_ID)),
                   LTRIM(RTRIM(ISNULL(NAME_CN,''))),
                   LTRIM(RTRIM(ISNULL(NAME_EN,'')))
            FROM dbo.COMPANY
            ORDER BY COMPANY_ID;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<CompanyOption>();
        while (await reader.ReadAsync(token))
        {
            var name = reader.GetString(1);
            result.Add(new CompanyOption(
                reader.GetString(0),
                name.Length == 0 ? reader.GetString(0) : name,
                reader.GetString(2)));
        }
        return result;
    }

    /// <summary>
    /// 按**显式**公司编号读取记录：编号为空或记录不存在时返回 null（不回落到别的公司）。
    /// </summary>
    public static async Task<CompanyProfile?> ReadAsync(
        SqlConnection connection, string? companyId, CancellationToken token)
    {
        var id = companyId?.Trim();
        if (string.IsNullOrEmpty(id)) return null;

        var fields = await ReadFieldsAsync(connection, token);
        if (fields.Count == 0)
        {
            // FIELDS 未登记时至少保证三根支柱可用；列名是常量白名单，不含外部输入。
            fields =
            [
                new ReportContractColumn("COMPANY_ID", "公司编号", "string"),
                new ReportContractColumn("NAME_CN", "中文名", "string"),
                new ReportContractColumn("NAME_EN", "英文名", "string"),
            ];
        }
        return await ReadRowAsync(connection, fields, id, token);
    }

    /// <summary>
    /// 设计器样例预览用的公司编号：取该模块**默认报表**的显式绑定；没有默认报表时取该模块第一张报表。
    /// 读到的值仍然是报表上明确写着的公司编号（不按"库存里只有一家公司"去猜）。
    /// </summary>
    public static async Task<string?> ReadPreviewCompanyIdAsync(
        SqlConnection connection, int moduleId, CancellationToken token)
    {
        const string sql = """
            SELECT TOP 1 LTRIM(RTRIM(COMPANY_ID))
            FROM dbo.REPORT
            WHERE M_IDX=@ModuleId
            ORDER BY CASE WHEN IS_DEFAULT=1 THEN 0 ELSE 1 END, REPORT_ID;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull || string.IsNullOrWhiteSpace(Convert.ToString(value))
            ? null
            : Convert.ToString(value)!.Trim();
    }

    private static async Task<CompanyProfile?> ReadRowAsync(
        SqlConnection connection,
        IReadOnlyList<ReportContractColumn> fields,
        string companyId,
        CancellationToken token)
    {
        var columns = string.Join(',', fields.Select(field => $"[{field.Key}]"));
        var sql = $"SELECT TOP 1 {columns} FROM dbo.[{Table}] WHERE COMPANY_ID=@CompanyId;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@CompanyId", SqlDbType.NVarChar, 20).Value = companyId;
        var rows = await WorkbenchSql.ReadRowsAsync(command, token);
        if (rows.Count == 0) return null;
        var row = rows[0];
        var id = row.TryGetValue("COMPANY_ID", out var rawId) ? Convert.ToString(rawId)?.Trim() : null;
        return new CompanyProfile(
            id ?? string.Empty,
            row,
            Text(row, "NAME_CN"),
            Text(row, "NAME_EN"));
    }

    private static string? Text(IReadOnlyDictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value)?.Trim()
            : null;
}
