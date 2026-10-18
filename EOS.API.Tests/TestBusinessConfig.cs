using EOS.API.Data;
using EOS.API.Data.DocumentActions;
using EOS.API.Data.DocumentActions.Handlers;
using EOS.API.Data.Workbench;
using EOS.API.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EOS.API.Tests;

/// <summary>
/// 业务动作/校验配置仓库的测试构造点：定义装配会现读这两段，而构造仓库需要一串依赖。
/// </summary>
internal static class TestBusinessConfig
{
    public static ModuleBusinessConfigRepository Create(
        DbConnectionFactory connections,
        IUnifiedFormCapabilitySource? formCapability = null)
    {
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        var auditWriter = new WorkbenchAuditWriter(connections, new HttpContextAccessor(), provider,
            Options.Create(new AuditSettings()));
        return new ModuleBusinessConfigRepository(
            connections,
            new WorkbenchDirtyMarker(connections),
            auditWriter,
            new DocumentActionRegistry([new DocumentActionProbeHandler()], NullLogger<DocumentActionRegistry>.Instance),
            new WorkbenchIdempotency(),
            formCapability ?? TestFormCapability.None,
            NullLogger<ModuleBusinessConfigRepository>.Instance);
    }
}
