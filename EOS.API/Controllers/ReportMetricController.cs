using EOS.API.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 最小语义层口径清单：度量口径定义一次、机器可读、多处消费。
/// 供报表与 Agent 共同消费；不自建查询引擎、不引入外部语义层产品。
/// 度量定义本身属开发态，随 DbUp 迁移版本化落地。
///
/// 读 SQL 在 <see cref="ReportRepository.ListMetricsAsync"/>：控制器只做契约与授权，
/// 不持有数据访问（ADR-036 批 1 第 5 项）。
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/report-metrics")]
public sealed class ReportMetricController(ReportRepository reports) : ControllerBase
{
    /// <summary>口径清单：列出全部已定义的度量口径。</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken token)
        => Ok(await reports.ListMetricsAsync(token));
}
