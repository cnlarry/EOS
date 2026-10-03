import { describe, expect, it } from 'vitest'
import {
  DOMAIN_NODE_SIZE,
  DOMAIN_NODE_WIDTH,
  DOMAIN_PALETTE,
  GRID_GAP_X,
  GRID_GAP_Y,
  TABLE_NODE_SIZE,
  TABLE_NODE_WIDTH,
  bestColumnCount,
  domainColor,
  domainLinkId,
  domainLinkLabel,
  domainNodeId,
  domainRootIdxOf,
  flowEdgeId,
  flowEdgeStyle,
  isEdgeVisible,
  isWeakDomainLink,
  layoutFlowGraph,
  layoutGridGraph,
  matchIds,
  nodeHeightForLines,
  nodeLineCount,
  nodeSize,
  orderByRelation,
  relationKeyLabel,
  type FlowDomainLink,
  type FlowNodeData,
  type FlowNodeSize,
  type FlowTableEdge,
} from './flowGraph'

/** 边工厂：只写关心的字段，其余取"字段引用、在本域下游"的默认值。 */
function edge(overrides: Partial<FlowTableEdge> = {}): FlowTableEdge {
  return {
    fromTable: 'UP',
    toTable: 'DOWN',
    kind: 'internal',
    registered: false,
    relationName: null,
    fieldCount: 1,
    fields: ['F'],
    keyPairs: [],
    downstreamInDomain: true,
    upstreamInDomain: true,
    ...overrides,
  }
}

function link(overrides: Partial<FlowDomainLink> = {}): FlowDomainLink {
  return { fromRootIdx: 14, toRootIdx: 16, edgeCount: 0, relationEdgeCount: 0, ...overrides }
}

/** 节点载荷工厂：默认是"两行"的表节点（标题 + 表代码，无补充信息）。 */
function nodeData(overrides: Partial<FlowNodeData> = {}): FlowNodeData {
  return { title: '销售订单明细', code: 'COP_ORDER_D', meta: '', badge: '', variant: 'table', ...overrides }
}

describe('flowGraph 标识与配色', () => {
  it('域节点 id 与编号互转，非域节点与非数字残号都返回 null', () => {
    expect(domainNodeId(14)).toBe('domain:14')
    expect(domainRootIdxOf('domain:14')).toBe(14)
    expect(domainRootIdxOf('domain:0')).toBe(0)
    expect(domainRootIdxOf('COP_ORDER_D')).toBeNull()
    // Number('') 是 0，若不做整段数字校验会把残号误判成"未归属域"
    expect(domainRootIdxOf('domain:')).toBeNull()
    expect(domainRootIdxOf('domain:1a')).toBeNull()
  })

  it('域编号 0 是合法的"未归属域"，不能被当成 falsy 丢掉', () => {
    expect(domainRootIdxOf(domainNodeId(0))).toBe(0)
  })

  it('边 id 由两端表名决定，方向不同即不同边', () => {
    expect(flowEdgeId(edge({ fromTable: 'A', toTable: 'B' }))).toBe('A->B')
    expect(flowEdgeId(edge({ fromTable: 'B', toTable: 'A' }))).toBe('B->A')
  })

  it('域间连线 id 用域节点 id 拼装', () => {
    expect(domainLinkId(link({ fromRootIdx: 14, toRootIdx: 16 }))).toBe('domain:14->domain:16')
  })

  it('域配色按色板循环，负数也能取到合法色号', () => {
    expect(domainColor(0)).toBe(DOMAIN_PALETTE[0])
    expect(domainColor(DOMAIN_PALETTE.length)).toBe(DOMAIN_PALETTE[0])
    expect(domainColor(DOMAIN_PALETTE.length + 2)).toBe(DOMAIN_PALETTE[2])
    expect(domainColor(-1)).toBe(DOMAIN_PALETTE[DOMAIN_PALETTE.length - 1])
  })
})

describe('flowGraph 边与域间连线的判据', () => {
  it('弱边：只有一处字段引用且不是登记关系才是弱边', () => {
    expect(isWeakDomainLink(link({ edgeCount: 1, relationEdgeCount: 0 }))).toBe(true)
    expect(isWeakDomainLink(link({ edgeCount: 0, relationEdgeCount: 0 }))).toBe(true)
    expect(isWeakDomainLink(link({ edgeCount: 2, relationEdgeCount: 0 }))).toBe(false)
    // 已登记关系哪怕只有一条也不算弱——它本身就是这一级视图最该被看见的东西
    expect(isWeakDomainLink(link({ edgeCount: 0, relationEdgeCount: 1 }))).toBe(false)
  })

  it('域间标签把两个数据源分开写，不合并成一个数', () => {
    expect(domainLinkLabel(link({ edgeCount: 9, relationEdgeCount: 2 }), false)).toBe('9+2')
    expect(domainLinkLabel(link({ edgeCount: 9, relationEdgeCount: 0 }), false)).toBe('9')
    expect(domainLinkLabel(link({ edgeCount: 0, relationEdgeCount: 2 }), false)).toBe('2')
    // 弱边不挂数字：它已经退到背景，再挂一个 "1" 只会增加噪音
    expect(domainLinkLabel(link({ edgeCount: 1, relationEdgeCount: 0 }), false)).toBeUndefined()
    // 只看登记关系时，数字就该只讲登记关系
    expect(domainLinkLabel(link({ edgeCount: 9, relationEdgeCount: 2 }), true)).toBe('2')
  })

  it('登记关系边标签取键列摘要而不是关系名（表名在两端节点上已写着）', () => {
    expect(relationKeyLabel(['SEND_NO → SEND_NO'])).toBe('SEND_NO')
    expect(relationKeyLabel(['SEND_NO → SEND_NO', 'SEND_TYPE → SEND_TYPE'])).toBe('SEND_NO 等 2 键')
    expect(relationKeyLabel([])).toBe('登记关系')
  })

  it('登记关系边始终可见，不受"跨域折叠"与"上游视角"影响', () => {
    const registered = edge({ registered: true, downstreamInDomain: false, upstreamInDomain: false, kind: 'external' })
    expect(isEdgeVisible(registered, false, false)).toBe(true)
  })

  it('下游视角：域内边常显，跨域引用按开关折叠', () => {
    const internal = edge({ kind: 'internal' })
    expect(isEdgeVisible(internal, false, false)).toBe(true)

    const external = edge({ kind: 'external', upstreamInDomain: false })
    expect(isEdgeVisible(external, false, false)).toBe(false)
    expect(isEdgeVisible(external, true, false)).toBe(true)
  })

  it('上游视角：把"本域被谁引用"的边放出来，关掉就收回去', () => {
    const fromOutside = edge({ kind: 'external', downstreamInDomain: false, upstreamInDomain: true })
    expect(isEdgeVisible(fromOutside, false, false)).toBe(false)
    expect(isEdgeVisible(fromOutside, false, true)).toBe(true)
    // 上游视角不该把与"上游"无关的域外对也放进来
    const unrelated = edge({ kind: 'external', downstreamInDomain: false, upstreamInDomain: false })
    expect(isEdgeVisible(unrelated, false, true)).toBe(false)
  })

  it('边样式：登记关系用深青实线，跨域引用用灰虚线', () => {
    const internal = flowEdgeStyle('internal', false)
    expect(internal.style?.stroke).toBe('#206bc4')
    expect(internal.style?.strokeDasharray).toBeUndefined()

    const external = flowEdgeStyle('external', false)
    expect(external.style?.strokeDasharray).toBe('5 4')

    const registered = flowEdgeStyle('external', true)
    expect(registered.style?.stroke).toBe('#0b7285')
    expect(registered.style?.strokeDasharray).toBeUndefined()
    expect((registered.markerEnd as { color?: string }).color).toBe('#0b7285')
  })
})

describe('flowGraph 搜索', () => {
  const items = [
    { id: 'domain:14', text: '销售管理 14' },
    { id: 'COP_ORDER_D', text: 'COP_ORDER_D 销售订单明细 销售管理' },
    { id: 'PRODUCT', text: 'PRODUCT 产品 基本参数' },
  ]

  it('大小写不敏感，命中表代码或中文名', () => {
    expect([...matchIds(items, 'cop_order')]).toEqual(['COP_ORDER_D'])
    expect([...matchIds(items, '销售')]).toEqual(['domain:14', 'COP_ORDER_D'])
    expect([...matchIds(items, '产品')]).toEqual(['PRODUCT'])
  })

  it('空关键字视为未搜索，返回空集合而不是全部命中', () => {
    expect(matchIds(items, '').size).toBe(0)
    expect(matchIds(items, '   ').size).toBe(0)
  })

  it('保持传入顺序，首个命中即定位目标', () => {
    expect([...matchIds(items, '销售')][0]).toBe('domain:14')
  })
})

describe('flowGraph 节点尺寸', () => {
  it('行数 = 标题 + 可选表代码 + 可选补充信息', () => {
    expect(nodeLineCount(nodeData({ code: '', meta: '' }))).toBe(1)
    expect(nodeLineCount(nodeData())).toBe(2)
    expect(nodeLineCount(nodeData({ meta: '销售管理' }))).toBe(3)
  })

  it('每多一行就多出一行高加一份行距', () => {
    expect(nodeHeightForLines(2) - nodeHeightForLines(1)).toBe(18)
    expect(nodeHeightForLines(3) - nodeHeightForLines(2)).toBe(18)
  })

  it('高度按行数算：两行 52、三行 70（上下内边距 8 + 行高 16 + 行距 2 + 上下边框 2）', () => {
    // 这两个数是与 business-flow.css 的契约：三行内容若还按两行的高度摆，第三行就被裁掉
    expect(nodeHeightForLines(1)).toBe(34)
    expect(nodeHeightForLines(2)).toBe(52)
    expect(nodeHeightForLines(3)).toBe(70)
  })

  it('域节点恒为两行；表节点按内容取两行或三行', () => {
    expect(nodeSize(nodeData({ variant: 'domain', code: '', meta: '30 张表 · 域内流程 12' }))).toEqual({
      width: DOMAIN_NODE_WIDTH,
      height: nodeHeightForLines(2),
    })
    expect(nodeSize(nodeData())).toEqual({ width: TABLE_NODE_WIDTH, height: nodeHeightForLines(2) })
    // 模块名与表名不同时才会补第三行——这正是先前被裁掉的那一行
    expect(nodeSize(nodeData({ meta: '销售管理' }))).toEqual({ width: TABLE_NODE_WIDTH, height: nodeHeightForLines(3) })
    expect(nodeSize(nodeData({ meta: '销售管理' })).height).toBeGreaterThan(nodeSize(nodeData()).height)
  })
})

describe('flowGraph 布局', () => {
  const sizes = new Map<string, FlowNodeSize>([
    ['A', TABLE_NODE_SIZE],
    ['B', TABLE_NODE_SIZE],
    ['C', TABLE_NODE_SIZE],
  ])

  it('分层布局让上游在左、下游在右', () => {
    const positions = layoutFlowGraph(['A', 'B', 'C'], sizes, [
      { source: 'A', target: 'B' },
      { source: 'B', target: 'C' },
    ])
    const xA = positions.get('A')?.x ?? 0
    const xB = positions.get('B')?.x ?? 0
    const xC = positions.get('C')?.x ?? 0
    expect(xA).toBeLessThan(xB)
    expect(xB).toBeLessThan(xC)
  })

  it('分层布局忽略自环与指向不存在节点的边，且每个节点都有坐标', () => {
    const positions = layoutFlowGraph(['A', 'B'], sizes, [
      { source: 'A', target: 'A' },
      { source: 'A', target: 'MISSING' },
    ])
    expect(positions.size).toBe(2)
    expect(positions.get('A')).toBeDefined()
    expect(positions.get('B')).toBeDefined()
  })

  it('网格布局按行列间距摆放，列满换行', () => {
    const grid = new Map<string, FlowNodeSize>([
      ['n1', { width: 10, height: 10 }],
      ['n2', { width: 10, height: 10 }],
      ['n3', { width: 10, height: 10 }],
    ])
    const positions = layoutGridGraph(['n1', 'n2', 'n3'], grid, 2)
    expect(positions.get('n1')).toEqual({ x: 0, y: 0 })
    expect(positions.get('n2')).toEqual({ x: 10 + GRID_GAP_X, y: 0 })
    expect(positions.get('n3')).toEqual({ x: 0, y: 10 + GRID_GAP_Y })
  })

  it('列数选择：始终落在合法区间，宽视口取的列数不少于扁视口', () => {
    const width = DOMAIN_NODE_WIDTH
    const height = DOMAIN_NODE_SIZE.height

    expect(bestColumnCount(0, width, height, 1.6)).toBe(1)
    expect(bestColumnCount(1, width, height, 1.6)).toBe(1)

    for (const count of [2, 9, 24, 40]) {
      const columns = bestColumnCount(count, width, height, 1.6)
      expect(columns).toBeGreaterThanOrEqual(1)
      expect(columns).toBeLessThanOrEqual(count)
    }

    const wide = bestColumnCount(24, width, height, 3)
    const tall = bestColumnCount(24, width, height, 0.5)
    expect(wide).toBeGreaterThan(tall)
    // 极端宽的视口应当铺成一行——这是"让整块图贴合视口"的极限形态
    expect(bestColumnCount(24, width, height, 100)).toBe(24)
  })

  it('按引用关系排序把相关的节点排到相邻位置，且不丢不重', () => {
    const order = orderByRelation(
      ['a', 'b', 'c', 'd'],
      [
        { source: 'a', target: 'b' },
        { source: 'c', target: 'd' },
      ],
    )
    expect([...order].sort()).toEqual(['a', 'b', 'c', 'd'])
    expect(Math.abs(order.indexOf('a') - order.indexOf('b'))).toBe(1)
    expect(Math.abs(order.indexOf('c') - order.indexOf('d'))).toBe(1)
  })

  it('按引用关系排序：无节点时返回空，单节点原样返回', () => {
    expect(orderByRelation([], [])).toEqual([])
    expect(orderByRelation(['only'], [])).toEqual(['only'])
  })
})
