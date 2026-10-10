namespace EOS.API.Features.Import;

/// <summary>
/// 可导入的目标：**模块**，不是表。
///
/// <para>
/// 权限、字段级限制、默认值、效果链都挂在模块上（`MODULES` + 统一表单定义），
/// 表只是模块的落地形态；按表导入就只能裸写库。故候选集 = 统一表单写名单里的
/// 统一工作台模块，可导入与否由该模块的写路径裁决。
/// </para>
/// </summary>
public sealed record ImportTarget(int ModuleId, string Title, string MasterTable);

/// <summary>导入定义里的一列。判据全部来自服务端表单定义，前端不自行推断。</summary>
public sealed record ImportFieldInfo(
    string Key,
    string Label,
    string DataType,
    string? Format,
    bool IsRequired,
    bool IsPrimaryKey,
    bool IsAutoIncrement,
    int? MaxLength);

/// <summary>
/// 某个模块的导入定义：主表 + 可由用户填写的列 + 必须映射的主键列。
/// <paramref name="RequiredKeys"/> 里的列没映射就禁止执行——缺主键必然撞库。
/// </summary>
public sealed record ImportDefinitionInfo(
    int ModuleId,
    string Title,
    string MasterTable,
    IReadOnlyList<ImportFieldInfo> Fields,
    IReadOnlyList<string> RequiredKeys);

/// <summary>解析后的表格数据：列名、全部数据行、是否因超出上限被截断。</summary>
public sealed record ImportFileData(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    int TotalRows,
    bool Truncated,
    string SourceName);

/// <summary>
/// 预演/执行请求：<paramref name="Mapping"/> 与 <paramref name="Columns"/> 按下标对齐，
/// 元素为 null 表示该列不导入。<paramref name="SourceName"/> 只用于批次审计留档。
/// </summary>
public sealed record ImportRunRequest(
    IReadOnlyList<string> Columns,
    IReadOnlyList<string?> Mapping,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    string? SourceName = null);

/// <summary>逐行判定结果。Code 是稳定标识（不是给人看的文案），与保存路径的错误码同源。</summary>
public sealed record ImportRowOutcome(
    int RowNumber,
    bool Ok,
    string? Code,
    string? Message,
    IReadOnlyList<ImportRowIssue>? FieldErrors);

public sealed record ImportRowIssue(string Field, string Message, string? Code);

/// <summary>整批结果。<paramref name="DryRun"/> 为真表示这一批全部回滚过（预演）。</summary>
public sealed record ImportRunResult(
    bool DryRun,
    int Total,
    int Succeeded,
    int Failed,
    IReadOnlyList<ImportRowOutcome> Rows);

/// <summary>一条列映射：源列名 → 目标字段键（<paramref name="Field"/> 为 null 表示该列不导入）。</summary>
public sealed record ImportMappingEntry(string Column, string? Field);

/// <summary>
/// 记住的映射。按（用户 + 模块）存一份，补导时按**列名**逐列套用——列顺序变了也不串位。
/// </summary>
public sealed record ImportMappingSnapshot(
    int ModuleId,
    string SourceName,
    IReadOnlyList<ImportMappingEntry> Entries,
    DateTime? UpdatedAt,
    string UpdatedBy);

public sealed record ImportMappingSaveRequest(
    string? SourceName,
    IReadOnlyList<ImportMappingEntry> Entries);

/// <summary>
/// 前置资料就绪度：目标模块的必填字段里，那些**引用另一张主档**而那张主档还是空表的情形。
/// <para>
/// 这类字段在预演时未必报错（引用存在性取决于校验配置），但真到落库会因引不到值整行失败；
/// 提前说出来，实施就能按依赖顺序先导被引用的那张表。
/// </para>
/// </summary>
public sealed record ImportReadinessItem(
    string Field,
    string FieldLabel,
    string SourceTable,
    string SourceLabel,
    long Rows);

