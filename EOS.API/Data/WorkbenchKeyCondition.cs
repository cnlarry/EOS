namespace EOS.API.Data;

/// <summary>
/// 单据主键身份串构造：把主键列与值拼成 `[COL]='VAL' AND ...`（列名来自服务端元数据白名单，
/// 值做单引号转义）。产物是**自描述的身份串**：写入 WF_MONITOR.KEY_VALUE 后可由
/// <see cref="WorkflowEngine.ParseKeyValues"/> 读回主键值，格式固定、不得变更（历史流程实例按此串匹配）。
///
/// 作为 SQL 谓词使用仅限于"只有身份串、拿不到主键列元数据"的场景（从 KEY_VALUE 读回后）。
/// 凡同时握有主键列与值的调用方，一律改用参数化的 <c>WorkbenchSql.BuildKeyWhere</c>，
/// 不要把本方法当作 SQL 片段生成器。嵌入安全依赖此处的转义，移除转义会同时打开所有嵌入点。
/// </summary>
public static class WorkbenchKeyCondition
{
    /// <summary>
    /// 构造主键身份串，如 [QUOTE_TYPE]='BJK' AND [QUOTE_NO]='BJK26080001'。
    /// </summary>
    public static string Build(IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues)
    {
        if (pkColumns.Count != keyValues.Count)
            throw new ArgumentException("主键列与主键值数量不一致。");
        return string.Join(" AND ", pkColumns.Select((column, index) =>
            $"[{column}]='{Escape(keyValues[index])}'"));
    }

    private static string Escape(string value) => value.Replace("'", "''");
}
