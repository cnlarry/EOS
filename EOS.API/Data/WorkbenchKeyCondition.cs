namespace EOS.API.Data;

/// <summary>
/// 单据主键条件构造：把主键列与值拼成 `WHERE` 片段（列名来自服务端元数据白名单，值做单引号转义），
/// 供工作流状态读写、删除守卫、助手工具等复用。不信任任何来自客户端的表名/列名/条件片段。
/// </summary>
public static class WorkbenchKeyCondition
{
    /// <summary>
    /// 构造主键条件，如 [QUOTE_TYPE]='BJK' AND [QUOTE_NO]='BJK26080001'。
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
