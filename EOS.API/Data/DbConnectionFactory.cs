using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 唯一数据库连接入口。所有仓储通过该工厂创建 SqlConnection，
/// 便于统一连接串来源与未来集中埋点（连接计时、超时策略等）。
/// </summary>
public sealed class DbConnectionFactory(IConfiguration configuration)
{
    public SqlConnection Create()
    {
        var connectionString = configuration.GetConnectionString("ErpDatabase")
            ?? throw new InvalidOperationException("ConnectionStrings:ErpDatabase 未配置。");
        return new SqlConnection(connectionString);
    }

    /// <summary>创建指向 EOS.IM 即时通讯库的连接（独立于 EOS.ERP 旧库）。</summary>
    public SqlConnection CreateIm()
    {
        var connectionString = configuration.GetConnectionString("ImDatabase")
            ?? throw new InvalidOperationException("ConnectionStrings:ImDatabase 未配置。");
        return new SqlConnection(connectionString);
    }

    /// <summary>创建指向 EOS.Mail 邮件任务库的连接（独立于 EOS.ERP 旧库与 EOS.IM 消息库）。</summary>
    public SqlConnection CreateMail()
    {
        var connectionString = configuration.GetConnectionString("MailDatabase")
            ?? throw new InvalidOperationException("ConnectionStrings:MailDatabase 未配置。");
        return new SqlConnection(connectionString);
    }
}
