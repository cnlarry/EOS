import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import type { Edge } from '@xyflow/react'
import { useSearchParams } from 'react-router-dom'
import { ErpCommandBar } from '../../components/common/ErpCommandBar'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListGrid } from '../../components/common/ErpListGrid'
import { TabbedPanel, type TabbedPanelTab } from '../../components/common/TabbedPanel'
import { Button } from '../../components/ui/Button'
import { useAuth } from '../../features/auth/authContext'
import { moduleReadPermission } from '../../features/auth/modulePermissions'
import { apiClient } from '../../services/api'
import type { ColumnDef } from '../../lib/tanstackTable'
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
  linkRows,
  orderByRelation,
  parseDomainLinkId,
  relationKeyLabel,
  type FlowDomainGraph,
  type FlowLinkGraph,
  type FlowLinkRow,
  type FlowOverview,
  type FlowTableEdge,
} from './flowGraph'
import './business-flow.css'

/** 业务域写进地址栏的参数：?domain=14 可直接打开，刷新、分享、后退都落在同一个域。 */
const DOMAIN_PARAM = 'domain'

/**
 * 第三级的两种看法（页签）：**视图**回答"这两个模块怎么串起来的"（图的强项），
 * **列表**回答"到底有哪几条、靠哪一列"（表的强项，可排序可复制）。
 * 两者读同一份数据、共用页头那句摘要，不会出现图里 5 条表里 3 条。
 */
type LinkTab = 'view' | 'list'
const LINK_TABS: TabbedPanelTab<LinkTab>[] = [
  { key: 'view', label: '视图' },
  { key: 'list', label: '列表' },
]
/** 第三级「两个模块之间」写进地址栏的对端域：?domain=14&peer=16 表示"域 14 引用到域 16"。 */
const PEER_PARAM = 'peer'

/**
 * 业务流程图（/admin/business-flow，模块 2314）：
 * 两个数据源合成一张表间关系图，三级下钻——业务域总览 → 域内表间关系 → 两个域之间的关系明细。
 *
 * - FIELD_DATASOURCE（字段取值来源）回答"这个字段的值从哪来"，量大、含大量选基础资料；
 * - FIELD_RELATION（已登记单据关系，RELATION_KIND=EFFECT）回答"本单用键列关联到哪张单"，
 *   量小、集中在业务域，是真正意义上的单据上下游。两者在同一对表上会重合，故合并成一条边并标出 Registered。
 *
 * 第三级从总览图上那条带数字的连线进入：线上写 "6+3"，点进去是这两个模块之间的逐条关系
 * （上游表 / 下游表 / 关系类型 / 引用字段 / 登记键列）。行数是两类事实的并集——
 * 同一对表双重身份时只占一行、两个标签并列，重合条数在标题上写明，免得数字对不上。
 *
 * 边的域内/跨域只看两端是否同域——既有元数据里没有「主档 / 单据」标记，任何按表名或引用频次的
 * 自动判据都会误判，故只采用这条完全由既有元数据推导、无需维护的判据。
 */
export function BusinessFlowPage() {
  const { bootstrap } = useAuth()
  const [searchParams, setSearchParams] = useSearchParams()
  const [showExternal, setShowExternal] = useState(false)
  const [includeUpstream, setIncludeUpstream] = useState(false)
  const [registeredOnly, setRegisteredOnly] = useState(false)
  const [majorOnly, setMajorOnly] = useState(false)
  const [selectedEdgeId, setSelectedEdgeId] = useState<string | null>(null)
  // 第三级的页签：默认进视图（模型 2314 的第三级最常问的是"这两个模块怎么串起来的"）
  const [linkTab, setLinkTab] = useState<LinkTab>('view')
  // layoutNonce 用作画布的 key：点「重新布局」即重新挂载画布，重算坐标并重置视口。
  const [layoutNonce, setLayoutNonce] = useState(0)

  // 当前业务域完全由 URL 决定，不再另存一份 state：否则刷新与分享都会丢域。
  const activeRootIdx = useMemo(() => {
    const raw = searchParams.get(DOMAIN_PARAM)
    return raw != null && /^\d+$/.test(raw) ? Number(raw) : null
  }, [searchParams])
  // 对端域只与 domain 一起出现：两个都在即为第三级（两个模块之间的关系）
  const peerRootIdx = useMemo(() => {
    const raw = searchParams.get(PEER_PARAM)
    return raw != null && /^\d+$/.test(raw) ? Number(raw) : null
  }, [searchParams])
  const inLink = activeRootIdx != null && peerRootIdx != null
  // 第二级（域内明细图）：进了某个域、但还没下钻到某一对域之间
  const inDomain = activeRootIdx != null && !inLink

  const overview = useQuery({
    queryKey: ['business-flow-overview'],
    queryFn: () => apiClient.get<FlowOverview>('/admin/business-flow/overview'),
  })

  const domainGraph = useQuery({
    queryKey: ['business-flow-domain', activeRootIdx],
    queryFn: () => apiClient.get<FlowDomainGraph>(`/admin/business-flow/domains/${activeRootIdx}`),
    enabled: inDomain,
  })

  const linkGraph = useQuery({
    queryKey: ['business-flow-link', activeRootIdx, peerRootIdx],
    queryFn: () => apiClient.get<FlowLinkGraph>(`/admin/business-flow/domains/${activeRootIdx}/links/${peerRootIdx}`),
    enabled: inLink,
  })

  // 当前用户有读权限的模块：服务端 bootstrap 已按用户过滤，与菜单同一口径
  const allowedPermissions = useMemo(() => new Set(bootstrap?.permissions ?? []), [bootstrap])

  // 域配色：按编号升序分配固定色号——同一个域的颜色不能随布局排序漂移，否则"这个颜色是谁"根本记不住。
  const colorIndexOf = useMemo(() => {
    const domains = [...(overview.data?.domains ?? [])].sort((a, b) => a.rootIdx - b.rootIdx)
    return new Map(domains.map((domain, index) => [domain.rootIdx, index]))
  }, [overview.data])

  // 第三级「只看登记关系」先在数据上过滤：画布与下面的明细表始终是同一批关系，
  // 不会出现"图上有 5 条、表里 3 条"这种对不上的情况。
  const linkData = useMemo(() => {
    const data = linkGraph.data
    if (!data) return undefined
    return registeredOnly ? { ...data, edges: data.edges.filter((edge) => edge.registered) } : data
  }, [linkGraph.data, registeredOnly])
  const linkDetailRows = useMemo(() => linkRows(linkData), [linkData])
  const linkTableCounts = useMemo(() => {
    const tables = linkTables(linkData)
    return {
      from: tables.filter((table) => table.inFromDomain).length,
      to: tables.filter((table) => !table.inFromDomain).length,
    }
  }, [linkData])
  /**
   * 同时是两类事实的关系条数：线上写的是"字段引用 6 + 登记 3"两个独立计数，
   * 而这里是合并后的行数，重合的那几条会让两个数字对不上。不说明白，用户会以为少列了。
   */
  const linkDualCount = useMemo(
    () => linkDetailRows.filter((row) => row.registered && row.fieldCount > 0).length,
    [linkDetailRows],
  )

  const graph = useMemo(() => {
    if (inLink) return buildLinkGraph(linkData, colorIndexOf)
    if (inDomain) {
      const data = domainGraph.data
      if (!data) return { nodes: [] as FlowNodeSeed[], edges: [] as Edge[] }
      return buildDomainGraph(data, allowedPermissions, showExternal, includeUpstream, registeredOnly)
    }
    return buildOverviewGraph(overview.data, majorOnly, registeredOnly, colorIndexOf)
  }, [
    allowedPermissions,
    colorIndexOf,
    domainGraph.data,
    includeUpstream,
    inDomain,
    inLink,
    linkData,
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

  /**
   * 第三级的关系明细表。上游表 / 下游表两列把表代码放前面、表名补在后面——
   * 抓代码的人与认名字的人都能一眼对上；"关系类型"是标签而不是单选，
   * 因为同一对表可能既是字段取值来源、又是已登记单据关系。
   */
  const linkColumns = useMemo<ColumnDef<FlowLinkRow, unknown>[]>(() => [
    {
      accessorKey: 'fromTable',
      header: '上游表',
      cell: ({ row }) => (
        <>
          <span className="font-monospace">{row.original.fromTable}</span>
          {row.original.fromTableName && row.original.fromTableName !== row.original.fromTable
            ? <span className="text-secondary ms-1">{row.original.fromTableName}</span>
            : null}
        </>
      ),
    },
    {
      accessorKey: 'toTable',
      header: '下游表',
      cell: ({ row }) => (
        <>
          <span className="font-monospace">{row.original.toTable}</span>
          {row.original.toTableName && row.original.toTableName !== row.original.toTable
            ? <span className="text-secondary ms-1">{row.original.toTableName}</span>
            : null}
        </>
      ),
    },
    {
      id: 'relationKinds',
      header: '关系类型',
      enableSorting: false,
      meta: { truncate: false },
      cell: ({ row }) => (
        <span className="d-inline-flex flex-wrap gap-1">
          {row.original.relationKinds.map((kind) => (
            <span
              key={kind}
              className={`badge ${kind === '已登记单据关系' ? 'bg-teal-lt' : 'bg-azure-lt'}`}
              title={kind === '已登记单据关系' ? (row.original.relationName ?? undefined) : undefined}
            >
              {kind}
            </span>
          ))}
        </span>
      ),
    },
    {
      accessorKey: 'fields',
      header: '引用字段',
      cell: (info) => info.getValue()
        ? <code className="text-secondary small">{String(info.getValue())}</code>
        : <span className="text-secondary">—</span>,
    },
    {
      accessorKey: 'keyPairs',
      header: '登记键列',
      cell: (info) => info.getValue()
        ? <code className="small">{String(info.getValue())}</code>
        : <span className="text-secondary">—</span>,
    },
  ], [])

  /**
   * 下钻/返回都只改 URL：同一份 state 不存两处，就不存在"显示的和地址栏不一致"。
   * 第三级就是把对端域一并写进地址（`?domain=14&peer=16`），故它与第二级共用同一个入口。
   */
  const setDomain = (rootIdx: number | null, peerIdx: number | null = null) => {
    const next = new URLSearchParams(searchParams)
    if (rootIdx == null) next.delete(DOMAIN_PARAM)
    else next.set(DOMAIN_PARAM, String(rootIdx))
    if (peerIdx == null) next.delete(PEER_PARAM)
    else next.set(PEER_PARAM, String(peerIdx))
    setSearchParams(next)
    // 跨域/上游开关是"看清某一张图"的手段，换域后一律收起，避免上一张图的视角残留
    setShowExternal(false)
    setIncludeUpstream(false)
    setSelectedEdgeId(null)
    // 第三级的页签同理：换一对模块就回到视图，不把上一对的看法带过来
    setLinkTab('view')
  }

  /** 点总览图上那条带数字的连线：进入"这两个模块之间"的关系明细。 */
  const openDomainLink = (fromRootIdx: number, toRootIdx: number) => setDomain(fromRootIdx, toRootIdx)

  /**
   * 点连线：总览图上每条线都是"两个域之间"，点它就下钻到那两个域之间的关系明细；
   * 域内图上的线是表与表之间，点它只是选中、在右侧看细节。两种 id 形状不同，故一处分流即可。
   */
  const handleEdgeSelect = (edgeId: string | null) => {
    if (edgeId == null) {
      setSelectedEdgeId(null)
      return
    }
    const link = parseDomainLinkId(edgeId)
    if (link) openDomainLink(link.fromRootIdx, link.toRootIdx)
    else setSelectedEdgeId(edgeId)
  }

  const activateNode = (nodeId: string) => {
    const rootIdx = domainRootIdxOf(nodeId)
    // 双击只在总览图上代表下钻；域内图里双击节点没有额外含义
    if (inDomain || rootIdx == null) return
    setDomain(rootIdx)
  }

  /**
   * 用浏览器新标签页打开统一工作台，不接管当前页。
   * 这张图是"我在查元数据"的工作现场（当前业务域、选中的连线、拖过的排版），
   * 在同一标签页跳走就全丢了；副窗口打开还能把两边对着看。
   * 调用发生在点击事件里（用户手势内），不会被浏览器的弹窗拦截。
   */
  const openInNewTab = (routeUrl: string) => {
    window.open(routeUrl, '_blank', 'noopener')
  }

  const loading = overview.isPending || (inDomain && domainGraph.isPending) || (inLink && linkGraph.isPending)
  const failure = overview.error ?? (inDomain ? domainGraph.error : null) ?? (inLink ? linkGraph.error : null)

  // 命令栏只放"动作"，视图开关放页头——动作与观察方式分开，命令栏就不会被开关塞满。
  const actions = (
    <ErpCommandBar
      ariaLabel="业务流程图工具"
      items={[
        { action: 'back', label: '返回总览', visible: activeRootIdx != null, onClick: () => setDomain(null, null) },
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
        <strong>
          {inLink
            ? `${linkData?.fromName ?? `业务域 #${activeRootIdx}`} → ${linkData?.toName ?? `业务域 #${peerRootIdx}`}`
            : inDomain
              ? (domainGraph.data?.name ?? `业务域 #${activeRootIdx}`)
              : '业务域总览'}
        </strong>
        <span className="text-secondary ms-2 small">
          {inLink
            ? `共 ${linkDetailRows.length} 条关系${linkDualCount > 0 ? `（其中 ${linkDualCount} 条同时是已登记单据关系，故比连线上的两个数字之和少）` : ''} · 上游 ${linkTableCounts.from} 张表 · 下游 ${linkTableCounts.to} 张表`
            : inDomain
              ? `悬停聚焦、单击进入对应模块；共 ${graph.nodes.length} 张表 · ${graph.edges.length} 条关系`
              : `悬停聚焦其流向，双击业务域进入内部，单击连线看两域之间的关系 · 共 ${overview.data?.domains.length ?? 0} 个业务域`}
        </span>
      </div>
      <div className="erp-flow-header-tools">
        {/* 第三级没有卡片命令栏（页签面板本身就是容器），返回/重新布局并进页头这一行 */}
        {inLink ? actions : null}
        {inLink
          // 第三级只有"这两个域之间"的关系，域内/跨域与上下游视角都不适用
          ? null
          : inDomain
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
          {inLink ? (
            <>
              <span className="erp-flow-legend-item">
                <i className="erp-flow-legend-line" />
                字段取值来源
              </span>
              <span className="erp-flow-legend-item">
                <i className="erp-flow-legend-line erp-flow-legend-line--registered" />
                已登记单据关系
              </span>
            </>
          ) : inDomain ? (
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

  // 三级共用的图区：加载 / 错误 / 空态 / 画布（第三级还分视图与列表两个页签）
  const graphArea = failure ? (
    <ErrorState
      message={describeApiError(failure, '业务流程图数据加载失败。')}
      onRetry={() => {
        void overview.refetch()
        void domainGraph.refetch()
        void linkGraph.refetch()
      }}
    />
  ) : loading ? (
    <LoadingState label="正在读取表间关系…" />
  ) : inLink ? (
    linkTab === 'list' ? (
      <ErpListGrid
        plain
        listId="business-flow-link-edges"
        columns={linkColumns}
        rows={linkDetailRows}
        rowKey={(row) => row.id}
        sorting={{ mode: 'client' }}
        empty={<div className="erp-state"><span className="text-secondary">这两个业务域之间没有关系。</span></div>}
      />
    ) : graph.nodes.length === 0 ? (
      <div className="erp-state">
        <span className="text-secondary">这两个业务域之间没有符合当前过滤条件的关系。</span>
      </div>
    ) : (
      // React Flow 要确定高度：这一层由 flex 吃掉面板剩余高度（见 business-flow.css）
      <div className="erp-flow-link-canvas">
        <BusinessFlowCanvas key={layoutNonce} nodes={graph.nodes} edges={graph.edges} layout="layered" />
      </div>
    )
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
        selectedEdgeId={selectedEdgeId}
        onActivateNode={activateNode}
        onOpenNode={openInNewTab}
        onEdgeSelect={handleEdgeSelect}
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
  )

  return (
    <div className="erp-flow-page">
      {inLink ? (
        /* 第三级：页签面板本身就是容器（标签行 + 一圈边框的内容面板），外面不再套卡片——
           套一层会出现"卡片边框 + 面板边框"叠成粗边框的观感。页头（标题 / 摘要 / 动作 / 开关 / 图例）
           在面板内容里，与画布、列表同属一个框；两个页签读同一份过滤后的数据。 */
        <TabbedPanel
          label="域间关系视图"
          className="erp-flow-link-panel"
          tabs={LINK_TABS}
          activeKey={linkTab}
          onActiveKeyChange={setLinkTab}
        >
          {header}
          {graphArea}
        </TabbedPanel>
      ) : (
        <ErpListCard actions={actions} header={header} ariaLabel="业务流程图">
          {graphArea}
        </ErpListCard>
      )}
    </div>
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

/** 第三级图上出现的表：上游域与下游域各若干张，按表代码去重。 */
function linkTables(data: FlowLinkGraph | undefined): { tableId: string; tableName: string; inFromDomain: boolean }[] {
  const tables = new Map<string, { tableId: string; tableName: string; inFromDomain: boolean }>()
  for (const edge of data?.edges ?? []) {
    if (!tables.has(edge.fromTable)) {
      tables.set(edge.fromTable, { tableId: edge.fromTable, tableName: edge.fromTableName, inFromDomain: true })
    }
    if (!tables.has(edge.toTable)) {
      tables.set(edge.toTable, { tableId: edge.toTable, tableName: edge.toTableName, inFromDomain: false })
    }
  }
  return [...tables.values()]
}

/**
 * 第三级图：只画这一对业务域之间的表与关系。
 *
 * 两端靠外观与颜色区分——上游域的表按本域表画（实线），下游域的表按域外表画（虚线），
 * 左侧色条取该域在总览上的颜色，于是"这条线是从哪边引过来的"在图上直接看得出来。
 * 这一级不提供"打开模块"入口：节点入口是第二级的事，这里只回答"两个模块之间有什么"。
 */
function buildLinkGraph(
  data: FlowLinkGraph | undefined,
  colorIndexOf: Map<number, number>,
): { nodes: FlowNodeSeed[]; edges: Edge[] } {
  if (!data) return { nodes: [], edges: [] }
  // 取不到色号时退回用域编号本身当色号，结果同样稳定、不随布局漂移
  const colorOf = (rootIdx: number) => domainColor(colorIndexOf.get(rootIdx) ?? rootIdx)

  const edges: Edge[] = data.edges.map((edge) => ({
    id: flowEdgeId(edge),
    source: edge.fromTable,
    target: edge.toTable,
    // 登记关系用键列做标签（关系名里的表名在两端节点上已写着，重复无信息量）
    ...(edge.registered ? { label: relationKeyLabel(edge.keyPairs) } : {}),
    // kind 恒按域内流程画（蓝实线）：这一级的轴是"已登记 / 仅字段引用"，不是"域内 / 跨域"
    ...flowEdgeStyle('internal', edge.registered),
  }))

  const nodes: FlowNodeSeed[] = linkTables(data).map((table) => ({
    id: table.tableId,
    type: 'flow' as const,
    data: {
      title: table.tableName || table.tableId,
      code: table.tableId,
      meta: '',
      badge: '',
      variant: table.inFromDomain ? 'table' : 'table-external',
      color: colorOf(table.inFromDomain ? data.fromRootIdx : data.toRootIdx),
    },
  }))
  return { nodes, edges }
}
