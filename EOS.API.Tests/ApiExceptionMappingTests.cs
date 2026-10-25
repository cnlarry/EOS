using EOS.API.Data.Effects;
using EOS.API.Errors;
using EOS.API.Features.Assistant.Admin;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Import;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 异常 → 错误契约的映射表（`ApiExceptionFilter.Map`）。
///
/// 控制器不再自己 `catch` 映射错误之后，这层翻译就没有 HTTP 用例能间接覆盖了：
/// 用例直接调用控制器 action 时过滤器不参与，所以为它单独钉一份表。
/// 断言状态码、稳定码与文案来源（消息取异常的 Message，还是固定文案），三者任一漂移都会红。
/// </summary>
public sealed class ApiExceptionMappingTests
{
    [Fact]
    public void 未登记的类型不吞异常()
    {
        // 返回 0 表示"上层放行成 500 并照常记日志"：没登记 ≠ 静默成功
        var (status, code, message) = ApiExceptionFilter.Map(new NotImplementedInThisBuildException());

        Assert.Equal(0, status);
        Assert.Equal(string.Empty, code);
        Assert.Equal(string.Empty, message);
    }

    [Fact]
    public void 请求参数类异常映射为400()
    {
        var (status, code, message) = ApiExceptionFilter.Map(new ArgumentException("字段 X 非法。"));

        Assert.Equal(400, status);
        Assert.Equal(ApiErrorCodes.InvalidArgument, code);
        Assert.Equal("字段 X 非法。", message);
    }

    [Fact]
    public void 关账拦截映射为400并带期间码()
    {
        var (status, code, _) = ApiExceptionFilter.Map(
            new PeriodClosedException("CLOSE", "CM001", new DateTime(2026, 1, 31), "期间已关账。"));

        Assert.Equal(400, status);
        Assert.Equal(ApiErrorCodes.PeriodClosed, code);
    }

    [Fact]
    public void 向量模型未接线映射为503()
    {
        var (status, code, message) = ApiExceptionFilter.Map(new EmbeddingNotConfiguredException("未配置嵌入模型。"));

        Assert.Equal(503, status);
        Assert.Equal(ApiErrorCodes.KbEmbeddingNotConfigured, code);
        Assert.Equal("未配置嵌入模型。", message);
    }

    [Fact]
    public void 厂商侧失败带它自己的原因码()
    {
        var exception = new AssistantModelException(
            AssistantModelErrorKind.Unauthorized, "密钥被拒。");

        var (status, code, message) = ApiExceptionFilter.Map(exception);

        Assert.Equal(503, status);
        // 不能用"未配置"顶替：管理员会去反复确认模型配好了没有，而问题在密钥
        Assert.Equal("AI_MODEL_UNAUTHORIZED", code);
        Assert.Equal(exception.UserMessage, message);
    }

    [Fact]
    public void 导入的两类拒绝各带自己的码()
    {
        var file = ApiExceptionFilter.Map(new ImportFileException("IMPORT_BAD_ENCODING", "文件编码无法识别。"));
        var request = ApiExceptionFilter.Map(new ImportRequestException("IMPORT_MAPPING_MISSING", "缺少字段映射。"));

        Assert.Equal((400, "IMPORT_BAD_ENCODING"), (file.Status, file.Code));
        Assert.Equal((400, "IMPORT_MAPPING_MISSING"), (request.Status, request.Code));
    }

    [Fact]
    public void 效果预演的两类拒绝()
    {
        var unsupported = ApiExceptionFilter.Map(
            new EffectSimulationService.UnsupportedModuleException("SIMULATION_UNSUPPORTED", "该模块不执行效果链。"));
        var timeout = ApiExceptionFilter.Map(new EffectSimulationService.TimeoutException());

        Assert.Equal((400, "SIMULATION_UNSUPPORTED"), (unsupported.Status, unsupported.Code));
        Assert.Equal((504, ApiErrorCodes.SimulationTimeout), (timeout.Status, timeout.Code));
        // 超时异常本身没有消息：文案必须来自映射表，否则前端只会看到空字符串
        Assert.False(string.IsNullOrWhiteSpace(timeout.Message));
    }

    [Fact]
    public void 助手变更集被拦下映射为400()
    {
        var (status, code, message) = ApiExceptionFilter.Map(new ChangeSetBlockedException("试算未通过。"));

        Assert.Equal(400, status);
        Assert.Equal(ApiErrorCodes.ChangeSetBlocked, code);
        Assert.Equal("试算未通过。", message);
    }

    private sealed class NotImplementedInThisBuildException : Exception;
}
