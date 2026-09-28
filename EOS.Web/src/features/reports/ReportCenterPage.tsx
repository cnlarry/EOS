import {
  IconArrowDown,
  IconArrowUp,
  IconArrowsSort,
  IconCheck,
  IconChevronDown,
  IconChevronRight,
  IconInbox,
  IconPencil,
  IconStar,
  IconStarFilled,
} from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useId, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
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

/** 目录项在页面内的唯一键（模块 + 报表编号）。 */
function itemKey(item: ReportCatalogItem): string {
  return `${item.moduleId}:${item.reportId}`
}

/** 收藏顺序未显式设置过的项排在显式排序项之后。 */
function sortRank(item: ReportCatalogItem): number {
  return item.sortIndex > 0 ? item.sortIndex : Number.MAX_SAFE_INTEGER
}

/** 最近使用的展示条数。 */
const RECENT_LIMIT = 6

/**
 * 报表中心目录页。
 *
 * 形态是「导航目录」而非数据列表，因此分区组织：
 * 收藏（磁贴，独立于业务域的排序）→ 最近使用（紧凑行）→ 全部报表（按业务域折叠的索引）。
 * 搜索或「仅显示收藏」生效时只呈现过滤结果，分组自动展开为平铺结果。
 */
export function ReportCenterPage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const baseId = useId()
  const [search, setSearch] = useState('')
  const [onlyFavorites, setOnlyFavorites] = useState(false)
  const [sortMode, setSortMode] = useState(false)
  const [expandedDomains, setExpandedDomains] = useState<Record<string, boolean>>({})

  const catalog = useQuery({
    queryKey: ['report-center', 'catalog'],
    queryFn: () => apiClient.get<ReportCatalogItem[]>('/report-center/catalog'),
  })

  const items = useMemo(() => catalog.data ?? [], [catalog.data])

  // 收藏按 SORT_IDX 单独排序：目录接口的返回顺序是「业务域优先」，
  // 直接沿用会让跨业务域的收藏顺序不可见，上移/下移也就成了空操作。
  const favorites = useMemo(() => items
    .filter((item) => item.favorite)
    .sort((a, b) => sortRank(a) - sortRank(b) || a.reportName.localeCompare(b.reportName, 'zh-CN')),
  [items])

  const refreshCatalog = useCallback(
    () => void queryClient.invalidateQueries({ queryKey: ['report-center', 'catalog'] }),
    [queryClient],
  )

  const favoriteMutation = useMutation({
    mutationFn: (item: ReportCatalogItem) => apiClient.post('/report-center/favorite', {
      moduleId: item.moduleId,
      reportId: item.reportId,
      favorite: !item.favorite,
      // 加入收藏时追加到末尾；取消收藏不提交该字段，服务端保持既有顺序
      sortIndex: item.favorite ? undefined : favorites.length + 1,
    }),
    onSuccess: refreshCatalog,
  })

  const reorderMutation = useMutation({
    mutationFn: (ordered: { moduleId: number; reportId: string }[]) =>
      apiClient.post('/report-center/reorder', { items: ordered }),
    onSuccess: refreshCatalog,
  })

  const favoriteIndex = useMemo(() => {
    const map = new Map<string, number>()
    favorites.forEach((item, index) => map.set(itemKey(item), index))
    return map
  }, [favorites])

  const recentReports = useMemo(
    () => items
      .filter((item) => item.lastRunAt != null)
      .sort((a, b) => new Date(b.lastRunAt!).getTime() - new Date(a.lastRunAt!).getTime())
      .slice(0, RECENT_LIMIT),
    [items],
  )

  const groups = useMemo(() => {
    const text = search.trim().toLowerCase()
    const filtered = items.filter((item) => {
      if (onlyFavorites && !item.favorite) return false
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
  }, [items, search, onlyFavorites])

  const domainCount = useMemo(
    () => new Set(items.map((item) => item.domainDesc || item.moduleDesc || '其它')).size,
    [items],
  )

  // 搜索/仅收藏时结果应当直接可见，此时分组不再折叠
  const filtering = search.trim() !== '' || onlyFavorites
  // 只有一个业务域时没有可浏览的索引，直接展开
  const singleDomain = groups.length === 1
  const allExpanded = groups.length > 0 && groups.every((group) => expandedDomains[group.domain] === true)

  const openReport = (item: ReportCatalogItem) => {
    void apiClient.post('/report-center/touch', {
      moduleId: item.moduleId,
      reportId: item.reportId,
    }).catch(() => undefined)
    // 打开的是**这张报表**，不是"这个模块的默认报表"：地址带身份，才能深链、分享、收藏到具体一张
    navigate(`/report/${encodeURIComponent(item.reportId)}`)
  }

  const moveFavorite = (item: ReportCatalogItem, direction: -1 | 1) => {
    const index = favoriteIndex.get(itemKey(item)) ?? -1
    const target = index < 0 ? undefined : favorites[index + direction]
    if (index < 0 || !target) return
    const next = [...favorites]
    next[index] = target
    next[index + direction] = item
    reorderMutation.mutate(next.map((it) => ({ moduleId: it.moduleId, reportId: it.reportId })))
  }

  /** 报表条目上的次级动作（自定义版式 / 收藏开关），磁贴与紧凑行共用。 */
  const renderItemActions = (item: ReportCatalogItem) => (
    <>
      <button
        type="button"
        className="erp-catalog-tile-btn"
        aria-label={`自定义版式 ${item.reportName}`}
        title="自定义打印版式"
        onClick={() => navigate(`/layout-designer/${item.moduleId}`)}
      >
        <IconPencil size={14} />
      </button>
      <button
        type="button"
        className={`erp-catalog-tile-btn${item.favorite ? ' is-favorite' : ''}`}
        aria-label={item.favorite ? `取消收藏 ${item.reportName}` : `收藏 ${item.reportName}`}
        title={item.favorite ? '取消收藏' : '收藏'}
        disabled={favoriteMutation.isPending}
        onClick={() => favoriteMutation.mutate(item)}
      >
        {item.favorite ? <IconStarFilled size={14} /> : <IconStar size={14} />}
      </button>
    </>
  )

  return (
    <div className="erp-catalog-page">
      <section className="card erp-catalog-card">
        <header className="erp-catalog-head">
          <div className="erp-catalog-title">
            <strong>报表中心</strong>
            <span>共 {items.length} 张报表 · {domainCount} 个业务域</span>
          </div>
          <ErpSearchBox
            value={search}
            onChange={setSearch}
            placeholder="搜索报表名称 / 编号 / 业务域"
            ariaLabel="搜索报表"
          />
          <div className="erp-catalog-actions">
            <Button variant="ghost" size="sm" icon={<IconInbox size={16} />} onClick={() => navigate('/report-center/inbox')}>
              收件箱
            </Button>
            <Button
              variant={onlyFavorites ? 'primary' : 'ghost'}
              size="sm"
              icon={onlyFavorites ? <IconStarFilled size={16} /> : <IconStar size={16} />}
              aria-pressed={onlyFavorites}
              aria-label="仅显示收藏"
              title="仅显示收藏"
              onClick={() => setOnlyFavorites((current) => !current)}
            >
              仅显示收藏
            </Button>
          </div>
        </header>

        <div className="erp-catalog-body">
          {catalog.isPending ? (
            <LoadingState label="正在加载报表目录…" />
          ) : catalog.isError ? (
            <ErrorState
              message={describeApiError(catalog.error, '报表目录加载失败。')}
              onRetry={() => void catalog.refetch()}
            />
          ) : (
            <>
              {!filtering && (
                <section className="erp-catalog-section" aria-label="我的收藏">
                  <div className="erp-catalog-section-head">
                    <h2>我的收藏</h2>
                    <span className="erp-catalog-count">{favorites.length}</span>
                    {favorites.length > 1 && (
                      <div className="erp-catalog-section-actions">
                        <Button
                          variant={sortMode ? 'primary' : 'ghost'}
                          size="sm"
                          icon={sortMode ? <IconCheck size={16} /> : <IconArrowsSort size={16} />}
                          aria-pressed={sortMode}
                          onClick={() => setSortMode((current) => !current)}
                        >
                          {sortMode ? '完成排序' : '编辑排序'}
                        </Button>
                      </div>
                    )}
                  </div>
                  {favorites.length === 0 ? (
                    <div className="erp-catalog-hint">还没有收藏。在任意报表上点击星标，即可固定到这里。</div>
                  ) : (
                    <div className="erp-catalog-grid">
                      {favorites.map((item) => {
                        const index = favoriteIndex.get(itemKey(item)) ?? -1
                        return (
                          <div className="erp-catalog-tile" key={`favorite:${itemKey(item)}`}>
                            <button
                              type="button"
                              className="erp-catalog-tile-open"
                              title={`打开 ${item.reportName}`}
                              onClick={() => openReport(item)}
                            >
                              <span className="erp-catalog-tile-name">{item.reportName}</span>
                              <span className="erp-catalog-tile-meta">
                                {item.domainDesc || item.moduleDesc}
                                {item.isDefault ? ' · 默认' : ''}
                              </span>
                            </button>
                            <span className="erp-catalog-tile-actions" data-pinned={sortMode ? 'true' : undefined}>
                              {sortMode && (
                                <>
                                  <button
                                    type="button"
                                    className="erp-catalog-tile-btn"
                                    aria-label={`上移 ${item.reportName}`}
                                    title="上移"
                                    disabled={index <= 0 || reorderMutation.isPending}
                                    onClick={() => moveFavorite(item, -1)}
                                  >
                                    <IconArrowUp size={14} />
                                  </button>
                                  <button
                                    type="button"
                                    className="erp-catalog-tile-btn"
                                    aria-label={`下移 ${item.reportName}`}
                                    title="下移"
                                    disabled={index < 0 || index >= favorites.length - 1 || reorderMutation.isPending}
                                    onClick={() => moveFavorite(item, 1)}
                                  >
                                    <IconArrowDown size={14} />
                                  </button>
                                </>
                              )}
                              {renderItemActions(item)}
                            </span>
                          </div>
                        )
                      })}
                    </div>
                  )}
                </section>
              )}

              {!filtering && recentReports.length > 0 && (
                <section className="erp-catalog-section" aria-label="最近使用">
                  <div className="erp-catalog-section-head">
                    <h2>最近使用</h2>
                    <span className="erp-catalog-count">{recentReports.length}</span>
                  </div>
                  <div className="erp-catalog-rows">
                    {recentReports.map((item) => (
                      <div className="erp-catalog-row" key={`recent:${itemKey(item)}`}>
                        <button
                          type="button"
                          className="erp-catalog-row-open"
                          title={`打开 ${item.reportName}`}
                          onClick={() => openReport(item)}
                        >
                          <span className="erp-catalog-row-name">{item.reportName}</span>
                          <span className="erp-catalog-row-meta">{item.domainDesc || item.moduleDesc}</span>
                        </button>
                        <span className="erp-catalog-row-tail">{formatRunTime(item.lastRunAt)}</span>
                      </div>
                    ))}
                  </div>
                </section>
              )}

              <section className="erp-catalog-section" aria-label="全部报表">
                <div className="erp-catalog-section-head">
                  <h2>全部报表</h2>
                  <span className="erp-catalog-count">{groups.length} 个业务域</span>
                  {!filtering && !singleDomain && (
                    <div className="erp-catalog-section-actions">
                      <Button
                        variant="ghost"
                        size="sm"
                        icon={allExpanded ? <IconChevronDown size={16} /> : <IconChevronRight size={16} />}
                        onClick={() => setExpandedDomains(
                          allExpanded ? {} : Object.fromEntries(groups.map((group) => [group.domain, true])),
                        )}
                      >
                        {allExpanded ? '折叠全部' : '展开全部'}
                      </Button>
                    </div>
                  )}
                </div>

                {groups.length === 0 ? (
                  <div className="erp-catalog-empty">
                    <EmptyState
                      title={onlyFavorites ? '暂无收藏报表' : '没有匹配的报表'}
                      description={onlyFavorites ? '点击报表右侧的星标即可收藏。' : '请调整搜索关键字后重试。'}
                    />
                  </div>
                ) : (
                  groups.map(({ domain, reports }, groupIndex) => {
                    const expanded = filtering || singleDomain || expandedDomains[domain] === true
                    const bodyId = `${baseId}-domain-${groupIndex}`
                    return (
                      <section className="erp-catalog-group" key={domain}>
                        {filtering || singleDomain ? (
                          <div className="erp-catalog-group-title">
                            <strong>{domain}</strong>
                            <span className="erp-catalog-count">{reports.length}</span>
                          </div>
                        ) : (
                          <button
                            type="button"
                            className="erp-catalog-group-title"
                            aria-expanded={expanded}
                            aria-controls={bodyId}
                            title={expanded ? `折叠 ${domain}` : `展开 ${domain}`}
                            onClick={() => setExpandedDomains((current) => ({ ...current, [domain]: !expanded }))}
                          >
                            {expanded ? <IconChevronDown size={16} /> : <IconChevronRight size={16} />}
                            <strong>{domain}</strong>
                            <span className="erp-catalog-count">{reports.length}</span>
                          </button>
                        )}
                        {expanded && (
                          <div className="erp-catalog-group-body" id={bodyId}>
                            {reports.map((item) => (
                              <div className="erp-catalog-row" key={itemKey(item)}>
                                <button
                                  type="button"
                                  className="erp-catalog-row-open"
                                  title={`打开 ${item.reportName}`}
                                  onClick={() => openReport(item)}
                                >
                                  <span className="erp-catalog-row-name">{item.reportName}</span>
                                  {item.isDefault && <span className="erp-catalog-badge">默认</span>}
                                  <span className="erp-catalog-row-meta">{item.reportId}</span>
                                </button>
                                <span className="erp-catalog-row-tail">{renderItemActions(item)}</span>
                              </div>
                            ))}
                          </div>
                        )}
                      </section>
                    )
                  })
                )}
              </section>
            </>
          )}
        </div>
      </section>
    </div>
  )
}

/** 最近使用的时间：只到分钟，避免紧凑行被完整 Locale 串撑开。 */
function formatRunTime(value: string | null): string {
  if (!value) return ''
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return ''
  return date.toLocaleString('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' })
}
