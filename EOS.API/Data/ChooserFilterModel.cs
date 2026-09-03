using System.Text.Json;
using System.Text.Json.Serialization;

namespace EOS.API.Data;

/// <summary>
/// 字段选择器结构化过滤条件条目。
/// 书写契约：扁平行（field/operator/value/nullSafe）是构建器支持、推荐使用的形式；
/// 表达式（left/right）、嵌套 group、子查询为存量兼容的高级 JSON 语法（构建器不可视化，
/// 仅文本 JSON 维护，编译校验失败即拒绝保存）；新增语法需评审，不默认扩展。
/// 第一版（P1）扁平格式继续有效：field/operator/value/nullSafe；
/// P3 扩展：left/right 表达式、group 嵌套、negate、subquery（IN/NOT_IN/EXISTS/函数）。
/// - field：源表物理列（裸列名）或「表.列」跨表引用（须在源表 QUERY_RELATION JOIN 白名单内）；
/// - operator：EQ/NE/GT/GE/LT/LE/LIKE/NOT_LIKE/IS_NULL/IS_NOT_NULL，宏 ISNULL_ZERO / DAYS_FROM_TODAY，
/// 子查询算子 IN/NOT_IN/EXISTS/NOT_EXISTS/IN_FUNCTION；
/// - value：字面量或运行时模板（{m.X}/{d.X}/{X}/{module}），编译期一律参数化绑定；
/// - nullSafe：可选扩展（ZERO/EMPTY），把列包成 ISNULL(列,0)/ISNULL(列,'') 再比较，
/// 用于覆盖高频写法 ISNULL(列,0) op 值（如 ISNULL(HR_EMPLOYEE.STATE,0)&lt;4）。
/// </summary>
public sealed record ChooserFilterItem(
    string? Field = null,
    string? Operator = null,
    string? Value = null,
    [property: JsonPropertyName("nullSafe")] string? NullSafe = null,
    [property: JsonPropertyName("left")] ChooserFilterExpression? Left = null,
    [property: JsonPropertyName("right")] ChooserFilterExpression? Right = null,
    [property: JsonPropertyName("negate")] bool? Negate = null,
    [property: JsonPropertyName("group")] ChooserFilterStruct? Group = null,
    [property: JsonPropertyName("subquery")] ChooserFilterSubquery? Subquery = null);

/// <summary>
/// 条件表达式（P3）：column（表.列/裸列）、literal（字面量）、template（{m.X}/{d.X}/{module}）、
/// isnull（ISNULL(expr,default)）、arith（左 op 右）、datediff（DATEDIFF(part,column,GETDATE())）。
/// </summary>
public sealed record ChooserFilterExpression(
    string Kind,
    string? Table = null,
    string? Column = null,
    string? Value = null,
    string? Op = null,
    [property: JsonPropertyName("left")] ChooserFilterExpression? Left = null,
    [property: JsonPropertyName("right")] ChooserFilterExpression? Right = null);

/// <summary>子查询 FROM 表（逗号多表）。</summary>
public sealed record ChooserSubqueryTable(string Table, string? Alias = null);

/// <summary>
/// 子查询条件（P3）：IN/NOT_IN 用 Column；EXISTS/NOT_EXISTS 忽略 Column；
/// Function 为受控表值函数名（如 f_get_pro_units），Args 为函数实参（模板/字面量）；
/// Filter 为子查询 WHERE 条件链（表引用以 From 集合为白名单）。
/// </summary>
public sealed record ChooserFilterSubquery(
    string? Table = null,
    [property: JsonPropertyName("from")] IReadOnlyList<ChooserSubqueryTable>? From = null,
    string? Column = null,
    string? Function = null,
    [property: JsonPropertyName("args")] IReadOnlyList<string>? Args = null,
    [property: JsonPropertyName("filter")] IReadOnlyList<ChooserFilterItem>? Filter = null);

/// <summary>字段选择器结构化过滤条件集：第一版平铺 + 顶层 logic（AND/OR）。</summary>
public sealed record ChooserFilterStruct(string Logic, IReadOnlyList<ChooserFilterItem> Items)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        // 序列化省略 null 键：存量 JSON 曾把 item 全字段（left/right/nullSafe/...）都写成 :null，
        // 既膨胀体积又干扰结构识别；新写入保持紧凑（解析对 null 键本就兼容）
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>空/空白视为「无过滤」（合法）；JSON 非法返回 false（调用方 fail-closed）。</summary>
    public static bool TryParse(string? json, out ChooserFilterStruct? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(json)) return true;
        try
        {
            value = JsonSerializer.Deserialize<ChooserFilterStruct>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }
        return value is not null;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

/// <summary>回填映射条目：target=表单目标字段（去前缀统一），column=来源表列（物理列或受控虚拟列，对齐旧语义）。</summary>
public sealed record ChooserReturnItem(string Target, string Column);

/// <summary>RETURN_ITEMS JSON 数组解析（有序回填映射）。</summary>
public static class ChooserReturnItems
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>空/空白视为空映射；JSON 非法返回 null（调用方 fail-closed）。</summary>
    public static IReadOnlyList<ChooserReturnItem>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ChooserReturnItem>>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>把有序映射序列化为 JSON 数组文本。</summary>
    public static string ToJson(IEnumerable<ChooserReturnItem> items) =>
        JsonSerializer.Serialize(items, JsonOptions);
}
