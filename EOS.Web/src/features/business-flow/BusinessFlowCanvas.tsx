import { useEffect, useMemo, useRef, useState } from 'react'
import {
  Background,
  Handle,
  MiniMap,
  Panel,
  Position,
  ReactFlow,
  useEdgesState,
  useNodesState,
  useReactFlow,
  type Edge,
  type Node,
  type NodeProps,
  type ReactFlowInstance,
} from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import { IconMaximize, IconMinus, IconPlus } from '@tabler/icons-react'
import { Button } from '../../components/ui/Button'
import {
  DOMAIN_NODE_SIZE,
  TABLE_NODE_SIZE,
  bestColumnCount,
  layoutFlowGraph,
  layoutGridGraph,
  nodeSize,
  type FlowNodeData,
} from './flowGraph'

type BusinessFlowNode = Node<FlowNodeData, 'flow'>

/**
 * 画布输入节点：坐标由布局在画布内算，调用方只提供 id 与展示数据，
 * 因此它不含 Node 要求的 position——避免把"还没布局"的中间态硬凑成合法的 Node。
 */
export interface FlowNodeSeed {
  id: string
  type: 'flow'
  data: FlowNodeData
}

function FlowNodeCard({ data }: NodeProps) {
  const node = data as FlowNodeData
  return (
    <div
      className={`erp-flow-node erp-flow-node--${node.variant}`}
      title={node.routeUrl ? `新标签页打开：${node.title}` : node.title}
      style={node.color ? { borderLeft: `3px solid ${node.color}` } : undefined}
    >
      {/* 自定义节点必须自带 Handle：边的两端连的是 Handle，没有它再完整的 edges 也画不出来。 */}
      <Handle type="target" position={Position.Left} className="erp-flow-handle" />
      <Handle type="source" position={Position.Right} className="erp-flow-handle" />
      <span className="erp-flow-node-title">{node.title}</span>
      {/* 域节点把编号并进了标题，这里就不再单独占一行——三行内容在固定高度里会把首尾裁掉 */}
      {node.code ? <span className="erp-flow-node-code">{node.code}</span> : null}
      {/* 没有补充信息时不留空行，否则居中对齐会把上下的文字挤出节点 */}
      {node.meta ? <span className="erp-flow-node-meta">{node.meta}</span> : null}
      {node.badge ? <span className="erp-flow-node-badge">{node.badge}</span> : null}
    </div>
  )
}

const nodeTypes = { flow: FlowNodeCard }

/**
 * 画布缩放控件：不用 React Flow 自带的 `<Controls>`——它那套白底方块按钮与自带 SVG 图标
 * 和系统其它页面不一致。这里改用统一的图标按钮（Tabler 图标 + 系统按钮组件）。
 */
function FlowControls() {
  const { zoomIn, zoomOut, fitView } = useReactFlow()
  return (
    <Panel position="bottom-left" className="erp-flow-controls">
      <Button size="sm" icon={<IconPlus size={16} />} title="放大" aria-label="放大" onClick={() => void zoomIn()} />
      <Button size="sm" icon={<IconMinus size={16} />} title="缩小" aria-label="缩小" onClick={() => void zoomOut()} />
      <Button
        size="sm"
        icon={<IconMaximize size={16} />}
        title="适应画布"
        aria-label="适应画布"
        onClick={() => void fitView({ padding: 0.1 })}
      />
    </Panel>
  )
}

interface BusinessFlowCanvasProps {
  nodes: FlowNodeSeed[]
  edges: Edge[]
  /**
   * 布局方式：域总览用 grid（域之间多为并行关系，分层会把它们平铺成一条超宽的带），
   * 域内明细用 layered（那里上下游是主角）。
   */
  layout?: 'grid' | 'layered'
  /** 搜索命中的节点：高亮它们、其余淡出。空集合表示没在搜索。 */
  highlightIds?: Set<string>
  /** 搜索命中的首个节点：变化时把视口移过去。 */
  focusNodeId?: string | null
  /** 当前选中的连线（细节面板正在看的那条）。 */
  selectedEdgeId?: string | null
  /** 双击节点（总览图上即下钻进入该域）。 */
  onActivateNode?: (nodeId: string) => void
  /** 点击有入口的节点时打开对应模块（由调用方决定怎么开，如新标签页）；路由由服务端解析，能不能进由统一工作台自己的权限门把关。 */
  onOpenNode?: (routeUrl: string) => void
  /** 点击连线取细节，点空白处传 null 表示收起。 */
  onEdgeSelect?: (edgeId: string | null) => void
}

/**
 * 业务流程图画布：布局（dagre 分层 / 网格）+ React Flow 交互。
 * 节点坐标是受控状态——布局在节点/边变化时重算，用户拖拽后由 React Flow 自己维护。
 *
 * 淡化优先级（越靠前越优先）：悬停的连线 → 搜索命中 → 悬停的节点。
 * 悬停连线优先是因为"这条线是谁跟谁"是最直接的问题；搜索次之，因为它是当前任务。
 */
export function BusinessFlowCanvas({
  nodes: inputNodes,
  edges: inputEdges,
  layout = 'layered',
  highlightIds,
  focusNodeId,
  selectedEdgeId,
  onActivateNode,
  onOpenNode,
  onEdgeSelect,
}: BusinessFlowCanvasProps) {
  const [nodes, setNodes, onNodesChange] = useNodesState<BusinessFlowNode>([])
  const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>([])
  const [focusedId, setFocusedId] = useState<string | null>(null)
  const [hoveredEdgeId, setHoveredEdgeId] = useState<string | null>(null)
  // 画布实际尺寸：网格的列数按它挑，宽高比贴合视口时 fitView 的缩放比最大
  const containerRef = useRef<HTMLDivElement>(null)
  const [canvasSize, setCanvasSize] = useState({ width: 0, height: 0 })
  // React Flow 实例经 onInit 拿到：搜索定位要调它的 fitView（在 <ReactFlow> 外面用 hook 拿不到）
  const [instance, setInstance] = useState<ReactFlowInstance<BusinessFlowNode, Edge> | null>(null)

  useEffect(() => {
    const element = containerRef.current
    if (!element) return
    const observer = new ResizeObserver((entries) => {
      const rect = entries[0]?.contentRect
      if (rect && rect.width > 0 && rect.height > 0) setCanvasSize({ width: rect.width, height: rect.height })
    })
    observer.observe(element)
    return () => observer.disconnect()
  }, [])

  useEffect(() => {
    // 尺寸按每个节点自己的行数算：三行内容的节点（标题 + 表代码 + 模块名）比两行的高，
    // 用同一个常量会让它在固定高度里被裁掉。
    const sizes = new Map(inputNodes.map((node) => [node.id, nodeSize(node.data)]))
    const ids = inputNodes.map((node) => node.id)
    const positions =
      layout === 'grid'
        ? layoutGridGraph(
            ids,
            sizes,
            bestColumnCount(
              ids.length,
              DOMAIN_NODE_SIZE.width,
              DOMAIN_NODE_SIZE.height,
              canvasSize.width > 0 && canvasSize.height > 0 ? canvasSize.width / canvasSize.height : 1.6,
            ),
          )
        : layoutFlowGraph(
            ids,
            sizes,
            inputEdges.map((edge) => ({ source: edge.source, target: edge.target })),
          )

    setNodes(
      inputNodes.map((node) => {
        const size = sizes.get(node.id) ?? TABLE_NODE_SIZE
        // 固定节点尺寸：布局是按这个尺寸算的间距，交给内容自适应会让布局与坐标对不上。
        return {
          ...node,
          position: positions.get(node.id) ?? { x: 0, y: 0 },
          width: size.width,
          height: size.height,
          sourcePosition: Position.Right,
          targetPosition: Position.Left,
        }
      }),
    )
    setEdges(inputEdges)
  }, [canvasSize.height, canvasSize.width, inputNodes, inputEdges, layout, setNodes, setEdges])

  // 搜索定位：延到下一帧再 fitView，等布局坐标写进 React Flow 的 store。
  useEffect(() => {
    if (!focusNodeId || !instance) return
    const handle = window.requestAnimationFrame(() => {
      if (!instance.getNodes().some((node) => node.id === focusNodeId)) return
      void instance.fitView({ nodes: [{ id: focusNodeId }], padding: 0.6, duration: 300, maxZoom: 1.2 })
    })
    return () => window.cancelAnimationFrame(handle)
  }, [focusNodeId, instance])

  // 单域聚焦：点中某个节点时只保留它与直接相连的节点/连线，其余淡出。
  // 这张图默认是"全貌"，聚焦之后它才变成能顺着一条流向读下去的图。
  const related = useMemo(() => {
    // 悬停某条连线时，突出的就是这条线的两端——"这条线是谁跟谁"最直接的回答
    if (hoveredEdgeId) {
      const hovered = edges.find((edge) => edge.id === hoveredEdgeId)
      if (hovered) return { nodeIds: new Set([hovered.source, hovered.target]), edgeIds: new Set([hovered.id]) }
    }
    // 搜索命中：命中节点与"命中节点之间的连线"留在亮处，其余淡出
    if (highlightIds && highlightIds.size > 0) {
      return {
        nodeIds: highlightIds,
        edgeIds: new Set(
          edges
            .filter((edge) => highlightIds.has(edge.source) && highlightIds.has(edge.target))
            .map((edge) => edge.id),
        ),
      }
    }
    if (!focusedId) return null
    // 聚焦的节点已不在当前图里（刚下钻、切了视图）时不淡化任何东西，避免整屏变灰
    if (!nodes.some((node) => node.id === focusedId)) return null
    const nodeIds = new Set([focusedId])
    const edgeIds = new Set<string>()
    for (const edge of edges) {
      if (edge.source !== focusedId && edge.target !== focusedId) continue
      edgeIds.add(edge.id)
      nodeIds.add(edge.source)
      nodeIds.add(edge.target)
    }
    return { nodeIds, edgeIds }
  }, [edges, focusedId, highlightIds, hoveredEdgeId, nodes])

  const displayNodes = useMemo(() => {
    if (!related) return nodes
    // 搜索命中的节点即使连线不在命中之间也保持点亮：命中本身就是结果，不该被淡化
    return nodes.map((node) => ({
      ...node,
      className: related.nodeIds.has(node.id) ? '' : 'erp-flow-dimmed',
    }))
  }, [nodes, related])

  const displayEdges = useMemo(() => {
    if (!related && !selectedEdgeId) return edges
    return edges.map((edge) => {
      const classes: string[] = []
      if (related && !related.edgeIds.has(edge.id)) classes.push('erp-flow-dimmed')
      if (selectedEdgeId === edge.id) classes.push('erp-flow-edge-selected')
      return classes.length === 0 ? edge : { ...edge, className: classes.join(' ') }
    })
  }, [edges, related, selectedEdgeId])

  return (
    // 外面这层只为量尺寸：网格挑列数要知道画布实际的宽高比。
    <div ref={containerRef} className="erp-flow-canvas-inner">
      <ReactFlow
        nodes={displayNodes}
        edges={displayEdges}
        nodeTypes={nodeTypes}
        onInit={setInstance}
        onNodesChange={onNodesChange}
        onEdgesChange={onEdgesChange}
        onNodeClick={(_event, node) => {
          const route = node.data.routeUrl as string | undefined
          // 有入口就直接进统一工作台，没有就退化为聚焦
          if (route) onOpenNode?.(route)
          else setFocusedId(node.id)
        }}
        onNodeMouseEnter={(_event, node) => setFocusedId(node.id)}
        onNodeMouseLeave={() => setFocusedId(null)}
        onEdgeMouseEnter={(_event, edge) => setHoveredEdgeId(edge.id)}
        onEdgeMouseLeave={() => setHoveredEdgeId(null)}
        onEdgeClick={(_event, edge) => onEdgeSelect?.(edge.id)}
        onPaneClick={() => {
          setFocusedId(null)
          setHoveredEdgeId(null)
          onEdgeSelect?.(null)
        }}
        onNodeDoubleClick={(_event, node) => onActivateNode?.(node.id)}
        fitView
        // minZoom 是兜底：大域的表很多时 fitView 会一路缩到看不清，
        // 宁可让图溢出视口（可平移查看）也不失去可读性。
        fitViewOptions={{ padding: 0.08, minZoom: 0.5, maxZoom: 1.1 }}
        minZoom={0.1}
        proOptions={{ hideAttribution: false }}
      >
        <Background gap={18} color="#e6e8eb" />
        <FlowControls />
        <MiniMap
          pannable
          zoomable
          nodeStrokeWidth={2}
          nodeColor={(node) => (node.data.variant === 'domain' ? '#4299e1' : '#c6d9ef')}
          style={{ background: '#f8f9fa' }}
        />
      </ReactFlow>
    </div>
  )
}
