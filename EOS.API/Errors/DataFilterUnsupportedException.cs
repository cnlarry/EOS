namespace EOS.API.Errors;

/// <summary>
/// 模块 FILTER / 用户 DATA_FILTER 表达式无法安全解析时抛出，
/// 由 ApiExceptionFilter 统一转为 403 DATA_FILTER_UNSUPPORTED（拒绝执行，不返回越权数据）。
/// </summary>
public sealed class DataFilterUnsupportedException(string message) : Exception(message);
