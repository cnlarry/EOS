import {
  IconArrowUpRight,
  IconChecklist,
  IconClockHour4,
  IconFileInvoice,
  IconPackage,
} from '@tabler/icons-react'

const metrics = [
  { label: '待审批单据', value: '12', detail: '较昨日增加 3 条', icon: IconChecklist },
  { label: '本月采购金额', value: '¥ 286,420', detail: '预算执行率 68%', icon: IconFileInvoice },
  { label: '待入库批次', value: '8', detail: '其中 2 批即将逾期', icon: IconPackage },
]

export function DashboardPage() {
  return (
    <>
      <div className="row row-deck row-cards">
        {metrics.map((metric) => {
          const Icon = metric.icon
          return (
            <div className="col-sm-6 col-xl-4" key={metric.label}>
              <article className="card erp-metric-card">
                <div className="card-body">
                  <div className="d-flex align-items-start">
                    <div>
                      <div className="text-secondary fw-medium">{metric.label}</div>
                      <div className="h1 mb-1 mt-2">{metric.value}</div>
                      <div className="text-secondary small">{metric.detail}</div>
                    </div>
                    <span className="erp-metric-icon ms-auto">
                      <Icon size={23} stroke={1.7} />
                    </span>
                  </div>
                </div>
              </article>
            </div>
          )
        })}
      </div>

      <div className="row row-cards mt-1">
        <div className="col-lg-8">
          <section className="card">
            <div className="card-header">
              <div>
                <h2 className="card-title">采购执行概览</h2>
                <div className="text-secondary small mt-1">最近六个月订单金额与到货进度</div>
              </div>
              <button className="btn btn-sm btn-ghost-secondary ms-auto" type="button">
                查看报表 <IconArrowUpRight size={16} />
              </button>
            </div>
            <div className="card-body">
              <div className="erp-chart-placeholder">
                <div className="erp-chart-bars" aria-label="采购趋势示意图">
                  {[42, 58, 47, 72, 64, 84, 69, 91, 77, 88, 73, 95].map((height, index) => (
                    <span key={index} style={{ height: `${height}%` }} />
                  ))}
                </div>
                <div className="erp-chart-labels">
                  <span>2月</span><span>3月</span><span>4月</span><span>5月</span><span>6月</span><span>7月</span>
                </div>
              </div>
            </div>
          </section>
        </div>
        <div className="col-lg-4">
          <section className="card h-100">
            <div className="card-header">
              <h2 className="card-title">待办事项</h2>
              <span className="badge bg-blue-lt ms-auto">5 项</span>
            </div>
            <div className="list-group list-group-flush">
              {[
                ['采购订单 PO-20260802-018', '等待您的审批', '10 分钟前'],
                ['供应商报价即将失效', '华南包装材料有限公司', '今天 14:00'],
                ['订单交期需要确认', 'PO-20260801-006', '昨天'],
              ].map(([title, detail, time]) => (
                <div className="list-group-item" key={title}>
                  <div className="d-flex gap-3">
                    <IconClockHour4 className="text-blue mt-1" size={19} />
                    <div className="min-w-0">
                      <div className="fw-semibold text-truncate">{title}</div>
                      <div className="text-secondary small">{detail}</div>
                      <div className="text-secondary small mt-1">{time}</div>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          </section>
        </div>
      </div>
    </>
  )
}
