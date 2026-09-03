using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.SqlClient;

using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>
/// 运行时 Definition 快照提供者：
/// 启动或显式刷新时从 WORKBENCH_DEFINITION_SNAPSHOT（IS_CURRENT=1）加载已发布快照，
/// 提供模块级不可变基线（DefinitionVersion = module-{id}-v{n}）。
/// DocumentWorkbenchRepository 在模块「已发布且未脏」时优先使用基线构建每用户定义，
/// 脏模块/无快照模块回退实时元数据构建；发布动作后刷新对应模块快照。
/// </summary>
public sealed class WorkbenchDefinitionProvider(
    DbConnectionFactory connections,
    ILogger<WorkbenchDefinitionProvider> logger)
{
    internal static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new ReadOnlySetConverter());
        return options;
    }

    private readonly ConcurrentDictionary<int, BaselineEntry> _baselines = new();

    public async Task RefreshAsync(CancellationToken token)
    {
        const string sql = """
            SELECT MODULE_ID, DEFINITION_JSON, VERSION
            FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WITH (NOLOCK)
            WHERE IS_CURRENT=1;
            """;
        var loaded = new Dictionary<int, BaselineEntry>();
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var moduleId = reader.GetInt32(0);
            var json = reader.GetString(1);
            var version = reader.GetInt32(2);
            try
            {
                var definition = JsonSerializer.Deserialize<WorkbenchDefinition>(json, JsonOptions);
                if (definition is not null)
                {
                    loaded[moduleId] = new BaselineEntry(definition, $"module-{moduleId}-v{version}");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("Definition 快照反序列化失败，跳过 module={ModuleId} error={Error}", moduleId, ex.Message);
            }
        }
        _baselines.Clear();
        foreach (var (moduleId, entry) in loaded)
        {
            _baselines[moduleId] = entry;
        }
        logger.LogInformation("Definition 快照加载完成 modules={Count}", loaded.Count);
    }

    public bool TryGetBaseline(int moduleId, out WorkbenchDefinition definition, out string version)
    {
        if (_baselines.TryGetValue(moduleId, out var entry))
        {
            definition = entry.Definition;
            version = entry.Version;
            return true;
        }
        definition = null!;
        version = string.Empty;
        return false;
    }

    public string? GetVersion(int moduleId) =>
        _baselines.TryGetValue(moduleId, out var entry) ? entry.Version : null;

    private sealed record BaselineEntry(WorkbenchDefinition Definition, string Version);

    /// <summary>STJ 不支持反序列化 IReadOnlySet&lt;string&gt;（WorkbenchDefinition.FilterFieldKeys），自实现为 HashSet。</summary>
    private sealed class ReadOnlySetConverter : System.Text.Json.Serialization.JsonConverter<IReadOnlySet<string>>
    {
        public override IReadOnlySet<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var values = JsonSerializer.Deserialize<List<string>>(ref reader, options);
            return values?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public override void Write(Utf8JsonWriter writer, IReadOnlySet<string> value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value.ToList(), options);
    }
}
