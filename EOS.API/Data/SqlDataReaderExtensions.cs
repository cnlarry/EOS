using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// SqlDataReader 按列名读取扩展（F1 BOM 四链下线时从 FieldConfigurationRepository 迁出收编）：
/// 原实现散落在多个仓储文件内嵌的扩展类中，统一到本共享类，避免删除仓储时误删被广泛引用的读取原语。
/// </summary>
internal static class SqlDataReaderExtensions
{
    public static string? GetNullableString(this SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal).Trim();
    }

    public static int? GetNullableInt32(this SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }
}
