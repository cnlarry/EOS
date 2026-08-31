import {
  DndContext, DragOverlay, PointerSensor, useDraggable, useSensor, useSensors,
  type DragEndEvent, type DragMoveEvent, type DragStartEvent,
} from '@dnd-kit/core'
import {
  IconArrowLeft, IconCheck, IconDeviceFloppy, IconGridDots, IconRotate2,
  IconRotateClockwise2, IconTypography, IconBraces, IconPhoto, IconMinus,
  IconRectangle, IconTable, IconBarcode, IconStack2, IconSettings, IconHistory, IconChevronsRight,
} from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { LoadingState } from '../../components/common/AsyncState'
import { Button } from '../../components/ui/Button'
import { usePageBreadcrumb } from '../../components/layout/PageBreadcrumbContext'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'
import { CanvasElement } from './CanvasElement'
import { ContextMenu } from './ContextMenu'
import './LayoutDesigner.css'
import { HeaderManagerModal } from './HeaderManagerModal'
import { HistoryModal } from './HistoryModal'
import { LayerPanel } from './LayerPanel'
import { OperationHistoryPanel } from './OperationHistoryPanel'
import { PageTemplateModal } from './PageTemplateModal'
import { PreviewDialog } from './PreviewDialog'
import { PropertyPanel } from './PropertyPanel'
import { TemplateModal } from './TemplateModal'
import type {
  DesignerDefinition, LayoutDocument, LayoutElement, LayoutTemplateInfo, PreviewSettings,
} from './types'

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
  text: '文本', field: '字段', image: '图片', line: '分隔线', rect: '矩形', table: '表格', barcode: '条形码',
}

const ELEMENT_ICONS: Record<string, React.ReactNode> = {
  text: <IconTypography size={18} />,
  field: <IconBraces size={18} />,
  image: <IconPhoto size={18} />,
  line: <IconMinus size={18} />,
  rect: <IconRectangle size={18} />,
  table: <IconTable size={18} />,
  barcode: <IconBarcode size={18} />,
}

const QUICK_TEXT_ELEMENTS: Array<{ label: string; content: string }> = [
  { label: '页码', content: '{{SYS.PAGE_NUMBER}}' },
  { label: '总页数', content: '{{SYS.TOTAL_PAGES}}' },
  { label: '第X/共Y页', content: '第 {{SYS.PAGE_NUMBER}} / {{SYS.TOTAL_PAGES}} 页' },
  { label: '日期', content: '{{SYS.TODAY}}' },
]

interface HistoryEntry {
  desc: string
  doc: LayoutDocument
}

interface HistoryState {
  past: HistoryEntry[]
  future: HistoryEntry[]
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
    case 'barcode': return { ...base, w: 60, h: 60, barcodeType: 'qrcode', content: '{{MASTER.ORDER_NO}}' }
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

function PaletteItem({ type, label }: {
  type: string
  label: string
}) {
  const { attributes, listeners, setNodeRef } = useDraggable({ id: `palette:${type}` })
  return (
    <button
      ref={setNodeRef}
      type="button"
      className="btn btn-outline-secondary d-flex align-items-center justify-content-center"
      style={{ width: 38, height: 38, touchAction: 'none', padding: 0 }}
      title={label}
      aria-label={label}
      {...attributes}
      {...listeners}
    >
      {ELEMENT_ICONS[type]}
    </button>
  )
}

export function LayoutDesignerPage() {
  const { moduleId } = useParams<{ moduleId: string }>()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { setBreadcrumb } = usePageBreadcrumb()
  const [doc, setDoc] = useState<LayoutDocument | null>(null)
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set())
  const [history, setHistory] = useState<HistoryState>({ past: [], future: [] })
  const [zoom, setZoom] = useState(DEFAULT_ZOOM)
  const [showGrid, setShowGrid] = useState(true)
  const [saved, setSaved] = useState(false)
  const [clipboard, setClipboard] = useState<LayoutElement[]>([])
  const [styleClipboard, setStyleClipboard] = useState<LayoutElement['style'] | null>(null)
  const [dragType, setDragType] = useState<string | null>(null)
  const [marquee, setMarquee] = useState<{ x: number; y: number; w: number; h: number } | null>(null)
  const [showHeaderManager, setShowHeaderManager] = useState(false)
  const [showPreviewDialog, setShowPreviewDialog] = useState(false)
  const [showHistory, setShowHistory] = useState(false)
  const [showTemplates, setShowTemplates] = useState(false)
  const [showPageTemplates, setShowPageTemplates] = useState(false)
  const [viewMode, setViewMode] = useState<'edit' | 'preview'>('edit')
  const [previewUrl, setPreviewUrl] = useState<string | null>(null)
  const [previewSettings, setPreviewSettings] = useState<PreviewSettings>({ source: 'sample' })
  const [contextMenu, setContextMenu] = useState<{ x: number; y: number; elementId: string | null } | null>(null)
  const [renderPreviewError, setRenderPreviewError] = useState<string | null>(null)
  const [rightTab, setRightTab] = useState<'layers' | 'properties' | 'history'>('layers')
  const [rightCollapsed, setRightCollapsed] = useState(false)
  const [draft, setDraft] = useState<LayoutDocument | null>(null)
  const [dragInfo, setDragInfo] = useState<{ id: string; x: number; y: number; pageY: number; w: number; h: number } | null>(null)
  const [snapLines, setSnapLines] = useState<{ xs: number[]; ys: number[] }>({ xs: [], ys: [] })
  const templateLayoutMutation = useMutation({
    mutationFn: (formatId: string) =>
      apiClient.get<{ layoutJson: string }>(`/layout-designer/templates/${formatId}/layout`),
  })
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
      const loaded = JSON.parse(definition.data.layoutJson) as LayoutDocument
      setDoc(loaded)
      setHistory({ past: [], future: [] })
      setSelectedIds(new Set())
      setSaved(definition.data.isCustom)
      // 自动保存草稿：仅未定制时提示恢复（定制版式以库中版本为准）
      if (!definition.data.isCustom) {
        const raw = localStorage.getItem(`erp-layout-draft:${moduleId}`)
        if (raw) {
          try {
            const draftDoc = JSON.parse(raw) as LayoutDocument
            if (draftDoc?.schemaVersion === 1 && JSON.stringify(draftDoc) !== definition.data.layoutJson) {
              setDraft(draftDoc)
            }
          } catch {
            localStorage.removeItem(`erp-layout-draft:${moduleId}`)
          }
        }
      } else {
        setDraft(null)
      }
    } catch {
      // 解析失败保持空
    }
  }, [definition.data, moduleId])

  // 页面级面包屑（报表中心 > 版式设计器）：与其它系统页面风格一致
  useEffect(() => {
    const title = definition.data?.title ? `${definition.data.title} · 版式设计` : '版式设计'
    setBreadcrumb({ leads: [{ label: '报表中心', to: '/report-center' }], title })
    return () => setBreadcrumb(null)
  }, [definition.data?.title, setBreadcrumb])

  // 自动保存草稿（防丢失，变更 500ms 后落 localStorage；保存成功即清除）
  useEffect(() => {
    if (doc == null || moduleId == null || saved) return
    const timer = setTimeout(() => {
      localStorage.setItem(`erp-layout-draft:${moduleId}`, JSON.stringify(doc))
    }, 500)
    return () => clearTimeout(timer)
  }, [doc, saved, moduleId])

  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 3 } }))

  const commit = useCallback((next: LayoutDocument, desc = '编辑') => {
    if (doc == null) return
    setHistory((h) => ({ past: [...h.past.slice(-49), { desc, doc }], future: [] }))
    setDoc(next)
    setSaved(false)
  }, [doc])

  const undo = useCallback(() => {
    if (doc == null) return
    const prev = history.past.at(-1)
    if (!prev) return
    setHistory((h) => ({ past: h.past.slice(0, -1), future: [{ desc: '当前', doc }, ...h.future] }))
    setDoc(prev.doc)
    setSaved(false)
  }, [doc, history])

  const redo = useCallback(() => {
    if (doc == null) return
    const next = history.future[0]
    if (!next) return
    setHistory((h) => ({ past: [...h.past, { desc: next.desc, doc }], future: h.future.slice(1) }))
    setDoc(next.doc)
    setSaved(false)
  }, [doc, history])

  const jumpToHistory = (index: number) => {
    const entry = history.past[index]
    if (!entry || doc == null) return
    const futureEntries: HistoryEntry[] = [
      ...history.past.slice(index + 1).map((e) => ({ desc: e.desc, doc: e.doc })),
      { desc: '当前', doc },
    ]
    setHistory({ past: history.past.slice(0, index), future: futureEntries })
    setDoc(entry.doc)
    setSaved(false)
  }

  const saveMutation = useMutation({
    mutationFn: () => apiClient.post(`/layout-designer/${moduleId}/save`, {
      layoutJson: JSON.stringify(doc),
      clientId: null,
    }),
    onSuccess: () => {
      setSaved(true)
      localStorage.removeItem(`erp-layout-draft:${moduleId}`)
      setDraft(null)
      void queryClient.invalidateQueries({ queryKey: ['layout-designer', moduleId] })
    },
  })

  const previewMutation = useMutation({
    mutationFn: async () => {
      // 先同步开窗（用户手势内），避免异步完成后被浏览器弹窗拦截
      const win = window.open('', '_blank')
      const blob = await apiClient.postFile(`/layout-designer/${moduleId}/preview`, {
        layoutJson: JSON.stringify(doc),
        clientId: null,
        rows: previewSettings.source === 'sample' ? previewSettings.rows ?? null : null,
        variant: previewSettings.source === 'sample' ? previewSettings.variant ?? null : null,
        key: previewSettings.source === 'real' && previewSettings.key
          ? previewSettings.key.split(',').map((k) => k.trim()).filter(Boolean)
          : null,
      })
      const url = URL.createObjectURL(blob)
      if (win) win.location.href = url
      else window.open(url, '_blank')
      setTimeout(() => URL.revokeObjectURL(url), 60000)
    },
    onError: (error) => {
      const message = error instanceof ApiError ? error.body.message : '预览生成失败，请检查后端服务与模块样例数据。'
      window.alert(message)
    },
  })

  // WYSIWYG 渲染预览模式：生成 PDF blob 供画布区 iframe 展示（所见即所得）
  const renderPreviewMutation = useMutation({
    mutationFn: async () => {
      const blob = await apiClient.postFile(`/layout-designer/${moduleId}/preview`, {
        layoutJson: JSON.stringify(doc),
        clientId: null,
        rows: previewSettings.source === 'sample' ? previewSettings.rows ?? null : null,
        variant: previewSettings.source === 'sample' ? previewSettings.variant ?? null : null,
        key: previewSettings.source === 'real' && previewSettings.key
          ? previewSettings.key.split(',').map((k) => k.trim()).filter(Boolean)
          : null,
      })
      if (previewUrl) URL.revokeObjectURL(previewUrl)
      const url = URL.createObjectURL(blob)
      setPreviewUrl(url)
    },
    onError: (error) => {
      setRenderPreviewError(
        error instanceof ApiError ? error.body.message : '渲染预览失败，请检查后端服务。')
    },
  })

  const enterPreviewMode = () => {
    setViewMode('preview')
    setRenderPreviewError(null)
    if (!renderPreviewMutation.isPending) void renderPreviewMutation.mutate()
  }

  const exitPreviewMode = () => {
    setViewMode('edit')
    if (previewUrl) {
      URL.revokeObjectURL(previewUrl)
      setPreviewUrl(null)
    }
  }

  const applyTemplate = (template: LayoutTemplateInfo) => {
    if (doc == null) return
    templateLayoutMutation.mutate(template.formatId, {
      onSuccess: (result) => {
        try {
          commit(JSON.parse(result.layoutJson) as LayoutDocument, '套用模板')
          setSelectedIds(new Set())
        } catch {
          // 模板 JSON 解析失败时保持当前画布
        }
      },
    })
  }

  const moveLayerToEnd = (id: string, toEnd: boolean) => {
    if (doc == null) return
    const section = (['header', 'content', 'footer'] as const).find((key) =>
      doc.sections[key].elements.some((el) => el.id === id))
    if (!section) return
    const elements = doc.sections[section].elements
    const index = elements.findIndex((el) => el.id === id)
    if (index < 0) return
    const next = [...elements]
    const [moved] = next.splice(index, 1)
    if (toEnd) next.push(moved); else next.unshift(moved)
    commit({
      ...doc,
      sections: {
        ...doc.sections,
        [section]: { ...doc.sections[section], elements: next },
      },
    }, '调整图层')
  }

  const handleCanvasContextMenu = (event: React.MouseEvent) => {
    event.preventDefault()
    event.stopPropagation()
    const elementDiv = (event.target as HTMLElement).closest('.canvas-element') as HTMLElement | null
    const elementId = elementDiv?.dataset.elementId ?? null
    setContextMenu({ x: event.clientX, y: event.clientY, elementId })
  }

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
      if ((event.ctrlKey || event.metaKey) && event.shiftKey && event.key.toLowerCase() === 'c' && selectedElements.length > 0) {
        event.preventDefault()
        setStyleClipboard(selectedElements[0].style ?? {})
        return
      }
      if ((event.ctrlKey || event.metaKey) && event.shiftKey && event.key.toLowerCase() === 'v'
        && styleClipboard && selectedIds.size > 0 && doc != null) {
        event.preventDefault()
        let next = doc
        for (const el of selectedElements) next = updateElement(next, el.id, { style: styleClipboard })
        commit(next, '粘贴')
        return
      }
      if ((event.ctrlKey || event.metaKey) && !event.shiftKey && event.key.toLowerCase() === 'c' && selectedElements.length > 0) {
        event.preventDefault()
        setClipboard(selectedElements.map((el) => ({ ...el, id: newElementId(el.type) })))
        return
      }
      if ((event.ctrlKey || event.metaKey) && !event.shiftKey && event.key.toLowerCase() === 'v' && clipboard.length > 0 && doc != null) {
        event.preventDefault()
        let next = doc
        for (const el of clipboard) next = addElementTo(next, { ...el, x: el.x + 3, y: el.y + 3 })
        commit(next, '微调')
        return
      }
      if ((event.key === 'Delete' || event.key === 'Backspace') && selectedIds.size > 0) {
        event.preventDefault()
        commit(removeElements(doc!, selectedIds), '删除元素')
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
  }, [commit, undo, redo, selectedIds, selectedElements, clipboard, styleClipboard, doc])

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
    commit(next, '移动')
  }

  const onDragStart = (event: DragStartEvent) => {
    const id = String(event.active.id)
    if (id.startsWith('palette:')) setDragType(id.slice(8))
    setSnapLines({ xs: [], ys: [] })
  }

  const sectionTopMmOf = (el: LayoutElement, d: LayoutDocument): number => {
    if (d.sections.header.elements.includes(el)) return d.page.margin.top
    if (d.sections.footer.elements.includes(el)) {
      const page = PAGE_SIZE_MM[d.page.size.toUpperCase()] ?? PAGE_SIZE_MM.A4
      const footerH = (d.sections.footer.height ?? 10)
      return page.height - d.page.margin.bottom - footerH
    }
    return d.page.margin.top + (d.sections.header.height ?? 10)
  }

  const onDragMove = (event: DragMoveEvent) => {
    const id = String(event.active.id)
    if (id.startsWith('palette:') || doc == null || geometry == null) return
    const el = allElementsOf(doc).find((e) => e.id === id)
    if (!el) return
    const nx = el.x + (event.delta.x ?? 0) / zoom
    const ny = el.y + (event.delta.y ?? 0) / zoom
    const round1 = (v: number) => Math.round(v * 10) / 10
    const sectionTop = sectionTopMmOf(el, doc)
    setDragInfo({ id, x: round1(nx), y: round1(ny), pageY: sectionTop + round1(ny), w: el.w, h: el.h })
    if (selectedIds.size <= 1) {
      const snap = collectSnapCandidates(doc, id, geometry.contentWidth, geometry.contentHmm)
      const sx = snapValue(nx, snap.xs)
      const sy = snapValue(ny, snap.ys)
      setSnapLines({
        xs: sx.snapped ? [geometry.marginLeftMm + sx.value * zoom] : [],
        ys: sy.snapped ? [sectionTop * zoom + sy.value * zoom] : [],
      })
    }
  }

  const onDragEnd = (event: DragEndEvent) => {
    const id = String(event.active.id)
    setDragInfo(null)
    setSnapLines({ xs: [], ys: [] })
    if (id.startsWith('palette:')) {
      const type = id.slice(8)
      const translated = event.active.rect.current.translated
      const page = pageRef.current
      if (translated && page && geometry != null) {
        const rect = page.getBoundingClientRect()
        const xMm = Math.max(0, (translated.left - rect.left) / zoom - geometry.marginLeftMm)
        const yMm = Math.max(0, (translated.top - rect.top) / zoom - geometry.contentTopMm)
        commit(addElementTo(doc!, defaultElement(type, Math.round(xMm * 10) / 10, Math.round(yMm * 10) / 10)), '添加元素')
      }
      setDragType(null)
      return
    }
    applyMoveWithSnap(id, event.delta.x ?? 0, event.delta.y ?? 0)
  }

  // ===== 框选 =====
  const onCanvasPointerDown = (event: React.PointerEvent) => {
    if (!doc || event.button !== 0) return
    // 元素/手柄/弹层内按下不触发框选（拖拽移动与缩放由元素自身处理）
    if ((event.target as HTMLElement).closest('.canvas-element, .position-fixed')) return
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
      const leftMm = Math.min(start.x, up.clientX) / zoom
      const topMm = Math.min(start.y, up.clientY) / zoom
      const rightMm = Math.max(start.x, up.clientX) / zoom
      const bottomMm = Math.max(start.y, up.clientY) / zoom
      const hit = allElementsOf(doc).filter((el) => {
        const elLeft = geometry.marginLeftMm + el.x
        const elTop = sectionTopMmOf(el, doc) + el.y
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
  const alignSelected = (action: 'left' | 'right' | 'hc' | 'top' | 'bottom' | 'vc' | 'equal-w' | 'equal-h' | 'dist-h' | 'dist-v') => {
    if (doc == null || selectedElements.length < 2) return
    const els = selectedElements
    if ((action === 'dist-h' || action === 'dist-v') && els.length < 3) return
    const minX = Math.min(...els.map((el) => el.x))
    const maxX = Math.max(...els.map((el) => el.x + el.w))
    const minY = Math.min(...els.map((el) => el.y))
    const maxY = Math.max(...els.map((el) => el.y + el.h))
    const centerX = (minX + maxX) / 2
    const centerY = (minY + maxY) / 2
    const maxW = Math.max(...els.map((el) => el.w))
    const maxH = Math.max(...els.map((el) => el.h))
    let next = doc
    if (action === 'dist-h') {
      const sorted = [...els].sort((a, b) => a.x - b.x)
      const first = sorted[0]
      const last = sorted[sorted.length - 1]
      const innerW = sorted.slice(1, -1).reduce((sum, e) => sum + e.w, 0)
      const gap = (last.x - first.x - innerW) / (sorted.length - 1)
      let cursor = first.x + first.w + gap
      for (const e of sorted.slice(1, -1)) {
        next = updateElement(next, e.id, { x: Math.round(cursor * 10) / 10 })
        cursor += e.w + gap
      }
      commit(next, '对齐')
      return
    }
    if (action === 'dist-v') {
      const sorted = [...els].sort((a, b) => a.y - b.y)
      const first = sorted[0]
      const last = sorted[sorted.length - 1]
      const innerH = sorted.slice(1, -1).reduce((sum, e) => sum + e.h, 0)
      const gap = (last.y - first.y - innerH) / (sorted.length - 1)
      let cursor = first.y + first.h + gap
      for (const e of sorted.slice(1, -1)) {
        next = updateElement(next, e.id, { y: Math.round(cursor * 10) / 10 })
        cursor += e.h + gap
      }
      commit(next, '对齐')
      return
    }
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
    commit(next, '对齐')
  }

  const onResize = (id: string, newW: number, newH: number) => {
    if (doc == null) return
    commit(updateElement(doc, id, {
      w: Math.round(newW * 10) / 10,
      h: Math.round(newH * 10) / 10,
    }), '缩放')
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
    }, '调整图层')
  }

  const toggleLayerVisible = (id: string) => {
    if (doc == null) return
    const el = allElementsOf(doc).find((e) => e.id === id)
    if (!el) return
    commit(updateElement(doc, id, { visible: el.visible === false }), '显隐')
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
    <div className="erp-full-list-page">
      <div className="card erp-list-card">
        <div className="d-flex flex-column h-100">
      <div className="d-flex align-items-center gap-2 border-bottom px-3 py-2">
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
          <Button variant="secondary" size="sm" onClick={() => setShowTemplates(true)} title="模板库">
            模板
          </Button>
          <Button variant="secondary" size="sm" onClick={() => setShowHistory(true)} title="版本历史"
            disabled={!definition.data?.isCustom}>
            历史
          </Button>
          <Button variant="secondary" size="sm" onClick={() => setShowHeaderManager(true)} title="页头字典引用">
            页头
          </Button>
          <Button variant="secondary" size="sm" onClick={() => setShowPageTemplates(true)} title="多页版式模板">
            页模板
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
          <Button variant={viewMode === 'edit' ? 'primary' : 'secondary'} size="sm"
            onClick={() => viewMode === 'edit' ? enterPreviewMode() : exitPreviewMode()}>
            {viewMode === 'edit' ? '渲染预览' : '返回编辑'}
          </Button>
          <Button variant="secondary" size="sm" disabled={!canEdit}
            onClick={() => setShowPreviewDialog(true)} title="PDF 预览（样例/真实单据）">
            <IconCheck size={16} /> 预览
          </Button>
          <Button size="sm" disabled={!canEdit || saveMutation.isPending}
            onClick={() => saveMutation.mutate()}>
            <IconDeviceFloppy size={16} /> {saved ? '已保存' : '保存'}
          </Button>
        </div>
      </div>

      {draft && (
        <div className="d-flex align-items-center gap-2 border-bottom px-3 py-1 bg-warning-subtle">
          <span className="small">发现未保存的编辑草稿（自动保存）</span>
          <Button variant="secondary" size="sm"
            onClick={() => { setDoc(draft); setDraft(null); setSaved(false); setHistory({ past: [], future: [] }) }}>
            恢复草稿
          </Button>
          <Button variant="ghost" size="sm"
            onClick={() => {
              setDraft(null)
              localStorage.removeItem(`erp-layout-draft:${moduleId}`)
            }}>
            丢弃
          </Button>
        </div>
      )}

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
          <Button variant="ghost" size="sm" title="水平均布（3+ 元素）" disabled={selectedElements.length < 3}
            onClick={() => alignSelected('dist-h')}>水平均布</Button>
          <Button variant="ghost" size="sm" title="垂直均布（3+ 元素）" disabled={selectedElements.length < 3}
            onClick={() => alignSelected('dist-v')}>垂直均布</Button>
        </div>
      )}

      <div className="d-flex flex-grow-1 overflow-hidden">
        <DndContext sensors={sensors} onDragStart={onDragStart} onDragMove={onDragMove} onDragEnd={onDragEnd}>
          {canDesign && (
            <div className="border-end bg-white d-flex flex-column" style={{ width: 180 }}>
              <div className="p-3 overflow-auto">
                <div className="text-secondary small mb-2">元素库（拖入画布）</div>
                <div className="d-flex flex-wrap gap-2">
                  {Object.keys(ELEMENT_LABELS).map((type) => (
                    <PaletteItem key={type} type={type} label={ELEMENT_LABELS[type]} />
                  ))}
                </div>
                <div className="text-secondary small mt-3 mb-1">快捷文本</div>
                <div className="d-flex flex-column gap-1">
                  {QUICK_TEXT_ELEMENTS.map((item) => (
                    <Button key={item.label} variant="secondary" size="sm"
                      onClick={() => {
                        if (doc == null) return
                        commit(addElementTo(doc, {
                          ...defaultElement('text', 10, 10),
                          content: item.content,
                          style: { fontSize: 9, align: 'left' },
                        }), '添加文本')
                      }}>
                      {item.label}
                    </Button>
                  ))}
                </div>
                <div className="text-secondary small mt-3 mb-1">快捷键</div>
                <div className="text-secondary small">
                  Ctrl+Z/Y 撤销重做、Ctrl+C/V 复制粘贴、Ctrl+Shift+C/V 样式、Delete 删除、方向键微调。
                </div>
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
            onContextMenu={handleCanvasContextMenu}
          >
            {viewMode === 'preview' ? (
              renderPreviewError
                ? <div className="alert alert-danger p-4">{renderPreviewError}</div>
                : previewUrl
                ? (
                  <iframe
                    src={previewUrl}
                    title="渲染预览"
                    className="d-block mx-auto bg-white shadow-sm"
                    style={{ width: geometry.pageWidth, height: geometry.pageHeight, border: 0 }}
                  />
                )
                : <div className="text-secondary p-4">正在生成渲染预览…</div>
            ) : (
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
                {/* 水平标尺 */}
                <div className="position-absolute"
                  style={{ top: -24, left: 0, right: 0, height: 22, background: '#f8f9fa', borderBottom: '1px solid #dee2e6', zIndex: 5, overflow: 'hidden' }}>
                  {Array.from({ length: Math.ceil(geometry.contentWidth / 5) + 1 }, (_, i) => i * 5).map((mm) => (
                    <div key={`h-${mm}`} className="position-absolute"
                      style={{ left: geometry.marginLeftMm * zoom + mm * zoom - 0.5, top: 0, bottom: 0, width: 1, background: mm % 10 === 0 ? '#adb5bd' : '#e9ecef' }}>
                      {mm % 10 === 0 && (
                        <span className="position-absolute text-secondary" style={{ top: 4, left: 2, fontSize: 8, lineHeight: 1 }}>{mm}</span>
                      )}
                    </div>
                  ))}
                </div>
                {/* 垂直标尺 */}
                <div className="position-absolute"
                  style={{ top: 0, bottom: 0, left: -24, width: 22, background: '#f8f9fa', borderRight: '1px solid #dee2e6', zIndex: 5, overflow: 'hidden' }}>
                  {Array.from({ length: Math.ceil(geometry.pageHeightMm / 5) + 1 }, (_, i) => i * 5).map((mm) => (
                    <div key={`v-${mm}`} className="position-absolute"
                      style={{ top: mm * zoom - 0.5, left: 0, right: 0, height: 1, background: mm % 10 === 0 ? '#adb5bd' : '#e9ecef' }}>
                      {mm % 10 === 0 && (
                        <span className="position-absolute text-secondary" style={{ left: 4, top: -6, fontSize: 8, lineHeight: 1 }}>{mm}</span>
                      )}
                    </div>
                  ))}
                </div>
                {/* 实时对齐参考线 */}
                {snapLines.xs.map((x, i) => (
                  <div key={`sx-${i}`} className="position-absolute"
                    style={{ left: x, top: 0, bottom: 0, width: 1, background: '#206bc4', zIndex: 45 }} />
                ))}
                {snapLines.ys.map((y, i) => (
                  <div key={`sy-${i}`} className="position-absolute"
                    style={{ top: y, left: 0, right: 0, height: 1, background: '#206bc4', zIndex: 45 }} />
                ))}
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
                          setRightTab('properties')
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
                {/* 拖拽实时坐标提示 */}
                {dragInfo && (
                  <div className="position-absolute bg-white border rounded px-2 py-1 shadow-sm text-secondary"
                    style={{
                      left: geometry.marginLeftMm * zoom + Math.min(dragInfo.x, geometry.contentWidth - 90) * zoom,
                      top: dragInfo.pageY * zoom - 28,
                      zIndex: 60, fontSize: 10, whiteSpace: 'nowrap',
                    }}>
                    x {dragInfo.x}  y {dragInfo.y}  {dragInfo.w}×{dragInfo.h}
                  </div>
                )}
              </div>
            )}
            <DragOverlay dropAnimation={null}>
              {dragType && <div className="p-2 border bg-white shadow-sm">{ELEMENT_LABELS[dragType]}</div>}
            </DragOverlay>
          </div>
        </DndContext>

        {rightCollapsed ? (
          <div className="border-start bg-white d-flex flex-column align-items-center py-2 gap-1" style={{ width: 38 }}>
            {([['layers', '图层'], ['properties', '属性'], ['history', '历史']] as const).map(([key, label]) => (
              <button key={key} type="button" title={label}
                className="btn btn-sm d-flex align-items-center justify-content-center"
                style={{ width: 30, height: 30 }}
                onClick={() => { setRightTab(key); setRightCollapsed(false) }}>
                {key === 'layers' ? <IconStack2 size={16} /> : key === 'properties' ? <IconSettings size={16} /> : <IconHistory size={16} />}
              </button>
            ))}
          </div>
        ) : (
        <div className="border-start bg-white d-flex flex-column" style={{ width: 310 }}>
          <div className="d-flex border-bottom" role="tablist" aria-label="右侧面板">
            <button
              type="button"
              role="tab"
              aria-selected={rightTab === 'layers'}
              className={`flex-grow-1 border-0 py-2 small ${rightTab === 'layers' ? 'bg-white fw-semibold' : 'bg-secondary-subtle text-secondary'}`}
              onClick={() => setRightTab('layers')}
            >
              图层
            </button>
            <button
              type="button"
              role="tab"
              aria-selected={rightTab === 'properties'}
              className={`flex-grow-1 border-0 py-2 small ${rightTab === 'properties' ? 'bg-white fw-semibold' : 'bg-secondary-subtle text-secondary'}`}
              onClick={() => setRightTab('properties')}
            >
              属性
            </button>
            <button
              type="button"
              role="tab"
              aria-selected={rightTab === 'history'}
              className={`flex-grow-1 border-0 py-2 small ${rightTab === 'history' ? 'bg-white fw-semibold' : 'bg-secondary-subtle text-secondary'}`}
              onClick={() => setRightTab('history')}
            >
              历史
            </button>
            <button
              type="button"
              title="折叠面板"
              className="border-0 bg-transparent px-2 text-secondary"
              onClick={() => setRightCollapsed(true)}
            >
              <IconChevronsRight size={16} />
            </button>
          </div>
          <div className="flex-grow-1 overflow-auto">
            {rightTab === 'layers' ? (
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
                onRename={(id, newId) => {
                  if (doc == null || !/^[A-Za-z0-9_-]{1,64}$/.test(newId)
                    || allElementsOf(doc).some((e) => e.id === newId)) return
                  commit(updateElement(doc, id, { id: newId }), '重命名')
                  setSelectedIds((prev) => {
                    const next = new Set(prev)
                    next.delete(id)
                    next.add(newId)
                    return next
                  })
                }}
                onRemove={(id) => {
                  if (doc == null) return
                  commit(removeElements(doc, new Set([id])), '删除元素')
                  setSelectedIds((prev) => { const next = new Set(prev); next.delete(id); return next })
                }}
              />
            ) : rightTab === 'history' ? (
              <OperationHistoryPanel
                past={history.past}
                future={history.future}
                onJump={jumpToHistory}
                onUndo={undo}
                onRedo={redo}
              />
            ) : (
              selected && definition.data
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
                    }), '微调') : undefined}
                    onChange={(patch) => {
                      if (patch.id && patch.id !== selected.id) {
                        // 重命名：唯一性校验，成功后同步选中态
                        if (!/^[A-Za-z0-9_-]{1,64}$/.test(patch.id)
                          || allElementsOf(doc).some((e) => e.id === patch.id)) return
                        commit(updateElement(doc, selected.id, { id: patch.id }), '重命名')
                        setSelectedIds((prev) => {
                          const next = new Set(prev)
                          next.delete(selected.id)
                          next.add(patch.id!)
                          return next
                        })
                        return
                      }
                      commit(updateElement(doc, selected.id, patch))
                    }}
                    onRemove={() => {
                      commit(removeElements(doc, new Set([selected.id])), '删除元素')
                      setSelectedIds((prev) => { const next = new Set(prev); next.delete(selected.id); return next })
                    }}
                  />
                )
                : <div className="p-4 text-secondary">选择画布中的元素以编辑属性。</div>
            )}
          </div>
        </div>
        )}
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
      {showPreviewDialog && (
        <PreviewDialog
          title={definition.data?.title ?? ''}
          onConfirm={(settings) => {
            setPreviewSettings(settings)
            previewMutation.mutate()
          }}
          onClose={() => setShowPreviewDialog(false)}
        />
      )}
      {showHistory && definition.data && (
        <HistoryModal
          moduleId={moduleId!}
          isCustom={definition.data.isCustom}
          canDesign={canDesign}
          onClose={() => setShowHistory(false)}
        />
      )}
      {showTemplates && (
        <TemplateModal
          currentModuleId={moduleId!}
          canDesign={canDesign}
          onApply={(template) => { applyTemplate(template); setShowTemplates(false) }}
          onClose={() => setShowTemplates(false)}
        />
      )}
      {showPageTemplates && (
        <PageTemplateModal
          doc={doc}
          onApply={(templates) => {
            if (doc == null) return
            commit({ ...doc, pageTemplates: templates }, '页模板')
          }}
          onClose={() => setShowPageTemplates(false)}
        />
      )}
      {contextMenu && (
        <ContextMenu
          x={contextMenu.x}
          y={contextMenu.y}
          onClose={() => setContextMenu(null)}
          items={
            contextMenu.elementId
              ? [
                { label: '复制', onClick: () => {
                  const el = allElementsOf(doc).find((e) => e.id === contextMenu.elementId)
                  if (el) setClipboard([{ ...el, id: newElementId(el.type) }])
                } },
                { label: '复制样式', onClick: () => {
                  const el = allElementsOf(doc).find((e) => e.id === contextMenu.elementId)
                  if (el) setStyleClipboard(el.style ?? {})
                } },
                { label: '粘贴', onClick: () => {
                  if (clipboard.length > 0 && doc != null) {
                    let next = doc
                    for (const el of clipboard) next = addElementTo(next, { ...el, x: el.x + 3, y: el.y + 3 })
                    commit(next, '粘贴')
                  }
                }, disabled: clipboard.length === 0 },
                { label: '粘贴样式', onClick: () => {
                  const el = allElementsOf(doc).find((e) => e.id === contextMenu.elementId)
                  if (styleClipboard && el) commit(updateElement(doc, el.id, { style: styleClipboard }), '粘贴样式')
                }, disabled: styleClipboard == null },
                { separator: true },
                { label: '删除', danger: true, onClick: () => {
                  commit(removeElements(doc, new Set([contextMenu.elementId!])), '删除元素')
                  setSelectedIds((prev) => { const next = new Set(prev); next.delete(contextMenu.elementId!); return next })
                } },
                { separator: true },
                { label: '置顶', onClick: () => moveLayerToEnd(contextMenu.elementId!, true) },
                { label: '上一层', onClick: () => reorderLayer(contextMenu.elementId!, 1) },
                { label: '下一层', onClick: () => reorderLayer(contextMenu.elementId!, -1) },
                { label: '置底', onClick: () => moveLayerToEnd(contextMenu.elementId!, false) },
                { separator: true },
                { label: '属性', onClick: () => setSelectedIds(new Set([contextMenu.elementId!])) },
              ]
              : [
                { label: '粘贴', onClick: () => {
                  if (clipboard.length > 0 && doc != null) {
                    let next = doc
                    for (const el of clipboard) next = addElementTo(next, { ...el, x: el.x + 3, y: el.y + 3 })
                    commit(next, '粘贴')
                  }
                }, disabled: clipboard.length === 0 },
              ]
          }
        />
      )}
      </div>
    </div>
  )
}
