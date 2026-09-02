import { IconArrowDown, IconArrowUp, IconInbox, IconSearch, IconStar, IconStarFilled, IconX, IconPencil } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { EmptyState, LoadingState } from '../../components/common/AsyncState'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'

interface ReportCatalogItem {
  moduleId: number
  moduleDesc: string
  domainDesc: string
  reportId: string
  reportName: string
  isDefault: boolean
  favorite: boolean
  sortIndex: number
  lastRunAt: string | null
}

export function ReportCenterPage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [search, setSearch] = useState('')
  const [onlyFavorites, setOnlyFavorites] = useState(false)

  const catalog = useQuery({
    queryKey: ['report-center', 'catalog'],
    queryFn: () => apiClient.get<ReportCatalogItem[]>('/report-center/catalog'),
  })

  const favoriteMutation = useMutation({
    mutationFn: (item: ReportCatalogItem) => apiClient.post('/report-center/favorite', {
      moduleId: item.moduleId,
      reportId: item.reportId,
      favorite: !item.favorite,
      sortIndex: item.favorite ? null : (catalog.data?.length ?? 0) + 1,
    }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['report-center', 'catalog'] }),
  })

  const reorderMutation = useMutation({
    mutationFn: (items: { moduleId: number; reportId: string }[]) =>
      apiClient.post('/report-center/reorder', { items }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['report-center', 'catalog'] }),
  })

  const moveFavorite = (item: ReportCatalogItem, direction: -1 | 1) => {
    const favorites = (catalog.data ?? []).filter((it) => it.favorite)
    const index = favorites.findIndex((it) => it.moduleId === item.moduleId && it.reportId === item.reportId)
    const target = favorites[index + direction]
    if (!target) return
    const next = [...favorites]
    next[index] = target
    next[index + direction] = item
    reorderMutation.mutate(next.map((it) => ({ moduleId: it.moduleId, reportId: it.reportId })))
  }

  const recentReports = useMemo(() => {
    const withRun = (catalog.data ?? []).filter((it) => it.lastRunAt != null)
    return withRun
      .sort((a, b) => new Date(b.lastRunAt!).getTime() - new Date(a.lastRunAt!).getTime())
      .slice(0, 5)
  }, [catalog.data])

  const groups = useMemo(() => {
    const items = catalog.data ?? []
    const filtered = items.filter((item) => {
      if (onlyFavorites && !item.favorite) return false
      const text = search.trim().toLowerCase()
      if (!text) return true
      return item.reportName.toLowerCase().includes(text)
        || item.reportId.toLowerCase().includes(text)
        || item.moduleDesc.toLowerCase().includes(text)
        || item.domainDesc.toLowerCase().includes(text)
    })
    const map = new Map<string, ReportCatalogItem[]>()
    for (const item of filtered) {
      const domain = item.domainDesc || item.moduleDesc || '其它'
      const list = map.get(domain) ?? []
      list.push(item)
      map.set(domain, list)
    }
    return [...map.entries()]
      .sort(([a], [b]) => a.localeCompare(b, 'zh-CN'))
      .map(([domain, reports]) => ({ domain, reports }))
  }, [catalog.data, search, onlyFavorites])

  const openReport = (item: ReportCatalogItem) => {
    void apiClient.post('/report-center/favorite', {
      moduleId: item.moduleId,
      reportId: item.reportId,
      favorite: item.favorite,
      lastRunAt: new Date().toISOString(),
    }).catch(() => undefined)
    navigate(`/reports/${item.moduleId}`)
  }

  return (
    <div className="erp-full-list-page">
      <section className="card erp-list-card">
        <section className="erp-list-command-bar" aria-label="报表中心工具栏">
          <div className="erp-nav-search erp-menu-search">
            <IconSearch size={16} aria-hidden="true" />
            <input
              type="search"
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              placeholder="搜索报表名称/编号/业务域"
              aria-label="搜索报表"
            />
            {search && (
              <button type="button" className="erp-nav-search-clear" aria-label="清除搜索" onClick={() => setSearch('')}>×</button>
            )}
          </div>
          <div className="erp-list-actions d-flex gap-2 align-items-center">
            <Button variant="ghost" size="sm" icon={<IconInbox size={16} />} onClick={() => navigate('/report-center/inbox')}>
              收件箱
            </Button>
            <label className="form-check form-check-inline mb-0 text-nowrap">
              <input type="checkbox" className="form-check-input" checked={onlyFavorites}
                onChange={(event) => setOnlyFavorites(event.target.checked)} />
              <span className="form-check-label">仅显示收藏</span>
            </label>
          </div>
        </section>
        <div className="erp-report-center-body p-3" style={{ overflow: 'auto' }}>
          {catalog.isPending ? <LoadingState label="正在加载报表目录…" /> : catalog.isError ? (
            <div className="alert alert-danger d-flex align-items-center justify-content-between">
              <span>{describeApiError(catalog.error, '报表目录加载失败。')}</span>
              <button type="button" className="btn btn-danger btn-sm" onClick={() => void catalog.refetch()}>重试</button>
            </div>
          ) : (
            <>
              {!onlyFavorites && !search.trim() && recentReports.length > 0 && (
                <section className="mb-4">
                  <h2 className="fs-6 fw-semibold text-secondary mb-2">最近使用</h2>
                  <div className="list-group list-group-flush">
                    {recentReports.map((item) => (
                      <div key={`recent:${item.moduleId}:${item.reportId}`} className="list-group-item list-group-item-action px-2 d-flex align-items-center gap-2 report-center-row">
                        <button type="button" className="btn btn-link p-0 text-decoration-none text-start flex-grow-1 report-center-link"
                          onClick={() => openReport(item)}>
                          <span className="fw-medium">{item.reportName}</span>
                          <span className="text-muted ms-2 small">{item.domainDesc || item.moduleDesc}</span>
                          <span className="text-muted ms-2 small font-monospace">{item.reportId}</span>
                        </button>
                        {item.favorite && <IconStarFilled size={16} className="text-warning" />}
                      </div>
                    ))}
                  </div>
                </section>
              )}
              {groups.length === 0 ? (
                <EmptyState title={onlyFavorites ? '暂无收藏报表' : '没有匹配的报表'} description={onlyFavorites ? '点击报表右侧的星标即可收藏。' : '请调整搜索关键字后重试。'} />
              ) : (
                groups.map(({ domain, reports }) => (
                  <section key={domain} className="mb-4">
                    <h2 className="fs-6 fw-semibold text-secondary mb-2">{domain}<span className="text-muted ms-2 small">（{reports.length}）</span></h2>
                    <div className="list-group list-group-flush">
                      {reports.map((item) => {
                        const favorites = (catalog.data ?? []).filter((it) => it.favorite)
                        const favIndex = favorites.findIndex((it) => it.moduleId === item.moduleId && it.reportId === item.reportId)
                        const canMoveUp = item.favorite && favIndex > 0
                        const canMoveDown = item.favorite && favIndex < favorites.length - 1
                        return (
                          <div key={`${item.moduleId}:${item.reportId}`} className="list-group-item list-group-item-action px-2 d-flex align-items-center gap-2 report-center-row">
                            <button type="button" className="btn btn-link p-0 text-decoration-none text-start flex-grow-1 report-center-link"
                              onClick={() => openReport(item)}>
                              <span className="fw-medium">{item.reportName}</span>
                              {item.isDefault && <span className="badge bg-primary-subtle text-primary ms-2 small">默认</span>}
                              <span className="text-muted ms-2 small font-monospace">{item.reportId}</span>
                            </button>
                            {item.favorite && (
                              <span className="d-inline-flex gap-1" aria-label="收藏排序">
                                <button type="button" className="btn btn-sm report-center-star" aria-label={`上移 ${item.reportName}`} title="上移" disabled={!canMoveUp || reorderMutation.isPending}
                                  onClick={() => moveFavorite(item, -1)}>
                                  <IconArrowUp size={14} />
                                </button>
                                <button type="button" className="btn btn-sm report-center-star" aria-label={`下移 ${item.reportName}`} title="下移" disabled={!canMoveDown || reorderMutation.isPending}
                                  onClick={() => moveFavorite(item, 1)}>
                                  <IconArrowDown size={14} />
                                </button>
                              </span>
                            )}
                            <button type="button"
                              className="btn btn-sm report-center-star"
                              aria-label={`自定义版式 ${item.reportName}`}
                              title="自定义版式"
                              onClick={() => navigate(`/layout-designer/${item.moduleId}`)}>
                              <IconPencil size={16} />
                            </button>
                            <button type="button"
                              className="btn btn-sm report-center-star"
                              aria-label={item.favorite ? `取消收藏 ${item.reportName}` : `收藏 ${item.reportName}`}
                              title={item.favorite ? '取消收藏' : '收藏'}
                              disabled={favoriteMutation.isPending}
                              onClick={() => favoriteMutation.mutate(item)}>
                              {item.favorite ? <IconStarFilled size={16} className="text-warning" /> : <IconStar size={16} />}
                            </button>
                          </div>
                        )
                      })}
                    </div>
                  </section>
                ))
              )}
            </>
          )}
        </div>
      </section>
    </div>
  )
}

export function ReportCenterErrorIcon() {
  return <IconX size={16} aria-hidden="true" />
}
