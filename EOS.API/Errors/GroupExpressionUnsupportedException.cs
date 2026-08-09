namespace EOS.API.Errors;

/// <summary>
/// GROUP_EXP（分组表达式）无法受控解析时抛出，由 ApiExceptionFilter 统一转为
/// 403 GROUP_EXP_UNSUPPORTED（拒绝执行，不返回未过滤数据）。
/// </summary>
public sealed class GroupExpressionUnsupportedException(string message) : Exception(message);
