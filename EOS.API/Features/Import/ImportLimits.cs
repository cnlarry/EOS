namespace EOS.API.Features.Import;

/// <summary>
/// 导入的规模上限：**只在这里声明一次**，业务代码不得写死同值数字。
///
/// <para>
/// 每次导入的每一行都走完整写通道（各自一个事务 + 审计 + 效果链），代价与"手工录一行"同阶，
/// 不是批量直写；上限是为了让"一次导入"有可预期的耗时段，而不是贴近裸写的吞吐。
/// </para>
/// </summary>
internal static class ImportLimits
{
    /// <summary>单个文件的最大字节数。</summary>
    public const long MaxFileBytes = 10 * 1024 * 1024;

    /// <summary>单个文件的最大数据行数（不含表头）；超出即截断并回报。</summary>
    public const int MaxRows = 2000;

    /// <summary>最大列数（防止畸形文件把内存撑爆）。</summary>
    public const int MaxColumns = 200;

    /// <summary>单行最多回报几条字段级错误：整批与整行的判定不受影响，只是明细不全量外发。</summary>
    public const int MaxFieldErrorsPerRow = 10;
}
