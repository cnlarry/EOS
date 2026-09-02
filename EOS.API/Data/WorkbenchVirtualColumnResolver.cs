using Microsoft.Data.SqlClient;

using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>
/// 受控虚拟列 / 转换函数 / 展示计算统一入口（ADR-005 §2 组件表 WorkbenchVirtualColumnResolver）：
/// 包装 VirtualColumnResolver（定义与列表虚拟列解析）与 ConvertFunctionResolver（展示转换）。
/// 阶段 3 先统一消费入口；解析器内部白名单与参数化边界保持不变。
/// </summary>
public sealed class WorkbenchVirtualColumnResolver
{
    public async Task<VirtualColumnResolution> ResolveDefinitionAsync(
        SqlConnection connection,
        string table,
        IReadOnlyList<WorkbenchField> virtualFields,
        CancellationToken token,
        string? baseAlias = null)
        => await new VirtualColumnResolver(connection).ResolveAsync(table, virtualFields, token, baseAlias);

    public async Task ApplyConvertFunctionsAsync(
        SqlConnection connection,
        IReadOnlyList<WorkbenchField> fields,
        IReadOnlyList<Dictionary<string, object?>> rows,
        CancellationToken token)
    {
        foreach (var field in fields.Where(field => !string.IsNullOrWhiteSpace(field.ConvertFunction)))
        {
            await ConvertFunctionResolver.ApplyAsync(connection, field.Key, field.ConvertFunction!, rows, token);
        }
    }
}
