using EOS.API.Data;
using EOS.API.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EOS.API.Tests;

/// <summary>
/// 测试用的 <see cref="DepotStockPolicyService"/> 构造：它依赖审计写入器，
/// 而审计写入器又依赖连接工厂 / 定义版本提供器 / 请求上下文 / 审计开关。
/// 逐个用例各拼一遍会把样板抄散，集中在这里。
/// </summary>
internal static class PolicyServiceFactory
{
    public static DbConnectionFactory Connections(string connectionString) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = connectionString,
            })
            .Build());

    public static DepotStockPolicyService Create(string connectionString) =>
        new(Connections(connectionString), AuditWriter(connectionString));

    /// <summary>
    /// 审计写入器：库存移动引擎在"改写既有批次主档行"时要留痕，真库用例里的构造方式与服务端一致。
    /// </summary>
    public static WorkbenchAuditWriter AuditWriter(string connectionString)
    {
        var connections = Connections(connectionString);
        return new WorkbenchAuditWriter(
            connections,
            new HttpContextAccessor(),
            new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance),
            Options.Create(new AuditSettings()));
    }
}
