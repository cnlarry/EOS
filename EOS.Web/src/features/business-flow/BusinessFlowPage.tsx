import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import type { Edge } from '@xyflow/react'
import { useSearchParams } from 'react-router-dom'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpCommandBar } from '../../components/common/ErpCommandBar'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { Button } from '../../components/ui/Button'
import { useAuth } from '../../features/auth/authContext'
import { moduleReadPermission } from '../../features/auth/modulePermissions'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'
import { BusinessFlowCanvas, type FlowNodeSeed } from './BusinessFlowCanvas'
import {
  domainColor,
  domainEdgeStyle,
  domainLinkId,
  domainLinkLabel,
  domainNodeId,
  domainRootIdxOf,
  flowEdgeId,
  flowEdgeStyle,
  isEdgeVisible,
  isWeakDomainLink,
  matchIds,
  orderByRelation,
  relationKeyLabel,
  type FlowDomainGraph,
  type FlowOverview,
  type FlowTableEdge,
} from './flowGraph'
import './business-flow.css'

/** 业务域写进地址栏的参数：?domain=14 可直接打开，刷新、分享、后退都落在同一个域。 */
const DOMAIN_PARAM = 'domain'

/**
 * 业务流程图（/admin/business-flow，模块 2314）：
 * 两个数据源合成一张表间关系图，两级下钻——先看业务域总览，再进域内看表与表之间的关系。
 *
 * - FIELD_DATASOURCE（字段取值来源）回答"这个字段的值从哪来"，量大、含大量选基础资料；
 * - FIELD_RELATION（已登记单据关系，RELATION_KIND=EFFECT）回答"本单用键列关联到哪张单"，
 *   量小、集中在业务域，是真正意义上的单据上下游。两者实测零交集，故合并成一条边并标出 Registered。
 *
 * 边的域内/跨域只看两端是否同域——既有元数据里没有「主档 / 单据」标记，任何按表名或引用频次的
 * 自动判据都会误判，故只采用这条完全由既有元数据推导、无需维护的判据。
 */
export function BusinessFlowPage() {
  const { bootstrap } = useAuth()
  const [searchParams, setSearchParams] = useSearchParams()
  const [keyword, setKeyword] = useState('')
  const [showExternal, setShowExternal] = useState(false)
  const [includeUpstream, setIncludeUpstream] = useState(false)
  const [registeredOnly, setRegisteredOnly] = useState(false)
  const [majorOnly, setMajorOnly] = useState(false)
  const [selectedEdgeId, setSelectedEdgeId] = useState<string | null>(null)
  // layoutNonce 用作画布的 key：点「重新布局」即重新挂载画布，重算坐标并重置视口。
  const [layoutNonce, setLayoutNonce] = useState(0)

  // 当前业务域完全由 URL 决定，不再另存一份 state：否则刷新与分享都会丢域。
  const activeRootIdx = useMemo(() => {
    const raw = searchParams.get(DOMAIN_PARAM)
    return raw != null && /^\d+$/.test(raw) ? Number(raw) : null
  }, [searchParams])
  const inDomain = activeRootIdx != null

  const overview = useQuery({
    queryKey: ['business-flow-overview'],
    queryFn: () => apiClient.get<FlowOverview>('/admin/business-flow/overview'),
  })

  const domainGraph = useQuery({
    queryKey: ['business-flow-domain', activeRootIdx],
    queryFn: () => apiClient.get<FlowDomainGraph>(`/admin/business-flow/domains/${activeRootIdx}`),
    enabled: inDomain,
  })

  // 当前用户有读权限的模块：服务端 bootstrap 已按用户过滤，与菜单同一口径
  const allowedPermissions = useMemo(() => new Set(bootstrap?.permissions ?? []), [bootstrap])

  // 域配色：按编号升序分配固定色号——同一个域的颜色不能随布局排序漂移，否则"这个颜色是谁"根本记不住。
  const colorIndexOf = useMemo(() => {
    const domains = [...(overview.data?.domains ?? [])].sort((a, b) => a.rootIdx - b.rootIdx)
    return new Map(domains.map((domain, index) => [domain.rootIdx, index]))
  }, [overview.data])

  // 搜索：总览搜业务域，域内搜表代码 / 表名 / 模块名。Set 保持插入顺序，首个命中即定位目标。
  const highlightIds = useMemo(() => {
    if (!inDomain) {
      return matchIds(
        (overview.data?.domains ?? []).map((domain) => ({
          id: domainNodeId(domain.rootIdx),
          text: `${domain.name} ${domain.rootIdx}`,
        })),
        keyword,
      )
    }
    return matchIds(
      (domainGraph.data?.nodes ?? []).map((node) => ({
        id: node.tableId,
        text: `${node.tableId} ${node.tableName} ${node.moduleName}`,
      })),
      keyword,
    )
  }, [domainGraph.data, inDomain, keyword, overview.data])
  const focusNodeId = highlightIds.size > 0 ? [...highlightIds][0] : null

  const graph = useMemo(() => {
    if (!inDomain) return buildOverviewGraph(overview.data, majorOnly, registeredOnly, colorIndexOf)
    const data = domainGraph.data
    if (!data) return { nodes: [] as FlowNodeSeed[], edges: [] as Edge[] }
    return buildDomainGraph(data, allowedPermissions, showExternal, includeUpstream, registeredOnly)
  }, [
    allowedPermissions,
    colorIndexOf,
    domainGraph.data,
    includeUpstream,
    inDomain,
    majorOnly,
    overview.data,
    registeredOnly,
    showExternal,
  ])

  const tableNames = useMemo(
    () => new Map((domainGraph.data?.nodes ?? []).map((node) => [node.tableId, node.tableName || node.tableId])),
    [domainGraph.data],
  )
  const selectedEdge = useMemo(
    () =>
      selectedEdgeId == null
        ? null
        : ((domainGraph.data?.edges.find((edge) => flowEdgeId(edge) === selectedEdgeId) ?? null) as
            | FlowTableEdge
            | null),
    [domainGraph.data, selectedEdgeId],
  )

  /** 下钻/返回都只改 URL：同一份 state 不存两处，就不存在"显示的和地址栏不一致"。 */
  const setDomain = (rootIdx: number | null) => {
    const next = new URLSearchParams(searchParams)
    if (rootIdx == null) next.delete(DOMAIN_PARAM)
    else next.set(DOMAIN_PARAM, String(rootIdx))
    setSearchParams(next)
    // 跨域/上游开关是"看清某一张图"的手段，换域后一律收起，避免上一张图的视角残留
    setShowExternal(false)
    setIncludeUpstream(false)
    setSelectedEdgeId(null)
  }

  const activateNode = (nodeId: string) => {
    const rootIdx = domainRootIdxOf(nodeId)
    // 双击只在总览图上代表下钻；域内图里双击节点没有额外含义
    if (inDomain || rootIdx == null) return
    setDomain(rootIdx)
  }

  /**
   * 用浏览器新标签页打开统一工作台，不接管当前页。
   * 这张图是"我在查元数据"的工作现场（当前业务域、搜索词、选中的连线、拖过的排版），
   * 在同一标签页跳走就全丢了；副窗口打开还能把两边对着看。
   * 调用发生在点击事件里（用户手势内），不会被浏览器的弹窗拦截。
   */
  const openInNewTab = (routeUrl: string) => {
    window.open(routeUrl, '_blank', 'noopener')
  }

  const loading = overview.isPending || (inDomain && domainGraph.isPending)
  const failure = overview.error ?? (inDomain ? domainGraph.error : null)

  // 命令栏只放"动作"，视图开关放页头——动作与观察方式分开，命令栏就不会被开关塞满。
  const actions = (
    <ErpCommandBar
      ariaLabel="业务流程图工具"
      items={[
        { action: 'back', label: '返回总览', visible: inDomain, onClick: () => setDomain(null) },
        { action: 'refresh', label: '重新布局', onClick: () => setLayoutNonce((value) => value + 1) },
      ]}
    />
  )

  const toggle = (label: string, active: boolean, title: string, onClick: () => void) => (
    <Button
      size="sm"
      className="erp-flow-toggle"
      variant={active ? 'primary' : 'secondary'}
      title={title}
      aria-pressed={active}
      onClick={onClick}
    >
      {label}
    </Button>
  )

  const header = (
    <div className="erp-flow-header">
      <div className="erp-flow-heading">
        <strong>{inDomain ? (domainGraph.data?.name ?? `业务域 #${activeRootIdx}`) : '业务域总览'}</strong>
        <span className="text-secondary ms-2 small">
          {inDomain
            ? `悬停聚焦、单击进入对应模块；共 ${graph.nodes.length} 张表 · ${graph.edges.length} 条关系`
            : `悬停聚焦其流向，双击业务域进入内部 · 共 ${overview.data?.domains.length ?? 0} 个业务域`}
        </span>
      </div>
      <div className="erp-flow-header-tools">
        {inDomain
          ? [
              toggle('上游视角', includeUpstream, '除"本域引用了谁"外，再看"本域的表被谁引用"（含域外单据）', () =>
                setIncludeUpstream((value) => !value),
              ),
              toggle('跨域引用', showExternal, '显示 / 隐藏本域引用域外表的边（已登记单据关系不受此开关影响）', () =>
                setShowExternal((value) => !value),
              ),
            ]
          : [
              toggle('仅主要流向', majorOnly, '收起只有一处引用的弱连线，只看主要流向', () =>
                setMajorOnly((value) => !value),
              ),
            ]}
        {toggle('只看登记关系', registeredOnly, '只显示已登记单据关系（FIELD_RELATION），滤掉"选基础资料"那类字段引用', () =>
          setRegisteredOnly((value) => !value),
        )}
        <div className="erp-flow-legend">
          {inDomain ? (
            <>
              <span className="erp-flow-legend-item">
                <i className="erp-flow-legend-line" />
                域内流程
              </span>
              <span className="erp-flow-legend-item">
                <i className="erp-flow-legend-line erp-flow-legend-line--external" />
                跨域引用
              </span>
              <span className="erp-flow-legend-item">
                <i className="erp-flow-legend-line erp-flow-legend-line--registered" />
                已登记单据关系
              </span>
            </>
          ) : (
            <>
              <span className="erp-flow-legend-item">
                <i className="erp-flow-legend-line erp-flow-legend-line--multi" />
                线的颜色 = 上游业务域
              </span>
              <span className="erp-flow-legend-item">
                <i className="erp-flow-legend-line erp-flow-legend-line--registered" />
                粗线 = 含已登记单据关系
              </span>
              <span className="erp-flow-legend-item">
                <i className="erp-flow-legend-line erp-flow-legend-line--weak" />
                灰细线 = 仅一处引用
              </span>
            </>
          )}
        </div>
      </div>
    </div>
  )

  return (
    <ErpListCard
      search={
        <ErpSearchBox
          value={keyword}
          onChange={setKeyword}
          placeholder={inDomain ? '搜索表代码 / 表名' : '搜索业务域'}
          ariaLabel="搜索业务域或数据表"
        />
      }
      actions={actions}
      header={header}
      ariaLabel="业务流程图"
    >
      <div className="erp-flow-canvas">
        {failure ? (
          <ErrorState
            message={describeApiError(failure, '业务流程图数据加载失败。')}
            onRetry={() => {
              void overview.refetch()
              void domainGraph.refetch()
            }}
          />
        ) : loading ? (
          <LoadingState label="正在读取表间关系…" />
        ) : graph.nodes.length === 0 ? (
          <div className="erp-state">
            <span className="text-secondary">
              {inDomain
                ? '该业务域在当前视角下没有可展示的关系。试试打开「跨域引用」或「上游视角」，或关掉「只看登记关系」。'
                : '库里还没有配置表间关系，因此没有可展示的图。'}
            </span>
          </div>
        ) : (
          <div className="erp-flow-body">
            <BusinessFlowCanvas
              key={layoutNonce}
              nodes={graph.nodes}
              edges={graph.edges}
              layout={inDomain ? 'layered' : 'grid'}
              highlightIds={highlightIds}
              focusNodeId={focusNodeId}
              selectedEdgeId={selectedEdgeId}
              onActivateNode={activateNode}
              onOpenNode={openInNewTab}
              onEdgeSelect={setSelectedEdgeId}
            />
            {selectedEdge ? (
              <aside className="erp-flow-detail" aria-label="连线细节">
                <div className="erp-flow-detail-head">
                  <span className="erp-flow-detail-title">
                    {tableNames.get(selectedEdge.fromTable) ?? selectedEdge.fromTable}
                    <span className="erp-flow-detail-arrow" aria-hidden="true">
                      →
                    </span>
                    {tableNames.get(selectedEdge.toTable) ?? selectedEdge.toTable}
                  </span>
                  <Button size="sm" variant="ghost" title="收起细节" onClick={() => setSelectedEdgeId(null)}>
                    收起
                  </Button>
                </div>

                {selectedEdge.registered ? (
                  <section className="erp-flow-detail-section">
                    <div className="erp-flow-detail-label">已登记单据关系</div>
                    {selectedEdge.relationName ? (
                      <p className="erp-flow-detail-note">{selectedEdge.relationName}</p>
                    ) : null}
                    <p className="erp-flow-detail-note">键列（本单列 → 目标单列）</p>
                    <ul className="erp-flow-detail-list">
                      {selectedEdge.keyPairs.map((pair) => (
                        <li key={pair}>
                          <code>{pair}</code>
                        </li>
                      ))}
                    </ul>
                  </section>
                ) : null}

                <section className="erp-flow-detail-section">
                  <div className="erp-flow-detail-label">字段取值来源（{selectedEdge.fieldCount}）</div>
                  {selectedEdge.fields.length === 0 ? (
                    <p className="erp-flow-detail-note">这条关系不是字段取值，没有字段来源记录。</p>
                  ) : (
                    <ul className="erp-flow-detail-list">
                      {selectedEdge.fields.map((field) => (
                        <li key={field}>
                          <code>{field}</code>
                        </li>
                      ))}
                    </ul>
                  )}
                </section>

                <p className="erp-flow-detail-note">
                  上游：{selectedEdge.fromTable} · 下游：{selectedEdge.toTable}
                  {selectedEdge.upstreamInDomain && selectedEdge.downstreamInDomain ? '（两端都在本域）' : null}
                </p>
              </aside>
            ) : null}
          </div>
        )}
      </div>
    </ErpListCard>
  )
}

function buildOverviewGraph(
  data: FlowOverview | undefined,
  majorOnly: boolean,
  registeredOnly: boolean,
  colorIndexOf: Map<number, number>,
): { nodes: FlowNodeSeed[]; edges: Edge[] } {
  if (!data) return { nodes: [], edges: [] }

  // 取不到色号时退回用域编号本身当色号，结果同样稳定、不随布局漂移
  const colorOf = (rootIdx: number) => domainColor(colorIndexOf.get(rootIdx) ?? rootIdx)

  const links = data.links.filter((link) =>
    registeredOnly ? link.relationEdgeCount > 0 : !majorOnly || !isWeakDomainLink(link),
  )

  const edges: Edge[] = links.map((link) => ({
    id: domainLinkId(link),
    source: domainNodeId(link.fromRootIdx),
    target: domainNodeId(link.toRootIdx),
    label: domainLinkLabel(link, registeredOnly),
    ...domainEdgeStyle(link, colorOf(link.fromRootIdx)),
  }))

  // 只看登记关系时，没有登记关系的域会整片空着占位，把它们一并收起来。
  const connected = new Set(links.flatMap((link) => [domainNodeId(link.fromRootIdx), domainNodeId(link.toRootIdx)]))
  const candidates = data.domains.map((domain) => domainNodeId(domain.rootIdx))
  const ids = registeredOnly ? candidates.filter((id) => connected.has(id)) : candidates

  // 节点顺序决定网格里的邻接关系：先按引用聚类，再按聚类顺序填格，否则相关的域会离得很远。
  const order = orderByRelation(
    ids,
    links.map((link) => ({ source: domainNodeId(link.fromRootIdx), target: domainNodeId(link.toRootIdx) })),
  )
  const byId = new Map(data.domains.map((domain) => [domainNodeId(domain.rootIdx), domain]))

  const nodes: FlowNodeSeed[] = order.flatMap((id) => {
    const domain = byId.get(id)
    if (!domain) return []
    const relation = domain.relationEdgeCount > 0 ? ` · 登记 ${domain.relationEdgeCount}` : ''
    return [
      {
        id,
        type: 'flow' as const,
        data: {
          // 名称与编号并成一行：三行内容在固定高度的节点里会把首尾裁掉
          title: domain.rootIdx === 0 ? domain.name : `${domain.name}（#${domain.rootIdx}）`,
          code: '',
          meta: `${domain.tableCount} 张表 · 域内流程 ${domain.internalEdgeCount}${relation}`,
          // 跨域总量不再做成角标：它与边上那条"两域之间的引用数"表达同一件事，
          // 并列显示只会让画面更吵——该域的对外连接度从它引出的连线上一眼就能看出来。
          badge: '',
          variant: 'domain' as const,
          color: colorOf(domain.rootIdx),
        },
      },
    ]
  })

  return { nodes, edges }
}

function buildDomainGraph(
  data: FlowDomainGraph,
  allowedPermissions: Set<string>,
  showExternal: boolean,
  includeUpstream: boolean,
  registeredOnly: boolean,
): { nodes: FlowNodeSeed[]; edges: Edge[] } {
  const visible = data.edges.filter(
    (edge) => (!registeredOnly || edge.registered) && isEdgeVisible(edge, showExternal, includeUpstream),
  )

  const edges: Edge[] = visible.map((edge) => ({
    id: flowEdgeId(edge),
    source: edge.fromTable,
    target: edge.toTable,
    // 登记关系用键列做标签（关系名里的表名在两端节点上已写着，重复无信息量）
    ...(edge.registered ? { label: relationKeyLabel(edge.keyPairs) } : {}),
    ...flowEdgeStyle(edge.kind, edge.registered),
  }))

  // 折叠跨域引用后，只被跨域边连接的域外表会变成孤立点，一并过滤掉。
  const connected = new Set(edges.flatMap((edge) => [edge.source, edge.target]))
  const nodes: FlowNodeSeed[] = data.nodes
    .filter((node) => connected.has(node.tableId))
    .map((node) => ({
      id: node.tableId,
      type: 'flow' as const,
      data: {
        title: node.tableName || node.tableId,
        code: node.tableId,
        // 模块名多数与表名相同（表就是该模块的主表），单独占一行纯属重复；
        // 只有两者确实不同（如明细表挂在某单据模块下）时才补在下面一行。
        meta: node.moduleName && node.moduleName !== node.tableName ? node.moduleName : '',
        badge: node.selfReferenceCount > 0 ? `自引用 ${node.selfReferenceCount}` : '',
        variant: node.inDomain ? 'table' : 'table-external',
        // 入口只给"当前用户对该模块有读权限"的节点：服务端下发的 permissions 已按用户过滤过，
        // 与菜单同一口径——没权限的模块在图上同样不显示为可点入口，而不是点进去才被拒。
        routeUrl:
          node.routeUrl != null && node.moduleId != null && allowedPermissions.has(moduleReadPermission(node.moduleId))
            ? node.routeUrl
            : undefined,
      },
    }))
  return { nodes, edges }
}
