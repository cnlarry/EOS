using System.Data;
using EOS.API.Data;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace EOS.API.Controllers;

/// <summary>
/// 跨表明细查询（14996 待生产订单明细 / 14998 客户逾期未对帐 / 170297 厂商逾期未对账）。
/// 这些模块的 MODULES.FILTER 引用父表/其它表（如 COP_SEND_M、PRODUCT、BOM_STRU_M），
/// 超出通用工作台单表查询能力，故以受控只读页承接：SQL 全部为服务端常量（表/列名
/// 白名单），仅日期边界等值参数化；权限门为模块读权限（与工作台一致）。
/// 说明：本页为只读列表，未附加 EXEC_TAG/DATA_FILTER 数据范围（当前库无生效记录，
/// 若未来配置 B/C/D/E 范围需按工作台同等机制接入，见 tech-debt）。
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/detail-query/{moduleId:int}")]
public sealed class DetailQueryController(
    DbConnectionFactory connections,
    ModuleRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
    private sealed record DetailColumn(string Key, string Label, string DataType, string? DisplayFormat = null);
    private sealed record QuerySpec(
        int ModuleId,
        string Title,
        IReadOnlyList<DetailColumn> Columns,
        string SelectSql,
        string FromSql,
        string OrderBy,
        Action<SqlCommand> Bind);

    private static readonly IReadOnlyDictionary<int, QuerySpec> Specs = new Dictionary<int, QuerySpec>
    {
        [ModuleIds.DetailQueryOrder] = new(ModuleIds.DetailQueryOrder, "待生产订单明细",
            [
                new("ORDER_TYPE", "单别", "nvarchar"),
                new("ORDER_NO", "订单号", "nvarchar"),
                new("SERIAL_NO", "项次", "int"),
                new("PRO_NO", "料号", "nvarchar"),
                new("PRO_NAME", "品名", "nvarchar"),
                new("PLAN_QTY", "计划数量", "decimal"),
                new("FINISHED_PLAN_QTY", "已生产数量", "decimal"),
                new("UNFINISHED_QTY", "未生产数量", "decimal"),
                new("ORDER_DATE", "订单日期", "datetime"),
                new("CLIENT_ID", "客户", "nvarchar"),
            ],
            "[m].[ORDER_TYPE],[m].[ORDER_NO],[d].[SERIAL_NO],[d].[PRO_NO],[p].[PRO_NAME]," +
            "[d].[PLAN_QTY],[d].[FINISHED_PLAN_QTY],[d].[PLAN_QTY]-[d].[FINISHED_PLAN_QTY] AS [UNFINISHED_QTY]," +
            "[m].[ORDER_DATE],[m].[CLIENT_ID]",
            """
            dbo.COP_ORDER_D d WITH (NOLOCK)
            INNER JOIN dbo.COP_ORDER_M m WITH (NOLOCK) ON m.ORDER_TYPE=d.ORDER_TYPE AND m.ORDER_NO=d.ORDER_NO
            INNER JOIN dbo.PRODUCT p WITH (NOLOCK) ON p.PRO_NO=d.PRO_NO
            WHERE d.FINISHED_TAG=0 AND d.PLAN_QTY>d.FINISHED_PLAN_QTY
              AND EXISTS (SELECT 1 FROM dbo.BOM_STRU_M b WITH (NOLOCK) WHERE b.PRO_NO=d.PRO_NO)
              AND p.MAIN_SOURCE='2' AND m.ORDER_DATE>=@dateFrom
            """,
            "m.ORDER_DATE DESC, m.ORDER_TYPE, m.ORDER_NO, d.SERIAL_NO",
            command => command.Parameters.Add("@dateFrom", SqlDbType.DateTime).Value = new DateTime(2014, 10, 1)),
        [ModuleIds.DetailQuerySend] = new(ModuleIds.DetailQuerySend, "客户逾期未对帐",
            [
                new("SEND_TYPE", "单别", "nvarchar"),
                new("SEND_NO", "送货单号", "nvarchar"),
                new("SERIAL_NO", "项次", "int"),
                new("SEND_DATE", "送货日期", "datetime"),
                new("CLIENT_ID", "客户编号", "nvarchar"),
                new("CLIENT_NAME", "客户名称", "nvarchar"),
                new("PRO_NO", "料号", "nvarchar"),
                new("QTY", "数量", "decimal"),
                new("FINISHED_QTY", "已对帐数量", "decimal"),
                new("UNFINISHED_QTY", "未对帐数量", "decimal"),
                new("REMARK", "备注", "nvarchar"),
            ],
            "[m].[SEND_TYPE],[m].[SEND_NO],[d].[SERIAL_NO],[m].[SEND_DATE],[m].[CLIENT_ID],[m].[CLIENT_NAME]," +
            "[d].[PRO_NO],[d].[QTY],[d].[FINISHED_QTY],[d].[QTY]-[d].[FINISHED_QTY] AS [UNFINISHED_QTY],[d].[REMARK]",
            """
            dbo.COP_SEND_D d WITH (NOLOCK)
            INNER JOIN dbo.COP_SEND_M m WITH (NOLOCK) ON m.SEND_TYPE=d.SEND_TYPE AND m.SEND_NO=d.SEND_NO
            WHERE d.FINISHED_TAG=0 AND m.FINISHED_TAG=0 AND d.QTY-d.FINISHED_QTY>0 AND m.SEND_DATE<@dateBoundary
            """,
            "m.SEND_DATE DESC, m.SEND_TYPE, m.SEND_NO, d.SERIAL_NO",
            command => command.Parameters.Add("@dateBoundary", SqlDbType.DateTime).Value = LastMonth26()),
        [ModuleIds.DetailQueryReceive] = new(ModuleIds.DetailQueryReceive, "厂商逾期未对账",
            [
                new("RECEIVE_TYPE", "单别", "nvarchar"),
                new("RECEIVE_NO", "收料单号", "nvarchar"),
                new("SERIAL_NO", "项次", "int"),
                new("RECEIVE_DATE", "收料日期", "datetime"),
                new("SUPPLIER_ID", "厂商编号", "nvarchar"),
                new("PRO_NO", "料号", "nvarchar"),
                new("QTY", "数量", "decimal"),
                new("FINISHED_QTY", "已对帐数量", "decimal"),
                new("UNFINISHED_QTY", "未对帐数量", "decimal"),
                new("REMARK", "备注", "nvarchar"),
            ],
            "[m].[RECEIVE_TYPE],[m].[RECEIVE_NO],[d].[SERIAL_NO],[m].[RECEIVE_DATE],[m].[SUPPLIER_ID]," +
            "[d].[PRO_NO],[d].[QTY],[d].[FINISHED_QTY],[d].[QTY]-[d].[FINISHED_QTY] AS [UNFINISHED_QTY],[d].[REMARK]",
            """
            dbo.PUR_RECEIVE_D d WITH (NOLOCK)
            INNER JOIN dbo.PUR_RECEIVE_M m WITH (NOLOCK) ON m.RECEIVE_TYPE=d.RECEIVE_TYPE AND m.RECEIVE_NO=d.RECEIVE_NO
            WHERE d.FINISHED_TAG=0 AND m.FINISHED_TAG=0 AND d.QTY-d.FINISHED_QTY>0 AND m.RECEIVE_DATE<@dateBoundary
            """,
            "m.RECEIVE_DATE DESC, m.RECEIVE_TYPE, m.RECEIVE_NO, d.SERIAL_NO",
            command => command.Parameters.Add("@dateBoundary", SqlDbType.DateTime).Value = LastMonth26()),
    };

    /// <summary>convert(varchar(7),dateadd(month,-1,getdate()),120)+'-26' ：上月 26 日。</summary>
    private static DateTime LastMonth26()
    {
        var now = DateTime.Now;
        return new DateTime(now.Year, now.Month, 26).AddMonths(-1);
    }

    [HttpGet]
    public async Task<IActionResult> Query(int moduleId, CancellationToken token, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        if (!Specs.TryGetValue(moduleId, out var spec)) return NotFound();
        if (!(await rightsRepository.GetAsync(userContext.UserId, moduleId, token)).CanBrowse) return Forbid();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 100);
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand();
        command.Connection = connection;
        spec.Bind(command);
        command.CommandText =
            $"SELECT {spec.SelectSql} FROM {spec.FromSql} ORDER BY {spec.OrderBy} OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY; " +
            $"SELECT COUNT_BIG(1) FROM {spec.FromSql};";
        command.Parameters.Add("@Offset", SqlDbType.Int).Value = (page - 1) * pageSize;
        command.Parameters.Add("@PageSize", SqlDbType.Int).Value = pageSize;
        var formats = await ReadDisplayFormatsAsync(connection, spec, token);
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = await WorkbenchSql.ReadRowsAsync(reader, token);
        await reader.NextResultAsync(token);
        await reader.ReadAsync(token);
        var total = Convert.ToInt32(reader.GetInt64(0));
        return Ok(new
        {
            spec.ModuleId,
            spec.Title,
            Columns = spec.Columns.Select(column => new { column.Key, column.Label, column.DataType, DisplayFormat = formats.GetValueOrDefault(column.Key) }),
            Rows = rows,
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    /// <summary>从 FIELDS 读取明细查询列的 DISPLAY_FORMAT（表/字段均为服务端常量，值参数化）。</summary>
    private static async Task<Dictionary<string, string>> ReadDisplayFormatsAsync(
        SqlConnection connection,
        QuerySpec spec,
        CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(F_ID)),LTRIM(RTRIM(ISNULL(DISPLAY_FORMAT,'')))
            FROM dbo.FIELDS WITH (NOLOCK)
            WHERE T_ID=@Table AND F_ID IN ({0});
            """;
        var keys = spec.Columns.Select(column => column.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (keys.Count == 0) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parameters = keys.Select((_, index) => $"@f{index}").ToList();
        await using var command = new SqlCommand(string.Format(sql, string.Join(',', parameters)), connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = spec.ModuleId switch
        {
            ModuleIds.DetailQueryOrder => "COP_ORDER_D",
            ModuleIds.DetailQuerySend => "COP_SEND_D",
            ModuleIds.DetailQueryReceive => "PUR_RECEIVE_D",
            _ => "COP_ORDER_D",
        };
        for (var i = 0; i < keys.Count; i++)
            command.Parameters.Add(parameters[i], SqlDbType.NVarChar, 100).Value = keys[i];
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var format = reader.GetString(1);
            if (!string.IsNullOrWhiteSpace(format))
                result[reader.GetString(0).Trim()] = format.Trim();
        }
        return result;
    }
}
