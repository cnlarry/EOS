import dagre from '@dagrejs/dagre'
import { MarkerType, type Edge } from '@xyflow/react'

/** 业务域：表的归属模块的根模块。rootIdx 为 0 表示未归属任何模块的表。 */
export interface FlowDomain {
  rootIdx: number
  name: string
  tableCount: number
  internalEdgeCount: number
  outEdgeCount: number
  inEdgeCount: number
  /** 已登记单据关系边里「引用方在本域」的条数。 */
  relationEdgeCount: number
}

/**
 * 域间连线：上游来源域 → 下游引用域。
 * 两个计数出自两个互不重叠的数据源（实测零交集），因此分开保留而不是合成一个数：
 * `edgeCount` 是字段取值来源（含大量"选基础资料"），`relationEdgeCount` 是已登记单据关系。
 */
export interface FlowDomainLink {
  fromRootIdx: number
  toRootIdx: number
  edgeCount: number
  relationEdgeCount: number
}

export interface FlowOverview {
  domains: FlowDomain[]
  links: FlowDomainLink[]
}

export interface FlowTableNode {
  tableId: string
  tableName: string
  moduleName: string
  /** 该表是否属于当前正在浏览的域；false 表示它是被本域引用的域外表。 */
  inDomain: boolean
  degree: number
  selfReferenceCount: number
  /** 该表对应模块里可打开的那一个；为 null 表示没有能进的入口。 */
  moduleId: number | null
  /** 服务端按菜单白名单解析好的入口路由，前端直接跳转，不自己拼 URL。 */
  routeUrl: string | null
}

export interface FlowTableEdge {
  /** 上游（被引用方）：字段的取值来源表，或登记关系里被定位的目标表。 */
  fromTable: string
  /** 下游（引用方）：字段所属表，或登记关系里执行动作的本单。 */
  toTable: string
  /** internal=两端都在本域；external=有一端在域外（默认折叠）。 */
  kind: 'internal' | 'external' | 'self'
  /** 是否同时是已登记单据关系（FIELD_RELATION，RELATION_KIND=EFFECT）。 */
  registered: boolean
  /** 登记关系的语义名；一对表命中多个边组时用 " / " 连接。 */
  relationName: string | null
  /** 字段引用条数（FIELD_DATASOURCE）。 */
  fieldCount: number
  /** 引用字段名（FIELD_DATASOURCE）。 */
  fields: string[]
  /** 登记键列对，形如 "SEND_NO → SEND_NO"（本单列 → 目标单列）。 */
  keyPairs: string[]
  /** 引用方在本域：默认视图画的那些边（"本域引用了谁"）。 */
  downstreamInDomain: boolean
  /** 被引用方在本域：打开上游视角后额外画出来的边（"本域被谁引用"）。 */
  upstreamInDomain: boolean
}

export interface FlowDomainGraph {
  rootIdx: number
  name: string
  nodes: FlowTableNode[]
  edges: FlowTableEdge[]
}

/** 画布节点的展示载荷。 */
export interface FlowNodeData extends Record<string, unknown> {
  title: string
  code: string
  meta: string
  badge: string
  variant: 'domain' | 'table' | 'table-external'
  /** 该域的配色（与它引出的连线同色），域总览图上以左侧色条呈现。 */
  color?: string
  /** 点击可打开的模块入口（服务端解析好的路由）；没有就不给入口，点击只做聚焦。 */
  routeUrl?: string
}

export interface FlowNodeSize {
  width: number
  height: number
}

/** 域节点 id 前缀：域编号与表代码共存于同一张图，靠前缀区分两者。 */
export const DOMAIN_NODE_PREFIX = 'domain:'

export function domainNodeId(rootIdx: number): string {
  return `${DOMAIN_NODE_PREFIX}${rootIdx}`
}

/** 域节点 id → 域编号；不是域节点则返回 null。 */
export function domainRootIdxOf(nodeId: string): number | null {
  if (!nodeId.startsWith(DOMAIN_NODE_PREFIX)) return null
  const raw = nodeId.slice(DOMAIN_NODE_PREFIX.length)
  // 必须整段是数字：Number('') 是 0，会让 "domain:" 这种残号被误判成"未归属域"（编号正是 0）
  return /^\d+$/.test(raw) ? Number(raw) : null
}

/** 边的稳定 id：一对表只有一条边（两个数据源已合成），故两端表名即唯一键。 */
export function flowEdgeId(edge: FlowTableEdge): string {
  return `${edge.fromTable}->${edge.toTable}`
}

export function domainLinkId(link: FlowDomainLink): string {
  return `${domainNodeId(link.fromRootIdx)}->${domainNodeId(link.toRootIdx)}`
}

/**
 * 总览连线上的数字。两个数据源分开计数，所以带登记关系时写成 "9+2" 而不是加起来：
 * 一个 9 与一个 11 讲的是完全不同的事（后者里有 2 条真正的单据关系）。
 */
export function domainLinkLabel(link: FlowDomainLink, registeredOnly: boolean): string | undefined {
  if (registeredOnly) return String(link.relationEdgeCount)
  if (isWeakDomainLink(link)) return undefined
  if (link.edgeCount === 0) return String(link.relationEdgeCount)
  if (link.relationEdgeCount === 0) return String(link.edgeCount)
  return `${link.edgeCount}+${link.relationEdgeCount}`
}

/**
 * 一条域内边在当前视角下该不该画。判据集中在这里，便于阅读与单测：
 * 1. **已登记单据关系始终显示**——它就是"单据上下游"本身，不该被"跨域折叠"藏起来；
 * 2. 下游视角（默认）：本域引用了谁。域内边画，跨域引用按开关折叠；
 * 3. 上游视角：本域的表被谁引用。这类边落在别的域的下游视图里、本域看不到，所以开关一开就必须画，
 *    否则"上游视角"这个名字等于没有效果。
 */
export function isEdgeVisible(edge: FlowTableEdge, showExternal: boolean, includeUpstream: boolean): boolean {
  if (edge.registered) return true
  if (edge.downstreamInDomain) return edge.kind === 'internal' || showExternal
  return includeUpstream && edge.upstreamInDomain
}

export const DOMAIN_NODE_WIDTH = 180
export const TABLE_NODE_WIDTH = 168

/**
 * 节点几何：**必须与 `business-flow.css` 的 `.erp-flow-node` 保持一致**
 * （上下内边距 / 行高 / 行间距 / 上下边框）。布局按这里算出的尺寸摆节点，
 * 两边一旦不一致，节点要么互相压住、要么在固定高度里把第三行裁掉。
 *
 * 行高写成固定 px 而不是倍数：`line-height: 1.3` 会随字号变（标题 12px、表代码与
 * 补充信息 11px），三行混排算不出整数高度，节点高度就没法与渲染结果对上。
 */
const NODE_PADDING_Y = 8
const NODE_LINE_HEIGHT = 16
const NODE_LINE_GAP = 2
const NODE_BORDER_Y = 2

/** 节点实际渲染的行数：标题 + 可选表代码 + 可选补充信息。 */
export function nodeLineCount(data: FlowNodeData): number {
  return 1 + (data.code ? 1 : 0) + (data.meta ? 1 : 0)
}

export function nodeHeightForLines(lines: number): number {
  return NODE_PADDING_Y * 2 + lines * NODE_LINE_HEIGHT + (lines - 1) * NODE_LINE_GAP + NODE_BORDER_Y
}

/** 节点尺寸：宽度按类型固定，高度按实际行数算——三行内容的节点就该比两行的高。 */
export function nodeSize(data: FlowNodeData): FlowNodeSize {
  return {
    width: data.variant === 'domain' ? DOMAIN_NODE_WIDTH : TABLE_NODE_WIDTH,
    height: nodeHeightForLines(nodeLineCount(data)),
  }
}

/** 两行节点的尺寸。域节点恒为两行；网格列数的估算也按两行算。 */
export const DOMAIN_NODE_SIZE: FlowNodeSize = { width: DOMAIN_NODE_WIDTH, height: nodeHeightForLines(2) }
export const TABLE_NODE_SIZE: FlowNodeSize = { width: TABLE_NODE_WIDTH, height: nodeHeightForLines(2) }

const INTERNAL_EDGE_COLOR = '#206bc4'
const EXTERNAL_EDGE_COLOR = '#adb5bd'
const WEAK_EDGE_COLOR = '#cbd5e1'
/** 已登记单据关系专用色：深青，与字段引用边的蓝、跨域引用边的灰都拉得开。 */
const REGISTERED_EDGE_COLOR = '#0b7285'

/** 业务域配色板：给「上游域 → 下游域」的连线区分来源用，取自系统主色系再补足色相。 */
export const DOMAIN_PALETTE = [
  '#206bc4', // 蓝
  '#d63939', // 红
  '#f76707', // 橙
  '#2fb344', // 绿
  '#0ca678', // 青绿
  '#0dcaf0', // 青
  '#4263eb', // 靛
  '#ae3ec9', // 紫
  '#d6336c', // 玫红
  '#f59f00', // 黄
]

export function domainColor(colorIndex: number): string {
  const size = DOMAIN_PALETTE.length
  return DOMAIN_PALETTE[((colorIndex % size) + size) % size]
}

/**
 * 弱边：只有一条字段引用、且不是已登记单据关系的域间连线。
 * 留着它（不丢事实），但退到背景里，不与主要流向争视觉。
 * 已登记关系哪怕只有一条也不算弱——它本身就是这一级视图最该被看见的东西。
 */
export function isWeakDomainLink(link: FlowDomainLink): boolean {
  return link.relationEdgeCount === 0 && link.edgeCount <= 1
}

/**
 * 总览图上「业务域 → 业务域」的连线：它是这一级视图的主体，不能用域内图那种表示
 * "次要引用"的浅灰虚线——整图缩到全貌后基本看不见。
 *
 * 颜色按**上游域**取（这条引用是从哪个域来的）：顺着颜色就能读出一条上下游链路，
 * 配合单击聚焦，同一个域进出的一串线是同色的。
 * 线宽随条数分档，带已登记关系的连线再加重一档（它比"选基础资料"重要得多）；
 * 弱边**不上色**、退到背景——颜色要留给主要流向。
 * 走贝塞尔曲线而非直角折线（smoothstep）：网格排列下折线的直角绕行会让画面更碎。
 */
export function domainEdgeStyle(link: FlowDomainLink, color: string): Partial<Edge> {
  const weak = isWeakDomainLink(link)
  const total = link.edgeCount + link.relationEdgeCount
  const strokeWidth = weak
    ? 1
    : link.relationEdgeCount > 0
      ? 3
      : total >= 10
        ? 2.6
        : total >= 4
          ? 1.8
          : 1.4
  const stroke = weak ? WEAK_EDGE_COLOR : color
  return {
    type: 'default',
    style: { stroke, strokeWidth },
    markerEnd: { type: MarkerType.ArrowClosed, width: weak ? 14 : 16, height: weak ? 14 : 16, color: stroke },
    labelStyle: { fill: weak ? '#94a3b8' : color, fontSize: 10, fontWeight: 700 },
    labelBgStyle: { fill: '#ffffff', fillOpacity: 0.85 },
    labelBgPadding: [3, 2] as [number, number],
    labelBgBorderRadius: 3,
  }
}

/**
 * 边的视觉定义：
 * - 已登记单据关系：深青实线加粗（始终显示——它就是"单据上下游"本身，不该被"跨域折叠"藏起来）；
 * - 域内流程：蓝实线；
 * - 跨域引用：灰虚线（默认折叠，展开后一眼可辨）。
 * 与画布组件分开放：画布文件只导出组件，Fast Refresh 才能正常工作。
 */
export function flowEdgeStyle(kind: FlowTableEdge['kind'], registered = false): Partial<Edge> {
  if (registered) {
    return {
      type: 'smoothstep',
      animated: false,
      style: { stroke: REGISTERED_EDGE_COLOR, strokeWidth: 2.4 },
      markerEnd: { type: MarkerType.ArrowClosed, width: 18, height: 18, color: REGISTERED_EDGE_COLOR },
      labelStyle: { fill: REGISTERED_EDGE_COLOR, fontSize: 10, fontWeight: 700 },
      labelBgStyle: { fill: '#ffffff', fillOpacity: 0.9 },
      labelBgPadding: [3, 2] as [number, number],
      labelBgBorderRadius: 3,
    }
  }

  const external = kind !== 'internal'
  return {
    type: 'smoothstep',
    animated: false,
    style: {
      stroke: external ? EXTERNAL_EDGE_COLOR : INTERNAL_EDGE_COLOR,
      strokeWidth: external ? 1.2 : 1.8,
      strokeDasharray: external ? '5 4' : undefined,
    },
    markerEnd: {
      type: MarkerType.ArrowClosed,
      width: 16,
      height: 16,
      color: external ? EXTERNAL_EDGE_COLOR : INTERNAL_EDGE_COLOR,
    },
  }
}

/**
 * 登记关系边的标签：取键列摘要而不是关系名。
 * 关系名（如 "COP_RETURN_M 定位键（效果）"）里的表名已经在两端节点上写着，写进标签是重复；
 * "靠哪一列关联"才是这条边相对字段引用边多出来的信息。完整关系名在细节面板里给。
 */
export function relationKeyLabel(keyPairs: string[]): string {
  const first = keyPairs[0]?.split('→')[0]?.trim()
  if (!first) return '登记关系'
  return keyPairs.length > 1 ? `${first} 等 ${keyPairs.length} 键` : first
}

/** 域节点与表节点共用的搜索：关键字命中表代码/表名/模块名，大小写不敏感。 */
export function matchIds(items: { id: string; text: string }[], keyword: string): Set<string> {
  const needle = keyword.trim().toUpperCase()
  if (needle.length === 0) return new Set()
  return new Set(items.filter((item) => item.text.toUpperCase().includes(needle)).map((item) => item.id))
}

/**
 * 网格排列：按传入顺序逐行摆放。
 *
 * 业务域总览用它、而不是 dagre 分层：域之间大量互不相干（人事域与模具域没有引用关系），
 * dagre 找不到可分层级就把它们全平铺在同一层，23 个域拉成近 6000px 宽，
 * fitView 后缩放只剩约 0.18，节点文字与连线全部不可读。网格在同样的视口里能保持接近 1 的缩放。
 * 方向语义不丢：边的箭头仍指向上游 → 下游，且域内图那一级照旧用分层布局。
 */
/** 网格的行/列间距：bestColumnCount 估算尺寸与 layoutGridGraph 实际摆放必须同值，故共用常量。 */
export const GRID_GAP_X = 26
export const GRID_GAP_Y = 26

/**
 * 选一个让整块图的宽高比最接近视口的列数。
 *
 * 同样多的节点，整块图的宽高比贴合画布时 fitView 的缩放比最大——节点与文字也就最大。
 * 只按 √n 取列虽然方正，却不管视口是宽是扁，常常白白缩掉一档。
 * 比较用对数比值：偏宽与偏扁的惩罚对称。
 */
export function bestColumnCount(
  count: number,
  nodeWidth: number,
  nodeHeight: number,
  targetRatio: number,
): number {
  if (count <= 1) return 1
  let best = Math.max(2, Math.ceil(Math.sqrt(count)))
  let bestScore = Number.POSITIVE_INFINITY

  for (let columns = 1; columns <= count; columns += 1) {
    const rows = Math.ceil(count / columns)
    const width = columns * nodeWidth + (columns - 1) * GRID_GAP_X
    const height = rows * nodeHeight + (rows - 1) * GRID_GAP_Y
    if (width <= 0 || height <= 0) continue
    const score = Math.abs(Math.log(width / height / targetRatio))
    if (score < bestScore) {
      bestScore = score
      best = columns
    }
  }
  return best
}

/**
 * 按引用关系做一次广度优先排序：有引用关系的域会被排到相邻位置。
 *
 * 网格填格本身不认识边，若直接按表的数量顺次填，相关的域会离得很远、连线横穿整屏。
 * 先用 BFS 让「枢纽 → 它的邻居 → 邻居的邻居」依次排开，再交给网格，长边与交叉明显减少。
 */
export function orderByRelation(nodeIds: string[], links: { source: string; target: string }[]): string[] {
  const adjacency = new Map<string, Set<string>>()
  for (const id of nodeIds) adjacency.set(id, new Set())
  for (const link of links) {
    adjacency.get(link.source)?.add(link.target)
    adjacency.get(link.target)?.add(link.source)
  }

  const degree = (id: string) => adjacency.get(id)?.size ?? 0
  const visited = new Set<string>()
  const order: string[] = []

  for (const start of [...nodeIds].sort((a, b) => degree(b) - degree(a))) {
    if (visited.has(start)) continue
    visited.add(start)
    const queue = [start]
    while (queue.length > 0) {
      const current = queue.shift() as string
      order.push(current)
      // 邻居里连接多的优先排，让枢纽聚成一簇而不是散在网格各处
      const neighbors = [...(adjacency.get(current) ?? [])].sort((a, b) => degree(b) - degree(a))
      for (const neighbor of neighbors) {
        if (visited.has(neighbor)) continue
        visited.add(neighbor)
        queue.push(neighbor)
      }
    }
  }
  return order
}

export function layoutGridGraph(
  nodeIds: string[],
  sizes: Map<string, FlowNodeSize>,
  columns: number,
): Map<string, { x: number; y: number }> {
  const gapX = GRID_GAP_X
  const gapY = GRID_GAP_Y
  const positions = new Map<string, { x: number; y: number }>()
  let x = 0
  let y = 0
  let rowHeight = 0
  let column = 0

  for (const id of nodeIds) {
    const size = sizes.get(id) ?? TABLE_NODE_SIZE
    positions.set(id, { x, y })
    rowHeight = Math.max(rowHeight, size.height)
    column += 1
    if (column >= columns) {
      column = 0
      x = 0
      y += rowHeight + gapY
      rowHeight = 0
    } else {
      x += size.width + gapX
    }
  }
  return positions
}

/**
 * 用 dagre 算分层坐标。图是有向的「上游 → 下游」，LR 方向让流程从左往右读。
 * dagre 返回的是节点中心点，React Flow 要的是左上角，故回退半个节点尺寸。
 * 自环边不参与布局（它只会把节点自己绕住），在下游节点的角标上单独提示。
 */
export function layoutFlowGraph(
  nodeIds: string[],
  sizes: Map<string, FlowNodeSize>,
  links: { source: string; target: string }[],
  direction: 'LR' | 'TB' = 'LR',
): Map<string, { x: number; y: number }> {
  const graph = new dagre.graphlib.Graph()
  graph.setDefaultEdgeLabel(() => ({}))
  graph.setGraph({ rankdir: direction, nodesep: 20, ranksep: 56, marginx: 16, marginy: 16 })

  for (const id of nodeIds) {
    const size = sizes.get(id) ?? TABLE_NODE_SIZE
    graph.setNode(id, { width: size.width, height: size.height })
  }
  for (const link of links) {
    if (link.source !== link.target && graph.hasNode(link.source) && graph.hasNode(link.target)) {
      graph.setEdge(link.source, link.target)
    }
  }

  dagre.layout(graph)

  const positions = new Map<string, { x: number; y: number }>()
  for (const id of nodeIds) {
    const size = sizes.get(id) ?? TABLE_NODE_SIZE
    const node = graph.node(id) as { x: number; y: number } | undefined
    if (!node) {
      positions.set(id, { x: 0, y: 0 })
      continue
    }
    positions.set(id, { x: node.x - size.width / 2, y: node.y - size.height / 2 })
  }
  return positions
}
