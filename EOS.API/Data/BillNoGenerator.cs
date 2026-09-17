using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EOS.API.Data;

/// <summary>
/// 单据自动编号（发号器）。
///
/// 规则唯一来源：BILLKIND（模块 2304「单据性质设定」的主表）。
///   BILL_CODE    种类码（单别）
///   USED_BILL_NO 编码方式表达式：字头 + {日期令牌} + 尾随 0（0 的个数即流水宽度）
///   IS_AUTO      是否自动编号   IS_DEFAULT 是否默认单别
/// 表达式例：BJK{YYMM}0000 → 字头 BJK2608 + 4 位流水；CHPC00000 → 字头 CHPC + 5 位流水。
/// 日期令牌决定日期段：{YYYY}/{YYMM}/{YYMMDD}/... 分别按年/月/日切段，
/// 无日期令牌时日期段为空串，流水永不重置；起始值固定为 1（规则表无起始值字段）。
///
/// 取号不再扫描业务表，改为独立序列表 BILL_NO_SEQUENCE 按 (种类码, 日期段) 计数：
/// 单条 UPDATE ... OUTPUT 原子自增，并发取号必然互不相同；号码在保存事务内取得，
/// 事务回滚时号可作废（允许跳号，不允许重号）。
/// 表名/列名均为服务端常量，取值全部参数化。
/// </summary>
public static class BillNoGenerator
{
    private static readonly Regex TrailingZeros = new(@"0+$", RegexOptions.Compiled);
    private static readonly Regex DateToken = new(@"\{[^\{\}]*\}", RegexOptions.Compiled);

    /// <summary>表达式解析结果：字头、日期段、流水宽度。</summary>
    public sealed record Template(string Title, string Period, int Width);

    /// <summary>
    /// 解析单号表达式。例：BJK{YYMM}0000 + 2026-08 → ("BJK2608", "2608", 4)；
    /// {YYYY}000 → ("2026", "2026", 3)；CHPC00000 → ("CHPC", "", 5)。
    /// </summary>
    public static Template Parse(string expression, DateTime now)
    {
        var trailing = TrailingZeros.Match(expression).Value;
        var width = trailing.Length;
        var body = width > 0 ? expression[..(expression.Length - trailing.Length)] : expression;
        var period = new StringBuilder();
        var title = DateToken.Replace(body, match =>
        {
            var token = match.Value[1..^1];
            // Date token format: {YYMM} → yyMM, {YYYYMM} → yyyyMM, {YYMMDD} → yyMMdd, {YYYY} → yyyy
            token = token.Replace('Y', 'y').Replace('m', 'M').Replace('D', 'd');
            var rendered = now.ToString(token, CultureInfo.InvariantCulture);
            if (period.Length > 0) period.Append('|');
            period.Append(rendered);
            return rendered;
        });
        return new Template(title, period.ToString(), width);
    }

    /// <summary>
    /// 为模块生成新单号（事务内，原子自增）。返回 null 表示该模块未配置自动单号。
    /// </summary>
    public static Task<string?> GenerateAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int moduleId,
        CancellationToken token) => TakeAsync(connection, transaction, moduleId, consume: true, token);

    /// <summary>
    /// 预览下一个单号（不消耗流水）。用于新建表单的默认值，避免"打开新建页就烧号"。
    /// 与 GenerateAsync 之间若有并发保存，实际保存号可能大于预览号。
    /// </summary>
    public static Task<string?> PeekAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int moduleId,
        CancellationToken token) => TakeAsync(connection, transaction, moduleId, consume: false, token);

    /// <summary>
    /// 取模块默认单别（BILLKIND.IS_DEFAULT=1 的 BILL_CODE），无默认配置返回 null。
    /// </summary>
    public static async Task<string?> GetDefaultBillCodeAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int moduleId,
        CancellationToken token)
    {
        const string sql = """
            SELECT TOP 1 LTRIM(RTRIM(BILL_CODE)) AS BILL_CODE
            FROM dbo.BILLKIND WITH (NOLOCK)
            WHERE B_M_IDX=@ModuleId AND IS_DEFAULT=1
            ORDER BY BILL_CODE;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        var value = await command.ExecuteScalarAsync(token);
        return value as string;
    }

    /// <summary>
    /// 模块是否配置自动单号（BILLKIND 存在 IS_DEFAULT=1 且 IS_AUTO=1 的单别）。
    /// 判定只看 BILLKIND 规则本身，与模块是否登记保存后处理无关。
    /// </summary>
    public static async Task<bool> HasAutoBillNoAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int moduleId,
        CancellationToken token)
    {
        const string sql = """
            SELECT TOP 1 1 FROM dbo.BILLKIND WITH (NOLOCK)
            WHERE B_M_IDX=@ModuleId AND IS_DEFAULT=1 AND IS_AUTO=1;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        return await command.ExecuteScalarAsync(token) is not null;
    }

    private static async Task<string?> TakeAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int moduleId,
        bool consume,
        CancellationToken token)
    {
        var (billCode, expression) = await LoadRuleAsync(connection, transaction, moduleId, token);
        if (billCode is null || expression is null) return null;

        var template = Parse(expression, DateTime.Now);
        if (template.Width == 0) return template.Title;

        var serial = consume
            ? await TakeSerialAsync(connection, transaction, billCode, template, token)
            : await PeekSerialAsync(connection, transaction, billCode, template, token);
        return template.Title + serial.ToString(CultureInfo.InvariantCulture).PadLeft(template.Width, '0');
    }

    /// <summary>读取模块默认单别及其编码方式表达式。</summary>
    private static async Task<(string? BillCode, string? Expression)> LoadRuleAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int moduleId,
        CancellationToken token)
    {
        const string sql = """
            SELECT TOP 1 LTRIM(RTRIM(BILL_CODE)) AS BILL_CODE, LTRIM(RTRIM(USED_BILL_NO)) AS USED_BILL_NO
            FROM dbo.BILLKIND WITH (NOLOCK)
            WHERE B_M_IDX=@ModuleId AND IS_DEFAULT=1 AND IS_AUTO=1
            ORDER BY BILL_CODE;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return (null, null);
        var billCode = reader.IsDBNull(0) ? null : reader.GetString(0);
        var expression = reader.IsDBNull(1) ? null : reader.GetString(1);
        return (string.IsNullOrWhiteSpace(billCode) ? null : billCode.Trim(),
                string.IsNullOrWhiteSpace(expression) ? null : expression.Trim());
    }

    private static async Task<long> PeekSerialAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string billCode,
        Template template,
        CancellationToken token)
    {
        const string sql = """
            SELECT CURRENT_NO FROM dbo.BILL_NO_SEQUENCE WITH (NOLOCK)
            WHERE BILL_CODE=@BillCode AND PERIOD_KEY=@Period;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@BillCode", SqlDbType.NVarChar, 20).Value = billCode;
        command.Parameters.Add("@Period", SqlDbType.NVarChar, 16).Value = template.Period;
        var current = await command.ExecuteScalarAsync(token);
        return current is long value ? value + 1 : 1;
    }

    /// <summary>
    /// 原子取号：单条 UPDATE ... OUTPUT 自增并返回新值；计数器不存在时先建行再从 1 开始。
    /// 字头变化（单号规则被改）时按旧系统语义重置为 1。
    /// 并发建行会撞主键，捕获后重试；重试耗尽仍取不到号即抛异常，绝不静默返回重复号。
    /// </summary>
    private static async Task<long> TakeSerialAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string billCode,
        Template template,
        CancellationToken token)
    {
        const string sql = """
            DECLARE @Taken TABLE (SEQ BIGINT NOT NULL);
            DECLARE @Tries INT = 0;
            WHILE @Tries < 5 AND NOT EXISTS (SELECT 1 FROM @Taken)
            BEGIN
                SET @Tries = @Tries + 1;
                UPDATE dbo.BILL_NO_SEQUENCE
                   SET CURRENT_NO = CASE WHEN TITLE <> @Title THEN 1 ELSE CURRENT_NO + 1 END,
                       TITLE = @Title,
                       LAST_ISSUED_AT = SYSUTCDATETIME()
                OUTPUT inserted.CURRENT_NO INTO @Taken (SEQ)
                 WHERE BILL_CODE = @BillCode AND PERIOD_KEY = @Period;

                IF NOT EXISTS (SELECT 1 FROM @Taken)
                BEGIN
                    BEGIN TRY
                        INSERT dbo.BILL_NO_SEQUENCE (BILL_CODE, PERIOD_KEY, TITLE, CURRENT_NO, LAST_ISSUED_AT)
                        VALUES (@BillCode, @Period, @Title, 1, SYSUTCDATETIME());
                        INSERT @Taken (SEQ) VALUES (1);
                    END TRY
                    BEGIN CATCH
                        IF ERROR_NUMBER() NOT IN (2627, 2601) THROW;
                        DELETE FROM @Taken;
                    END CATCH
                END
            END
            SELECT TOP 1 SEQ FROM @Taken;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@BillCode", SqlDbType.NVarChar, 20).Value = billCode;
        command.Parameters.Add("@Period", SqlDbType.NVarChar, 16).Value = template.Period;
        command.Parameters.Add("@Title", SqlDbType.NVarChar, 64).Value = template.Title;
        var taken = await command.ExecuteScalarAsync(token);
        if (taken is not long serial)
        {
            throw new InvalidOperationException(
                $"单据自动编号取号失败（单别 {billCode}，日期段 {template.Period}）。");
        }
        return serial;
    }
}
