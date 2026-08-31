import {
  DndContext, DragOverlay, PointerSensor, useDraggable, useSensor, useSensors,
  type DragEndEvent, type DragStartEvent,
} from '@dnd-kit/core'
import {
  IconArrowLeft, IconCheck, IconDeviceFloppy, IconGridDots, IconRotate2,
  IconRotateClockwise2,
} from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { LoadingState } from '../../components/common/AsyncState'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'
import { CanvasElement } from './CanvasElement'
import { HeaderManagerModal } from './HeaderManagerModal'
import { LayerPanel } from './LayerPanel'
import { PropertyPanel } from './PropertyPanel'
import type { DesignerDefinition, LayoutDocument, LayoutElement } from './types'

const DEFAULT_ZOOM = 3 // px per mm
const MIN_ZOOM = 1
const MAX_ZOOM = 6
const SNAP_THRESHOLD_MM = 2

const PAGE_SIZE_MM: Record<string, { width: number; height: number }> = {
  A4: { width: 210, height: 297 },
  A5: { width: 148, height: 210 },
  LETTER: { width: 215.9, height: 279.4 },
}

const ELEMENT_LABELS: Record<string, string> = {
  text: '文本', field: '字段', image: '图片', line: '分隔线', rect: '矩形', table: '表格',
}

const PREVIEW_SCENARIOS: Array<{ label: string; rows?: number; variant?: string }> = [
  { label: '样例数据' },
  { label: '10 行明细', rows: 10 },
  { label: '50 行明细', rows: 50 },
  { label: '100 行明细', rows: 100 },
  { label: '长文本', variant: 'longText' },
  { label: '空明细', variant: 'empty' },
]

interface HistoryState {
  past: LayoutDocument[]
  future: LayoutDocument[]
}

function newElementId(type: string): string {
  return `${type.slice(0, 2)}${Date.now().toString(36).slice(-5)}${Math.floor(Math.random() * 36).toString(36)}`
}

function defaultElement(type: string, x = 10, y = 10): LayoutElement {
  const base = { id: newElementId(type), type, x, y, w: 60, h: 8, visible: true } as LayoutElement
  switch (type) {
    case 'text': return { ...base, content: '文本', style: { fontSize: 12, align: 'left' } }
    case 'field': return { ...base, field: '', style: { fontSize: 9 } }
    case 'image': return { ...base, w: 40, h: 20, resourceId: 'SYS.LOGO' }
    case 'line': return { ...base, w: 80, h: 0.5, style: { lineWidth: 0.5 } }
    case 'rect': return { ...base, w: 80, h: 30, style: { borderWidth: 0.5, backgroundColor: '#ffffff' } }
    case 'table': return {
      ...base, w: 180, h: 60, dataSource: 'details', showHeader: true,
      columns: [
        { field: 'DETAILS.PRO_NO', label: '料号', width: 40 },
        { field: 'DETAILS.QTY', label: '数量', width: 30, align: 'right' },
      ],
    }
    default: return base
  }
}

function allElementsOf(doc: LayoutDocument): LayoutElement[] {
  return [
    ...doc.sections.header.elements, ...doc.sections.content.elements, ...doc.sections.footer.elements,
  ]
}

function updateElement(doc: LayoutDocument, id: string, patch: Partial<LayoutElement>): LayoutDocument {
  const map = (el: LayoutElement) => {
    if (el.id !== id) return el
    return { ...el, ...patch }
  }
  return {
    ...doc,
    sections: {
      header: { ...doc.sections.header, elements: doc.sections.header.elements.map(map) },
      content: { ...doc.sections.content, elements: doc.sections.content.elements.map(map) },
      footer: { ...doc.sections.footer, elements: doc.sections.footer.elements.map(map) },
    },
  }
}

function addElementTo(doc: LayoutDocument, element: LayoutElement): LayoutDocument {
  return {
    ...doc,
    sections: {
      ...doc.sections,
      content: { ...doc.sections.content, elements: [...doc.sections.content.elements, element] },
    },
  }
}

function removeElements(doc: LayoutDocument, ids: Set<string>): LayoutDocument {
  const keep = (el: LayoutElement) => !ids.has(el.id)
  return {
    ...doc,
    sections: {
      header: { ...doc.sections.header, elements: doc.sections.header.elements.filter(keep) },
      content: { ...doc.sections.content, elements: doc.sections.content.elements.filter(keep) },
      footer: { ...doc.sections.footer, elements: doc.sections.footer.elements.filter(keep) },
    },
  }
}

/** 吸附：把值吸附到候选线（阈值内返回吸附值，否则原值）。 */
function snapValue(value: number, candidates: number[]): { value: number; snapped: boolean } {
  let best = value
  let bestDist = SNAP_THRESHOLD_MM
  let snapped = false
  for (const candidate of candidates) {
    const dist = Math.abs(value - candidate)
    if (dist <= bestDist) {
      bestDist = dist
      best = candidate
      snapped = true
    }
  }
  return { value: best, snapped }
}

function collectSnapCandidates(
  doc: LayoutDocument, activeId: string, contentWidth: number, contentHeight: number,
): { xs: number[]; ys: number[] } {
  const xs: number[] = [0, contentWidth, contentWidth / 2]
  const ys: number[] = [0, contentHeight]
  for (const el of allElementsOf(doc)) {
    if (el.id === activeId) continue
    xs.push(el.x, el.x + el.w)
    ys.push(el.y, el.y + el.h)
  }
  return { xs, ys }
}

function PaletteItem({ type, label, onDragStart }: {
  type: string
  label: string
  onDragStart: () => void
}) {
  const { attributes, listeners, setNodeRef } = useDraggable({ id: `palette:${type}` })
  return (
    <button
      ref={setNodeRef}
      type="button"
      className="btn btn-outline-secondary btn-sm"
      {...attributes}
      {...listeners}
      onPointerDown={onDragStart}
    >
      {label}
    </button>
  )
}

export function LayoutDesignerPage() {
  const { moduleId } = useParams<{ moduleId: string }>()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [doc, setDoc] = useState<LayoutDocument | null>(null)
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set())
  const [history, setHistory] = useState<HistoryState>({ past: [], future: [] })
  const [zoom, setZoom] = useState(DEFAULT_ZOOM)
  const [showGrid, setShowGrid] = useState(true)
  const [saved, setSaved] = useState(false)
  const [clipboard, setClipboard] = useState<LayoutElement[]>([])
  const [dragType, setDragType] = useState<string | null>(null)
  const [marquee, setMarquee] = useState<{ x: number; y: number; w: number; h: number } | null>(null)
  const [showHeaderManager, setShowHeaderManager] = useState(false)
  const [previewScenario, setPreviewScenario] = useState(0)
  const canvasRef = useRef<HTMLDivElement | null>(null)
  const pageRef = useRef<HTMLDivElement | null>(null)
  const marqueeRef = useRef<{ startX: number; startY: number } | null>(null)

  const definition = useQuery({
    queryKey: ['layout-designer', moduleId],
    queryFn: () => apiClient.get<DesignerDefinition>(`/layout-designer/${moduleId}/definition`),
    enabled: moduleId != null,
    // 403（无设计权限）等业务错误不重试：避免每次进入白等数秒指数退避
    retry: (failureCount, error) => {
      if (error instanceof ApiError && (error.status === 401 || error.status === 403)) return false
      return failureCount < 2
    },
  })

  useEffect(() => {
    if (!definition.data) return
    try {
      setDoc(JSON.parse(definition.data.layoutJson) as LayoutDocument)
      setHistory({ past: [], future: [] })
      setSelectedIds(new Set())
      setSaved(definition.data.isCustom)
    } catch {
      // 解析失败保持空
    }
  }, [definition.data])

  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 3 } }))

  const commit = useCallback((next: LayoutDocument) => {
    if (doc == null) return
    setHistory((h) => ({ past: [...h.past.slice(-49), doc], future: [] }))
    setDoc(next)
    setSaved(false)
  }, [doc])

  const undo = useCallback(() => {
    if (doc == null) return
    const prev = history.past.at(-1)
    if (!prev) return
    setHistory((h) => ({ past: h.past.slice(0, -1), future: [doc, ...h.future] }))
    setDoc(prev)
    setSaved(false)
  }, [doc, history])

  const redo = useCallback(() => {
    if (doc == null) return
    const next = history.future[0]
    if (!next) return
    setHistory((h) => ({ past: [...h.past, doc], future: h.future.slice(1) }))
    setDoc(next)
    setSaved(false)
  }, [doc, history])

  const saveMutation = useMutation({
    mutationFn: () => apiClient.post(`/layout-designer/${moduleId}/save`, {
      layoutJson: JSON.stringify(doc),
      clientId: null,
    }),
    onSuccess: () => {
      setSaved(true)
      void queryClient.invalidateQueries({ queryKey: ['layout-designer', moduleId] })
    },
  })

  const previewMutation = useMutation({
    mutationFn: async () => {
      const scenario = PREVIEW_SCENARIOS[previewScenario]
      const blob = await apiClient.postFile(`/layout-designer/${moduleId}/preview`, {
        layoutJson: JSON.stringify(doc),
        clientId: null,
        rows: scenario.rows ?? null,
        variant: scenario.variant ?? null,
      })
      const url = URL.createObjectURL(blob)
      window.open(url, '_blank')
      setTimeout(() => URL.revokeObjectURL(url), 60000)
    },
  })

  const selectedElements = useMemo(() => {
    if (doc == null) return []
    return allElementsOf(doc).filter((el) => selectedIds.has(el.id))
  }, [doc, selectedIds])

  const geometry = useMemo(() => {
    if (doc == null) return null
    const page = PAGE_SIZE_MM[doc.page.size.toUpperCase()] ?? PAGE_SIZE_MM.A4
    const headerH = (doc.sections.header.height ?? 10)
    const footerH = (doc.sections.footer.height ?? 10)
    const contentH = page.height - doc.page.margin.top - doc.page.margin.bottom - headerH - footerH
    const marginLeft = doc.page.margin.left
    return {
      pageWidthMm: page.width,
      pageHeightMm: page.height,
      pageWidth: page.width * zoom,
      headerTopMm: doc.page.margin.top,
      headerHmm: headerH,
      contentTopMm: doc.page.margin.top + headerH,
      contentHmm: Math.max(contentH, 20),
      contentWidth: page.width - doc.page.margin.left - doc.page.margin.right,
      footerTopMm: page.height - doc.page.margin.bottom - footerH,
      footerHmm: footerH,
      pageHeight: page.height * zoom,
      marginLeftMm: marginLeft,
      marginLeft: marginLeft * zoom,
    }
  }, [doc, zoom])

  const fitZoom = useCallback(() => {
    const wrap = canvasRef.current
    if (doc == null || wrap == null) return
    const page = PAGE_SIZE_MM[doc.page.size.toUpperCase()] ?? PAGE_SIZE_MM.A4
    const available = Math.max(wrap.clientWidth - 48, 100)
    const next = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, available / page.width))
    setZoom(Math.round(next * 10) / 10)
  }, [doc])

  // ===== 快捷键 =====
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement
      if (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT') return
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'z') {
        event.preventDefault()
        if (event.shiftKey) redo(); else undo()
        return
      }
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'y') {
        event.preventDefault()
        redo()
        return
      }
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'c' && selectedElements.length > 0) {
        event.preventDefault()
        setClipboard(selectedElements.map((el) => ({ ...el, id: newElementId(el.type) })))
        return
      }
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'v' && clipboard.length > 0 && doc != null) {
        event.preventDefault()
        let next = doc
        for (const el of clipboard) next = addElementTo(next, { ...el, x: el.x + 3, y: el.y + 3 })
        commit(next)
        return
      }
      if ((event.key === 'Delete' || event.key === 'Backspace') && selectedIds.size > 0) {
        event.preventDefault()
        commit(removeElements(doc!, selectedIds))
        setSelectedIds(new Set())
        return
      }
      const arrows: Record<string, [number, number]> = {
        ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1],
      }
      const arrow = arrows[event.key]
      if (arrow && selectedIds.size > 0 && doc != null) {
        event.preventDefault()
        const step = event.shiftKey ? 2 : 0.5
        const [dx, dy] = arrow
        let next = doc
        for (const el of selectedElements) {
          next = updateElement(next, el.id, {
            x: Math.max(0, Math.round((el.x + dx * step) * 10) / 10),
            y: Math.max(0, Math.round((el.y + dy * step) * 10) / 10),
          })
        }
        commit(next)
      }
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [commit, undo, redo, selectedIds, selectedElements, clipboard, doc])

  // ===== 拖拽（移动 / 拖入） =====
  const applyMoveWithSnap = (activeId: string, deltaX: number, deltaY: number) => {
    if (doc == null || geometry == null) return
    const element = allElementsOf(doc).find((el) => el.id === activeId)
    if (!element) return
    const target = selectedIds.has(activeId) && selectedIds.size > 1 ? selectedIds : new Set([activeId])
    const dxMm = deltaX / zoom
    const dyMm = deltaY / zoom
    if (Math.abs(dxMm) < 0.01 && Math.abs(dyMm) < 0.01) return
    const snap = selectedIds.size <= 1
      ? collectSnapCandidates(doc, activeId, geometry.contentWidth, geometry.contentHmm)
      : { xs: [] as number[], ys: [] as number[] }
    const sx = snap.xs.length > 0 ? snapValue(element.x + dxMm, snap.xs).value : element.x + dxMm
    const sy = snap.ys.length > 0 ? snapValue(element.y + dyMm, snap.ys).value : element.y + dyMm
    let next = doc
    for (const id of target) {
      const el = allElementsOf(next).find((e) => e.id === id)
      if (!el) continue
      next = updateElement(next, id, {
        x: Math.max(0, Math.round(((id === activeId ? sx : el.x + dxMm)) * 10) / 10),
        y: Math.max(0, Math.round(((id === activeId ? sy : el.y + dyMm)) * 10) / 10),
      })
    }
    commit(next)
  }

  const onDragStart = (event: DragStartEvent) => {
    const id = String(event.active.id)
    if (id.startsWith('palette:')) setDragType(id.slice(8))
  }

  const onDragEnd = (event: DragEndEvent) => {
    const id = String(event.active.id)
    if (id.startsWith('palette:')) {
      const type = id.slice(8)
      const translated = event.active.rect.current.translated
      const page = pageRef.current
      if (translated && page && geometry != null) {
        const rect = page.getBoundingClientRect()
        const xMm = Math.max(0, (translated.left - rect.left) / zoom - geometry.marginLeftMm)
        const yMm = Math.max(0, (translated.top - rect.top) / zoom - geometry.contentTopMm)
        commit(addElementTo(doc!, defaultElement(type, Math.round(xMm * 10) / 10, Math.round(yMm * 10) / 10)))
      }
      setDragType(null)
      return
    }
    applyMoveWithSnap(id, event.delta.x ?? 0, event.delta.y ?? 0)
  }

  // ===== 框选 =====
  const onCanvasPointerDown = (event: React.PointerEvent) => {
    if (!doc || event.button !== 0) return
    if ((event.target as HTMLElement).closest('[data-element]')) return
    if (event.shiftKey) return // Shift+点击保留多选由元素 onClick 处理
    setSelectedIds(new Set())
    marqueeRef.current = { startX: event.clientX, startY: event.clientY }
    const start = { x: event.clientX, y: event.clientY }
    const handleMove = (move: PointerEvent) => {
      const rect = pageRef.current?.getBoundingClientRect()
      if (!rect) return
      const x = Math.min(start.x, move.clientX) - rect.left
      const y = Math.min(start.y, move.clientY) - rect.top
      const w = Math.abs(move.clientX - start.x)
      const h = Math.abs(move.clientY - start.y)
      setMarquee({ x, y, w, h })
    }
    const handleUp = (up: PointerEvent) => {
      window.removeEventListener('pointermove', handleMove)
      window.removeEventListener('pointerup', handleUp)
      setMarquee(null)
      const rect = pageRef.current?.getBoundingClientRect()
      if (!rect || geometry == null) return
      const leftMm = Math.min(start.x, up.clientX) / zoom - geometry.marginLeftMm
      const topMm = Math.min(start.y, up.clientY) / zoom
      const rightMm = Math.max(start.x, up.clientX) / zoom - geometry.marginLeftMm
      const bottomMm = Math.max(start.y, up.clientY) / zoom
      const hit = allElementsOf(doc).filter((el) => {
        const elLeft = geometry.marginLeftMm + el.x
        const elTop = sectionTopMmOf(el, doc, geometry) + el.y
        const elRight = elLeft + el.w
        const elBottom = elTop + el.h
        return elLeft < rightMm && elRight > leftMm && elTop < bottomMm && elBottom > topMm
      })
      if (hit.length > 0) setSelectedIds(new Set(hit.map((el) => el.id)))
    }
    window.addEventListener('pointermove', handleMove)
    window.addEventListener('pointerup', handleUp)
  }

  // ===== 对齐 =====
  const alignSelected = (action: 'left' | 'right' | 'hc' | 'top' | 'bottom' | 'vc' | 'equal-w' | 'equal-h') => {
    if (doc == null || selectedElements.length < 2) return
    const els = selectedElements
    const minX = Math.min(...els.map((el) => el.x))
    const maxX = Math.max(...els.map((el) => el.x + el.w))
    const minY = Math.min(...els.map((el) => el.y))
    const maxY = Math.max(...els.map((el) => el.y + el.h))
    const centerX = (minX + maxX) / 2
    const centerY = (minY + maxY) / 2
    const maxW = Math.max(...els.map((el) => el.w))
    const maxH = Math.max(...els.map((el) => el.h))
    let next = doc
    for (const el of els) {
      const patch: Partial<LayoutElement> = {}
      switch (action) {
        case 'left': patch.x = minX; break
        case 'right': patch.x = maxX - el.w; break
        case 'hc': patch.x = centerX - el.w / 2; break
        case 'top': patch.y = minY; break
        case 'bottom': patch.y = maxY - el.h; break
        case 'vc': patch.y = centerY - el.h / 2; break
        case 'equal-w': patch.w = maxW; break
        case 'equal-h': patch.h = maxH; break
      }
      next = updateElement(next, el.id, patch)
    }
    commit(next)
  }

  const onResize = (id: string, newW: number, newH: number) => {
    if (doc == null) return
    commit(updateElement(doc, id, {
      w: Math.round(newW * 10) / 10,
      h: Math.round(newH * 10) / 10,
    }))
  }

  const reorderLayer = (id: string, direction: -1 | 1) => {
    if (doc == null) return
    const section = (['header', 'content', 'footer'] as const).find((key) =>
      doc.sections[key].elements.some((el) => el.id === id))
    if (!section) return
    const elements = doc.sections[section].elements
    const index = elements.findIndex((el) => el.id === id)
    const target = index + direction
    if (index < 0 || target < 0 || target >= elements.length) return
    const next = [...elements]
    ;[next[index], next[target]] = [next[target], next[index]]
    commit({
      ...doc,
      sections: {
        ...doc.sections,
        [section]: { ...doc.sections[section], elements: next },
      },
    })
  }

  const toggleLayerVisible = (id: string) => {
    if (doc == null) return
    const el = allElementsOf(doc).find((e) => e.id === id)
    if (!el) return
    commit(updateElement(doc, id, { visible: el.visible === false }))
  }

  const sectionTopMmOf = (el: LayoutElement, d: LayoutDocument, geo: NonNullable<typeof geometry>): number => {
    if (d.sections.header.elements.includes(el)) return geo.headerTopMm
    if (d.sections.footer.elements.includes(el)) return geo.footerTopMm
    return geo.contentTopMm
  }

  if (definition.isLoading || doc == null || geometry == null) {
    return <div className="p-4"><LoadingState label="加载版式定义…" /></div>
  }
  if (definition.isError) {
    const forbidden = definition.error instanceof ApiError
      && (definition.error.status === 401 || definition.error.status === 403)
    return (
      <div className="p-4">
        <div className="alert alert-danger">
          {forbidden
            ? '无版式设计权限：需要管理员在权限管理中为当前账号授予 FORM_DESIGN_TAG（完整设计）或 FORM_ADJUST_TAG（微调）权限位。'
            : '加载失败：无权访问或模块无内置版式。'}
        </div>
        <Button variant="secondary" onClick={() => navigate(-1)}>返回</Button>
      </div>
    )
  }

  const canDesign = definition.data?.mode.canDesign === true
  const canEdit = definition.data != null && (definition.data.mode.canDesign || definition.data.mode.canAdjust)
  const simpleMode = canEdit && !canDesign

  const sectionSpecs = [
    { key: 'header', topMm: geometry.headerTopMm, hMm: geometry.headerHmm },
    { key: 'content', topMm: geometry.contentTopMm, hMm: geometry.contentHmm },
    { key: 'footer', topMm: geometry.footerTopMm, hMm: geometry.footerHmm },
  ] as const

  const selected = selectedElements[0] ?? null

  return (
    <div className="d-flex flex-column vh-100">
      <div className="d-flex align-items-center gap-2 border-bottom px-3 py-2 bg-white">
        <Button variant="secondary" size="sm" onClick={() => navigate(-1)}>
          <IconArrowLeft size={16} /> 返回
        </Button>
        <strong className="ms-2">{definition.data?.title ?? ''} · 版式设计器</strong>
        {!canEdit && <span className="badge bg-danger">无设计权限</span>}
        <div className="ms-auto d-flex align-items-center gap-2">
          <Button variant="secondary" size="sm" disabled={history.past.length === 0} onClick={undo}>
            <IconRotate2 size={16} /> 撤销
          </Button>
          <Button variant="secondary" size="sm" disabled={history.future.length === 0} onClick={redo}>
            <IconRotateClockwise2 size={16} /> 重做
          </Button>
          <Button variant="secondary" size="sm" onClick={() => setShowGrid((g) => !g)}>
            <IconGridDots size={16} /> 网格
          </Button>
          <Button variant="secondary" size="sm" onClick={() => setShowHeaderManager(true)} title="页头字典引用">
            页头
          </Button>
          <select
            className="form-select form-select-sm"
            style={{ width: 86 }}
            aria-label="画布缩放"
            value={zoom}
            onChange={(e) => setZoom(Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, Number(e.target.value))))}
          >
            {[1, 1.5, 2, 2.5, 3, 4, 5, 6].map((z) => (
              <option key={z} value={z}>{Math.round(z * 100 / 3)}%</option>
            ))}
          </select>
          <Button variant="secondary" size="sm" onClick={fitZoom} title="适配窗口">适配</Button>
          <select
            className="form-select form-select-sm"
            style={{ width: 120 }}
            aria-label="预览数据场景"
            value={previewScenario}
            onChange={(e) => setPreviewScenario(Number(e.target.value))}
          >
            {PREVIEW_SCENARIOS.map((s, index) => (
              <option key={s.label} value={index}>{s.label}</option>
            ))}
          </select>
          <Button variant="secondary" size="sm" disabled={!canEdit || previewMutation.isPending}
            onClick={() => previewMutation.mutate()}>
            <IconCheck size={16} /> 预览
          </Button>
          <Button size="sm" disabled={!canEdit || saveMutation.isPending}
            onClick={() => saveMutation.mutate()}>
            <IconDeviceFloppy size={16} /> {saved ? '已保存' : '保存'}
          </Button>
        </div>
      </div>

      {selectedElements.length > 1 && (
        <div className="d-flex align-items-center gap-1 border-bottom px-3 py-1 bg-white">
          <span className="text-secondary small me-2">对齐（{selectedElements.length} 个元素）：</span>
          <Button variant="ghost" size="sm" title="左对齐" onClick={() => alignSelected('left')}>左</Button>
          <Button variant="ghost" size="sm" title="水平居中" onClick={() => alignSelected('hc')}>水平中</Button>
          <Button variant="ghost" size="sm" title="右对齐" onClick={() => alignSelected('right')}>右</Button>
          <Button variant="ghost" size="sm" title="顶对齐" onClick={() => alignSelected('top')}>上</Button>
          <Button variant="ghost" size="sm" title="垂直居中" onClick={() => alignSelected('vc')}>垂直中</Button>
          <Button variant="ghost" size="sm" title="底对齐" onClick={() => alignSelected('bottom')}>下</Button>
          <span className="mx-1 text-secondary">|</span>
          <Button variant="ghost" size="sm" title="等宽" onClick={() => alignSelected('equal-w')}>等宽</Button>
          <Button variant="ghost" size="sm" title="等高" onClick={() => alignSelected('equal-h')}>等高</Button>
        </div>
      )}

      <div className="d-flex flex-grow-1 overflow-hidden">
        <DndContext sensors={sensors} onDragStart={onDragStart} onDragEnd={onDragEnd}>
          {canDesign && (
            <div className="border-end p-3 bg-white" style={{ width: 170 }}>
              <div className="text-secondary small mb-2">元素库（拖入画布）</div>
              <div className="d-flex flex-column gap-2">
                {Object.keys(ELEMENT_LABELS).map((type) => (
                  <PaletteItem key={type} type={type} label={ELEMENT_LABELS[type]}
                    onDragStart={() => setDragType(type)} />
                ))}
              </div>
              <div className="text-secondary small mt-3">
                快捷键：Ctrl+Z/Y 撤销重做、Ctrl+C/V 复制粘贴、Delete 删除、方向键微调（Shift 加速）。
              </div>
            </div>
          )}

          <div
            ref={canvasRef}
            className="flex-grow-1 overflow-auto bg-secondary-subtle p-4 position-relative"
            onWheel={(e) => {
              if (!e.ctrlKey) return
              e.preventDefault()
              setZoom((z) => Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, Math.round((z + (e.deltaY < 0 ? 0.25 : -0.25)) * 20) / 20)))
            }}
            onPointerDown={onCanvasPointerDown}
          >
            <div
              ref={pageRef}
              className="position-relative bg-white shadow-sm"
              style={{
                width: geometry.pageWidth,
                height: geometry.pageHeight,
                margin: '0 auto',
                backgroundImage: showGrid
                  ? 'linear-gradient(rgba(0,0,0,0.05) 1px, transparent 1px), linear-gradient(90deg, rgba(0,0,0,0.05) 1px, transparent 1px)'
                  : undefined,
                backgroundSize: `${5 * zoom}px ${5 * zoom}px`,
              }}
              onClick={() => setSelectedIds(new Set())}
            >
              {sectionSpecs.map(({ key, topMm, hMm }) => {
                const elements = doc.sections[key].elements.filter((el) => el.visible !== false)
                return (
                  <div key={key} className="position-absolute" style={{
                    top: topMm * zoom, left: 0, width: geometry.pageWidth, height: hMm * zoom,
                  }}>
                    <div className="position-absolute text-secondary text-uppercase"
                      style={{ top: -14, left: geometry.marginLeft, fontSize: 10 }}>
                      {key}
                    </div>
                    {elements.map((el) => (
                      <CanvasElement
                        key={el.id}
                        element={el}
                        zoom={zoom}
                        left={geometry.marginLeft + el.x * zoom}
                        top={topMm * zoom + el.y * zoom}
                        selected={selectedIds.has(el.id)}
                        canEdit={canEdit}
                        onSelect={(e) => {
                          e.stopPropagation()
                          setSelectedIds((prev) => {
                            if (e.shiftKey) {
                              const next = new Set(prev)
                              if (next.has(el.id)) next.delete(el.id); else next.add(el.id)
                              return next
                            }
                            return new Set([el.id])
                          })
                        }}
                        onResize={(newW, newH) => onResize(el.id, newW, newH)}
                      />
                    ))}
                  </div>
                )
              })}
              {marquee && (
                <div className="position-absolute border border-primary bg-primary bg-opacity-10"
                  style={{ left: marquee.x, top: marquee.y, width: marquee.w, height: marquee.h, zIndex: 50 }} />
              )}
            </div>
            <DragOverlay dropAnimation={null}>
              {dragType && <div className="p-2 border bg-white shadow-sm">{ELEMENT_LABELS[dragType]}</div>}
            </DragOverlay>
          </div>
        </DndContext>

        <div className="border-start bg-white overflow-auto" style={{ width: 310 }}>
          <LayerPanel
            doc={doc}
            selectedIds={selectedIds}
            canAdjust={canDesign || simpleMode}
            onSelect={(id, additive) => setSelectedIds((prev) => {
              if (additive) {
                const next = new Set(prev)
                if (next.has(id)) next.delete(id); else next.add(id)
                return next
              }
              return new Set([id])
            })}
            onToggleVisible={toggleLayerVisible}
            onReorder={reorderLayer}
          />
          <div className="border-top">
            {selected && definition.data
              ? (
                <PropertyPanel
                  element={selected}
                  dataContract={definition.data.dataContract}
                  systemFields={definition.data.systemFields}
                  canDesign={canDesign}
                  simpleMode={simpleMode}
                  onMove={simpleMode ? (dxMm, dyMm) => commit(updateElement(doc, selected.id, {
                    x: Math.max(0, Math.round((selected.x + dxMm) * 10) / 10),
                    y: Math.max(0, Math.round((selected.y + dyMm) * 10) / 10),
                  })) : undefined}
                  onChange={(patch) => commit(updateElement(doc, selected.id, patch))}
                  onRemove={() => {
                    commit(removeElements(doc, new Set([selected.id])))
                    setSelectedIds((prev) => { const next = new Set(prev); next.delete(selected.id); return next })
                  }}
                />
              )
              : <div className="p-4 text-secondary">选择画布中的元素以编辑属性。</div>}
          </div>
        </div>
      </div>
      {showHeaderManager && definition.data && (
        <HeaderManagerModal
          moduleId={moduleId!}
          currentHeaderId={definition.data.headerId}
          tailId={definition.data.tailId}
          canDesign={canDesign}
          onClose={() => setShowHeaderManager(false)}
        />
      )}
    </div>
  )
}
