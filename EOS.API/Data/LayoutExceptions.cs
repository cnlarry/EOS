namespace EOS.API.Data;

/// <summary>
/// layout.json 结构性/越界/非法格式错误（ADR-010 §3 fail-closed）：
/// 渲染时遇到不可恢复的结构错误直接拒绝，不做猜测。
/// </summary>
public sealed class LayoutInvalidException(string message) : Exception(message);
