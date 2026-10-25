using System.Data;

using Microsoft.Data.SqlClient;

using EOS.API.Models;

namespace EOS.API.Data.Metadata;

/// <summary>
/// `FIELDS` 字段清单的**唯一读取实现**：打印、查询中心、权限矩阵三处此前各写一份同形 SQL
/// （挑列、按 `VERIFY_INDEX` 排序、只留物理列、按成本/保密/禁止字段过滤），
/// 返回类型也各定义一份——同一事实有三处实现，改一处就会与另两处漂移。
///
/// 各调用方的差异全部由 <see cref="FieldReadOptions"/> 表达（是否只取可见、是否排除虚拟、
/// 是否要求物理列、能否看成本/保密、禁止字段集合、排序口径），**判定逻辑只此一份**；
/// 各自的返回类型由调用方在这里的投影上再 `Select` 成型——返回类型可以不同，
/// "哪些字段可以给这个人看"不能不同。
/// </summary>
internal static class FieldMetadataReader
{
    /// <summary>一条字段元数据：`FIELDS` 的只读投影，含权限相关标记。</summary>
    internal sealed record FieldMetadata(
        string Key,
        string Label,
        string DataType,
        int DisplayLength,
        string? Align,
        string? HeaderAlign,
        string? DisplayFormat,
        string? NumericRole,
        bool IsCost,
        bool IsSecrecy,
        bool IsVisible,
        bool IsVirtual);

    /// <summary>
    /// 读取口径。默认值取"最宽"：不过滤可见性/虚拟/物理（调用方按需开），
    /// 成本与保密字段默认**不可见**（fail-closed：忘了传就是把敏感字段挡在外面，而不是放出去）。
    /// </summary>
    internal sealed record FieldReadOptions
    {
        /// <summary>只取 `IS_VISIBLE=1`（权限矩阵、打印都只处理可见字段）。</summary>
        public bool VisibleOnly { get; init; }

        /// <summary>排除 `IS_VIRTUAL=1`（虚拟列不是物理列，不能进 SQL 投影）。</summary>
        public bool DropVirtual { get; init; }

        /// <summary>只留真有物理列（表/视图）的字段：元数据登记了但列不存在时不能渲染或查询。</summary>
        public bool PhysicalOnly { get; init; }

        public bool CanViewCost { get; init; }
        public bool CanViewSecrecy { get; init; }

        /// <summary>字段级禁止集合（"禁看"），按字段号大小写不敏感比对。</summary>
        public IReadOnlySet<string>? DeniedFields { get; init; }

        /// <summary>按 `VERIFY_INDEX` 再按字段号排序（默认）；权限矩阵只要字段号顺序。</summary>
        public bool OrderByVerifyIndex { get; init; } = true;
    }

    internal static async Task<IReadOnlyList<FieldMetadata>> ReadAsync(
        SqlConnection connection,
        string table,
        FieldReadOptions options,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(table)) return [];

        var order = options.OrderByVerifyIndex
            ? "ORDER BY COALESCE(VERIFY_INDEX,999),F_ID"
            : "ORDER BY F_ID";
        var sql = $"""
            SELECT LTRIM(RTRIM(F_ID)),COALESCE(NULLIF(LTRIM(RTRIM(F_DESC)),''),LTRIM(RTRIM(F_ID))),
                   COALESCE(NULLIF(LTRIM(RTRIM(F_TYPE)),''),'nvarchar'),COALESCE(DISPLAY_LENGTH,0),
                   LTRIM(RTRIM(ISNULL(ITEM_ALIGN,''))),LTRIM(RTRIM(ISNULL(HEADER_ALIGN,''))),
                   LTRIM(RTRIM(ISNULL(DISPLAY_FORMAT,''))),LTRIM(RTRIM(ISNULL(NUMERIC_ROLE,''))),
                   CAST(COALESCE(IS_COST,0) AS bit),CAST(COALESCE(IS_SECRECY,0) AS bit),
                   CAST(COALESCE(IS_VISIBLE,1) AS bit),CAST(COALESCE(IS_VIRTUAL,0) AS bit)
            FROM dbo.FIELDS WITH (NOLOCK)
            WHERE LTRIM(RTRIM(T_ID))=@Table
            {order};
            """;

        // 物理列集合走共享原语；只在需要时才查一次 sys.columns
        var physical = options.PhysicalOnly
            ? await WorkbenchSql.GetPhysicalColumnsAsync(connection, null, table.Trim(), token)
            : null;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table.Trim();
        await using var reader = await command.ExecuteReaderAsync(token);

        var result = new List<FieldMetadata>();
        while (await reader.ReadAsync(token))
        {
            var key = reader.GetString(0);
            var isCost = reader.GetBoolean(8);
            var isSecrecy = reader.GetBoolean(9);
            var isVisible = reader.GetBoolean(10);
            var isVirtual = reader.GetBoolean(11);

            if (options.VisibleOnly && !isVisible) continue;
            if (options.DropVirtual && isVirtual) continue;
            if (physical is not null && !physical.Contains(key)) continue;
            if (options.DeniedFields is not null && options.DeniedFields.Contains(key)) continue;
            if (isCost && !options.CanViewCost) continue;
            if (isSecrecy && !options.CanViewSecrecy) continue;

            var align = reader.GetString(4);
            var headerAlign = reader.GetString(5);
            var displayFormat = reader.GetString(6);
            var numericRole = reader.GetString(7);
            result.Add(new FieldMetadata(
                key,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                string.IsNullOrWhiteSpace(align) ? null : align.Trim(),
                string.IsNullOrWhiteSpace(headerAlign) ? null : headerAlign.Trim(),
                string.IsNullOrWhiteSpace(displayFormat) ? null : displayFormat.Trim(),
                string.IsNullOrWhiteSpace(numericRole) ? null : numericRole.Trim(),
                isCost,
                isSecrecy,
                isVisible,
                isVirtual));
        }
        return result;
    }
}
