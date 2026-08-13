namespace EOS.API.Errors;

/// <summary>报表 PDF 数据集超过服务端上限（10,000 行），拒绝生成并提示缩小条件。</summary>
public sealed class PdfDataTooLargeException(string message) : Exception(message);
