using System.Data;
using System.Text.Json;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 选择器来源记忆：记录「这张单的这个字段，当初是从哪个来源选入的」，供读取回显时决定用
/// 哪个来源解析同组伴生字段。只影响回显解析——来源配置、筛选、回写映射（RETURN_ITEMS）
/// 与业务表结构都不变；无记忆时回退「首个启用来源」，与既有行为一致。
/// </summary>
internal static class FormChooserSourceMemo
{
    /// <summary>行键/单据键与字段键的分隔符（两键拼接后作为内存字典的键，不落库）。</summary>
    private const char KeySeparator = '\u0001';

    internal static string SerializeKey(IEnumerable<string> keyValues) => JsonSerializer.Serialize(keyValues);

    /// <summary>键列宽上限（与建表口径一致）。记忆是可选的读侧优化，键过长时宁可不记，也不能让保存失败。</summary>
    internal const int MaxKeyLength = 200;

    internal static bool KeyFits(string serializedKey) => serializedKey.Length <= MaxKeyLength;

    internal static string EntryKey(string field, string keyValues) => field + KeySeparator + keyValues;

    /// <summary>拆分内存字典键（字段键 + 行键），供调用方按行键比对存活行。</summary>
    internal static (string Field, string KeyValues) ParseEntryKey(string entryKey)
    {
        var index = entryKey.IndexOf(KeySeparator);
        return index < 0
            ? (entryKey, string.Empty)
            : (entryKey[..index], entryKey[(index + 1)..]);
    }

    /// <summary>该字段是否值得记忆来源：复合格主字段且存在多个可用来源（单来源取第一个即唯一，记了也是冗余）。</summary>
    internal static bool ShouldRemember(FormFieldDefinition field) =>
        field.CellRole == 1
        && !string.IsNullOrWhiteSpace(field.CellGroup)
        && field.Choosers.Count(source => source.Active && !string.IsNullOrWhiteSpace(source.Table)) > 1;

    /// <summary>
    /// 选择回显所用来源：记忆命中且该来源仍启用时用记忆的来源，否则回退首个启用来源。
    /// 来源表为空或已停用时按回退处理，不把配置变更变成回显错误。
    /// </summary>
    internal static FieldChooserSource? SelectSource(FormFieldDefinition field, int? rememberedSerial)
    {
        var usable = field.Choosers
            .Where(source => source.Active && !string.IsNullOrWhiteSpace(source.Table))
            .ToList();
        if (usable.Count == 0)
        {
            return null;
        }
        if (rememberedSerial is not null)
        {
            var remembered = usable.FirstOrDefault(source => source.SerialNo == rememberedSerial);
            if (remembered is not null)
            {
                return remembered;
            }
        }
        return usable[0];
    }

    /// <summary>读取一张单据在某张表上的全部记忆：键 = 字段键 + 行键。</summary>
    internal static async Task<Dictionary<string, int>> ReadAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int moduleId,
        string table,
        string masterKeyValues,
        CancellationToken token)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        const string sql = """
            SELECT F_ID, KEY_VALUES, SOURCE_SERIAL_NO
            FROM dbo.FORM_CHOOSER_SOURCE_MEMO
            WHERE M_IDX = @ModuleId AND T_ID = @Table AND MASTER_KEY_VALUES = @MasterKey;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        command.Parameters.Add("@MasterKey", SqlDbType.NVarChar, 200).Value = masterKeyValues;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var field = reader.GetString(0);
            var keyValues = reader.GetString(1);
            result[EntryKey(field, keyValues)] = reader.GetInt32(2);
        }
        return result;
    }

    /// <summary>删除一张单据的全部记忆（单据删除时调用，防孤儿）。</summary>
    internal static async Task DeleteDocumentAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int moduleId,
        string masterKeyValues,
        CancellationToken token)
    {
        const string sql = "DELETE FROM dbo.FORM_CHOOSER_SOURCE_MEMO WHERE M_IDX = @ModuleId AND MASTER_KEY_VALUES = @MasterKey;";
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@MasterKey", SqlDbType.NVarChar, 200).Value = masterKeyValues;
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>删除某张表上若干行的记忆（按字段 + 行键精确定位）。</summary>
    internal static async Task DeleteEntriesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int moduleId,
        string table,
        IReadOnlyList<(string Field, string KeyValues)> entries,
        CancellationToken token)
    {
        foreach (var (field, keyValues) in entries)
        {
            const string sql = """
                DELETE FROM dbo.FORM_CHOOSER_SOURCE_MEMO
                WHERE M_IDX = @ModuleId AND T_ID = @Table AND F_ID = @Field AND KEY_VALUES = @KeyValues;
                """;
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
            command.Parameters.Add("@Field", SqlDbType.NVarChar, 100).Value = field;
            command.Parameters.Add("@KeyValues", SqlDbType.NVarChar, 200).Value = keyValues;
            await command.ExecuteNonQueryAsync(token);
        }
    }

    /// <summary>写入一条记忆（调用方保证先删除同键旧值）。</summary>
    internal static async Task InsertAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int moduleId,
        string table,
        string field,
        string keyValues,
        string masterKeyValues,
        int sourceSerialNo,
        string user,
        CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.FORM_CHOOSER_SOURCE_MEMO
                (M_IDX, T_ID, F_ID, KEY_VALUES, MASTER_KEY_VALUES, SOURCE_SERIAL_NO, UPDATED_BY, UPDATED_AT)
            VALUES (@ModuleId, @Table, @Field, @KeyValues, @MasterKey, @Serial, @User, SYSUTCDATETIME());
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        command.Parameters.Add("@Field", SqlDbType.NVarChar, 100).Value = field;
        command.Parameters.Add("@KeyValues", SqlDbType.NVarChar, 200).Value = keyValues;
        command.Parameters.Add("@MasterKey", SqlDbType.NVarChar, 200).Value = masterKeyValues;
        command.Parameters.Add("@Serial", SqlDbType.Int).Value = sourceSerialNo;
        command.Parameters.Add("@User", SqlDbType.NVarChar, 50).Value = (object?)user ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(token);
    }
}
