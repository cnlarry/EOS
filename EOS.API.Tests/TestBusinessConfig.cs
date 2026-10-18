using EOS.API.Data;
using EOS.API.Data.Workbench;

namespace EOS.API.Tests;

/// <summary>
/// 业务动作/校验规则只读入口的测试构造点（定义装配会现读这两段）。
/// 配置仓库要一串依赖，而它注入不了定义构建器（会成环），所以构建器只依赖这个读取点。
/// </summary>
internal static class TestBusinessConfig
{
    public static ModuleBusinessConfigReader Reader(DbConnectionFactory connections) => new(connections);
}
