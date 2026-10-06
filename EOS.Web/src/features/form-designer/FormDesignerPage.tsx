import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  DndContext,
  DragOverlay,
  PointerSensor,
  closestCenter,
  useSensor,
  useSensors,
  type DragEndEvent,
  type DragMoveEvent,
  type DragStartEvent,
} from '@dnd-kit/core'
import {
  IconArrowBackUp,
  IconArrowForwardUp,
  IconArrowLeft,
  IconCopyPlus,
  IconFileExport,
  IconFileUpload,
  IconLayoutRows,
  IconRefresh,
  IconRestore,
} from '@tabler/icons-react'
import { useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../types/api'
import { apiClient } from '../../services/api'
import { createId } from '../../lib/uuid'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { ErpCommandBar, type ErpCommandItem } from '../../components/common/ErpCommandBar'
import { ErpColumnSelector, type ColumnSelectorGroup } from '../../components/common/ErpColumnSelector'
import { UnifiedChooser, type UnifiedChooserRow } from '../../components/common/UnifiedChooser'
import { useMenuPlacement } from '../../components/common/useMenuPlacement'
import { applyDrop, parseDragId, zoneOf, type DragSource, type DropTarget } from './formDesignerDrag'
import DesignCanvas from './DesignCanvas'
import DetailColumnPanel from './DetailColumnPanel'
import { useDesignerHistory } from './useDesignerHistory'
import {
  DEFAULT_DIALOG_HEIGHT,
  DEFAULT_DIALOG_WIDTH,
  normalizeFormOpenMode,
  resolveDialogSize,
} from '../document-workbench/formOpenMode'
import {
  addFromPool,
  addTab,
  applyDetailColumns,
  applyTemplateDraft,
  deleteTab,
  exportDraftFile,
  importDraftFile,
  mergeCompanion,
  moveRow,
  moveRowToTab,
  removeSection,
  renameSection,
  renameTab,
  RESIDENT_TAB_NO,
  resetRow,
  setHidden,
  setDialogSize,
  setOpenMode,
  setPlacement,
  setSection,
  setTabColumns,
  tabColumns,
  tabTitle,
  toDraft,
  toSavePayload,
  validateDraft,
} from './formDesignerDraft'
import type { FormLayoutTemplate } from './types'
import type { DesignDraft, DesignState, DesignTable, SaveResponse } from './types'
import './form-designer.css'

/** 数字输入框 → 可空整数：留空即 null（"按默认开窗"），非数字也按空处理，不把 NaN 写进草稿。 */
function toOptionalInt(value: string): number | null {
  const trimmed = value.trim()
  if (trimmed === '') return null
  const parsed = Number(trimmed)
  return Number.isFinite(parsed) ? Math.round(parsed) : null
}

interface FormDesignerPageProps {
  moduleId: number
  /** 退出设计态（回到表单页）。 */
  onExit: () => void
}

/** 拖拽几何：move 与 end 事件都带这三个字段，落点判定只依赖它们。 */
type DragGeometry = Pick<DragMoveEvent, 'over' | 'activatorEvent' | 'delta'>

/** 拖拽开始时的一格几何（视口坐标）：落点判定的静态参照。 */
interface CellGeometry {
  key: string
  left: number
  right: number
  top: number
  bottom: number
}

/** 右键精修菜单：作用对象是一格（主表画布或明细表头）、一个分节标题或一个页签。 */
type DesignerMenu =
  | { kind: 'cell'; table: DesignTable; key: string; x: number; y: number }
  | { kind: 'section'; sectionId: string; x: number; y: number }
  | { kind: 'tab'; no: number; x: number; y: number }

/** 选择器返回的字段行（服务端列键 F_ID / F_DESC / F_TYPE）。 */
type PickedFieldRow = UnifiedChooserRow & { F_ID?: unknown }

/**
 * 表单设计态：右键【表单设计】进入，与运行态**同一套渲染**，但输入控件不可填、
 * 值用字段代号占位。保存即生效（服务端同请求内重发布该模块快照），无需另行发布。
 *
 * 只改版式：顺序、占位、复合格、分节、页签、移出表单；字段自身的属性在字段维护里改。
 *
 * 页面只有一条工具条与一整块画布：没有左侧字段池、也没有右侧属性面板——
 * 加字段走画布末尾的「+」（统一选择器），改版式走右键精修，画布得以横向铺满。
 */
export default function FormDesignerPage({ moduleId, onExit }: FormDesignerPageProps) {
  const queryClient = useQueryClient()
  const [state, setState] = useState<DesignState | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const history = useDesignerHistory<DesignDraft | null>(null)
  const draft = history.value
  const [activeTabNo, setActiveTabNo] = useState(1)
  const [selected, setSelected] = useState<{ table: DesignTable; key: string } | null>(null)
  /** 主表「+ 添加字段」的选择器是否打开（明细的选列与排序走「字段管理」，不用这个）。 */
  const [pickerOpen, setPickerOpen] = useState(false)
  /** 明细「字段管理」弹窗：选列 + 上下排序（明细拖拽排序的替代入口）。 */
  const [fieldManagerOpen, setFieldManagerOpen] = useState(false)
  const [compact, setCompact] = useState(true)
  const [busy, setBusy] = useState(false)
  const [issues, setIssues] = useState<string[]>([])
  const [status, setStatus] = useState<{ tone: 'ok' | 'warn' | 'error'; text: string } | null>(null)
  const [menu, setMenu] = useState<DesignerMenu | null>(null)
  /** 「表单呈现」配置弹窗（入口在页签行最右端）。 */
  const [presentationOpen, setPresentationOpen] = useState(false)
  const [templates, setTemplates] = useState<FormLayoutTemplate[] | null>(null)
  const [dragging, setDragging] = useState<DragSource | null>(null)
  const [dropTarget, setDropTarget] = useState<DropTarget>(null)
  /** 拖拽预览：把字段先按落点重排一份草稿渲染，后方字段随之让位，松手前就能看出结果。 */
  const [previewDraft, setPreviewDraft] = useState<DesignDraft | null>(null)
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 4 } }))
  /** 拖拽源：算预览时每帧都要读，用 ref 避免闭包读到过期的 state。 */
  const dragRef = useRef<DragSource | null>(null)
  /** 拖拽开始时的格几何快照与滚动位（落点判定的静态参照）。 */
  const cellsRef = useRef<CellGeometry[]>([])
  const scrollRef = useRef<{ top: number; left: number; el: HTMLElement | null }>({ top: 0, left: 0, el: null })

  const draggedLabel = useMemo(() => {
    if (!draft || !dragging) return ''
    const rows = dragging.table === 'master' ? draft.master : draft.detail
    const row = rows.find(item => item.key === dragging.key)
    if (row) return row.label
    const pool = dragging.table === 'master' ? draft.masterPool : draft.detailPool
    return pool.find(field => field.key === dragging.key)?.label ?? dragging.key
  }, [draft, dragging])

  /**
   * 主表选择器候选按**当前草稿**算：草稿里显示中的行就是"已在表单里"，把它交给服务端排除。
   * 不能按库里的版式行算——草稿里的移出/加入在保存前只存在于前端，按库算会把刚移出表单的字段
   * 当成仍在表单里而排除掉（恰好是用户想选回来的那一个），于是选择器里空无一物、放不回去。
   */
  const placedKeysForPicker = useMemo(() => {
    if (!pickerOpen || !draft) return ''
    return draft.master.filter(row => !row.hidden).map(row => row.key).join(',')
  }, [pickerOpen, draft])

  const closeMenu = () => setMenu(null)
  const runMenu = (action: () => void) => {
    action()
    closeMenu()
  }
  const menuRow = menu?.kind === 'cell'
    ? (menu.table === 'master' ? draft?.master : draft?.detail)?.find(row => row.key === menu.key) ?? null
    : null
  /** 复合格只由主表承担：同桌的"上一个可合并字段"（与当前字段合成 [主][选择][从] 三件套）。 */
  const menuPrev = menu?.kind === 'cell' && menu.table === 'master' && menuRow
    ? (() => {
        const mergeable = draft?.master.filter(row => row.cellRole !== 2) ?? []
        return mergeable.at(mergeable.findIndex(row => row.key === menuRow.key) - 1) ?? null
      })()
    : null

  /** 右键菜单定位：字段贴近屏幕下缘时自动翻到落点上方，不被窗口裁掉。 */
  const menuPlacement = useMenuPlacement(menu?.x ?? 0, menu?.y ?? 0, menu !== null)

  /** 明细「字段管理」候选：当前明细行（含已移出）+ 字段池，去重后按原次序给出。 */
  const detailColumnGroups = useMemo<ColumnSelectorGroup[]>(() => {
    if (!draft) return []
    const fields: { key: string; label: string }[] = []
    const seen = new Set<string>()
    for (const item of [...draft.detail, ...draft.detailPool]) {
      if (seen.has(item.key)) continue
      seen.add(item.key)
      fields.push({ key: item.key, label: item.label })
    }
    return [{
      id: 'detail',
      // 单组且对话框标题已写明对象：组标题留空，两侧列表标签回落成「可选字段 / 已选字段」
      label: '',
      fields,
      // 已选 = 未移出的明细行（顺序即版式顺序）；默认 = 加载时未移出的明细行
      visibleKeys: draft.detail.filter(row => !row.hidden).map(row => row.key),
      defaultKeys: draft.baseline.detail.filter(row => !row.hidden).map(row => row.key),
    }]
  }, [draft])

  /** 字段管理落地：按清单重建明细版式（移出/放回 + 排序一并生效，保存后才写库）。 */
  const applyDetailColumnSelection = (selection: Record<string, string[]>) => {
    if (!draft) return
    apply(applyDetailColumns(draft, selection.detail ?? []))
    setFieldManagerOpen(false)
    setStatus({ tone: 'ok', text: '明细列已更新，保存后生效。' })
  }

  /** 套用来源：只列共用同一主表的模块（跨主表套用会排出业务上不该出现的字段）。 */
  const loadTemplates = async () => {
    if (!draft) return
    try {
      const result = await apiClient.get<FormLayoutTemplate[]>(`/admin/form-layout/${draft.moduleId}/templates`)
      setTemplates(result ?? [])
      if ((result ?? []).length === 0) {
        setStatus({ tone: 'warn', text: '没有其它模块与本模块共用同一主表，暂无可套用来源。' })
      }
    } catch (error) {
      setStatus({ tone: 'error', text: String(error) })
    }
  }

  const exportFile = () => {
    if (!draft) return
    const blob = new Blob([exportDraftFile(draft)], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = `form-layout-${draft.moduleId}.json`
    link.click()
    URL.revokeObjectURL(url)
  }

  const importFile = async (file: File) => {
    if (!draft) return
    const result = importDraftFile(draft, await file.text())
    if ('error' in result) {
      setStatus({ tone: 'error', text: result.error })
      return
    }
    apply(result.draft)
    setStatus({ tone: 'ok', text: '版式文件已载入画布，确认后再点保存。' })
  }

  /** 记录画布格的静态几何：拖拽期间的落点按它判定，预览造成的位移不参与（否则落点会在两格间来回跳）。 */
  const snapshotCells = () => {
    cellsRef.current = [...document.querySelectorAll<HTMLElement>('[data-designer-cell]')]
      .map(node => {
        const rect = node.getBoundingClientRect()
        return {
          key: node.dataset.designerCell ?? '',
          left: rect.left,
          right: rect.right,
          top: rect.top,
          bottom: rect.bottom,
        }
      })
      .filter(cell => cell.key.length > 0)
    const scroller = document.querySelector<HTMLElement>('.erp-designer-main')
    scrollRef.current = { top: scroller?.scrollTop ?? 0, left: scroller?.scrollLeft ?? 0, el: scroller }
  }

  /** 拖拽期间的滚动补偿：快照记的是按下那一刻的视口坐标。 */
  const scrollOffset = () => {
    const scroller = scrollRef.current.el
    if (!scroller) return { left: 0, top: 0 }
    return { left: scroller.scrollLeft - scrollRef.current.left, top: scroller.scrollTop - scrollRef.current.top }
  }

  const handleDragStart = (event: DragStartEvent) => {
    const source = parseDragId(String(event.active.id))
    dragRef.current = source
    snapshotCells()
    setDragging(source)
  }

  /** 一格内的落点分区：明细是网格不是格版式，中心区不判合并（否则会给出终将被拒绝的落点提示）。 */
  const zoneOfCell = (offsetX: number, width: number) =>
    dragRef.current?.table === 'detail'
      ? (width > 0 && offsetX / width >= 0.5 ? 'after' : 'before')
      : zoneOf(offsetX, width)

  /** 指针落在格的哪一段 → 落点：只算几何，语义全部交给 applyDrop。 */
  const resolveTarget = (event: DragGeometry): DropTarget => {
    const over = event.over
    const data = over?.data.current as { kind?: string; key?: string; tabNo?: number; sectionId?: string | null } | undefined
    const pointerX = (event.activatorEvent as PointerEvent).clientX + event.delta.x
    const pointerY = (event.activatorEvent as PointerEvent).clientY + event.delta.y
    const offset = scrollOffset()
    const px = pointerX - offset.left
    const py = pointerY - offset.top
    if (cellsRef.current.length > 0) {
      const hit = cellsRef.current.find(cell => px >= cell.left && px <= cell.right && py >= cell.top && py <= cell.bottom)
      if (hit) {
        const zone = zoneOfCell(px - hit.left, hit.right - hit.left)
        return zone === 'merge' ? { kind: 'merge', key: hit.key } : { kind: 'insert', key: hit.key, before: zone === 'before' }
      }
    } else if (over && data?.kind === 'cell' && data.key) {
      // 快照缺失时的兜底：退回按 over 的实时矩形判定
      const zone = zoneOfCell(pointerX - over.rect.left, over.rect.width)
      return zone === 'merge' ? { kind: 'merge', key: data.key } : { kind: 'insert', key: data.key, before: zone === 'before' }
    }
    if (!over) return null
    if (data?.kind === 'tab' && typeof data.tabNo === 'number') return { kind: 'tab', tabNo: data.tabNo }
    if (data?.kind === 'section') return { kind: 'section', sectionId: data.sectionId ?? null }
    return null
  }

  const handleDragMove = (event: DragMoveEvent) => {
    const target = resolveTarget(event)
    setDropTarget(target)
    const source = dragRef.current
    if (!source || !draft || target?.kind !== 'insert') {
      setPreviewDraft(null)
      return
    }
    const result = applyDrop(draft, source, target)
    setPreviewDraft('rejected' in result ? null : result.draft)
  }

  const handleDragEnd = (event: DragEndEvent) => {
    const source = parseDragId(String(event.active.id))
    // 松手时的 over 才是最终落点；拖到空白处（over 为空）视为没有落点，不生效
    const target = event.over ? resolveTarget(event) : null
    dragRef.current = null
    setDragging(null)
    setDropTarget(null)
    setPreviewDraft(null)
    if (!source || !target || !draft) return
    const result = applyDrop(draft, source, target)
    if ('rejected' in result) {
      setStatus({ tone: 'warn', text: result.rejected })
      return
    }
    apply(result.draft)
    setSelected({ table: source.table, key: source.key })
  }

  const load = useCallback(async () => {
    setLoadError(null)
    try {
      const next = await apiClient.get<DesignState>(`/admin/form-layout/${moduleId}`)
      setState(next)
      history.rebase(toDraft(next))
      setActiveTabNo(1)
      setSelected(null)
      setIssues([])
    } catch (error) {
      setLoadError(error instanceof ApiError ? error.message : '加载版式失败。')
    }
    // history.rebase 是稳定引用；此处只在换模块时重新加载
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [moduleId])

  useEffect(() => {
    void load()
  }, [load])

  useEffect(() => {
    if (!history.dirty) return
    const handler = (event: BeforeUnloadEvent) => {
      event.preventDefault()
      event.returnValue = ''
    }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [history.dirty])

  useEffect(() => {
    const handler = (event: KeyboardEvent) => {
      if (!event.ctrlKey && !event.metaKey) return
      const key = event.key.toLowerCase()
      if (key === 'z') {
        event.preventDefault()
        if (event.shiftKey) history.redo()
        else history.undo()
      } else if (key === 'y') {
        event.preventDefault()
        history.redo()
      }
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [history])

  const apply = useCallback(
    (next: DesignDraft | null) => {
      if (!next || next === draft) return
      history.commit(next)
      setIssues(validateDraft(next))
    },
    [draft, history],
  )

  const save = useCallback(async () => {
    if (!draft || !state) return
    setBusy(true)
    setStatus(null)
    try {
      const payload = toSavePayload(draft, state.baseUpdatedAt, createId())
      const response = await apiClient.put<SaveResponse>(`/admin/form-layout/${moduleId}`, payload)
      if (response.state) {
        setState(response.state)
        history.rebase(toDraft(response.state))
        setIssues([])
      }
      // 保存即生效：工作区标签常驻不卸载，不主动失效缓存的话，切回统一表单看到的仍是旧版式
      await queryClient.invalidateQueries({ queryKey: ['workbench', String(moduleId)] })
      setStatus({
        tone: 'ok',
        text: `${response.message ?? '已保存'}${response.definitionVersion ? `（${response.definitionVersion}）` : ''}`,
      })
    } catch (error) {
      // 保存失败保留用户编辑内容：不清空、不退出版式，只说明原因（服务端是最终判据）
      setStatus({
        tone: error instanceof ApiError && error.status === 409 ? 'warn' : 'error',
        text: error instanceof ApiError ? error.message : '保存失败。',
      })
    } finally {
      setBusy(false)
    }
  }, [draft, state, moduleId, history, queryClient])

  const reset = useCallback(async () => {
    if (!draft || !state) return
    if (!window.confirm('重置为默认版式？本模块当前的版式定制会被清除（字段定义不变）。')) return
    setBusy(true)
    setStatus(null)
    try {
      const response = await apiClient.post<SaveResponse>(`/admin/form-layout/${moduleId}/reset`, {
        baseUpdatedAt: state.baseUpdatedAt,
        idempotencyKey: createId(),
      })
      if (response.state) {
        setState(response.state)
        history.rebase(toDraft(response.state))
        setIssues([])
      }
      await queryClient.invalidateQueries({ queryKey: ['workbench', String(moduleId)] })
      setStatus({ tone: 'ok', text: response.message ?? '已重置为默认版式。' })
    } catch (error) {
      setStatus({ tone: 'error', text: error instanceof ApiError ? error.message : '重置失败。' })
    } finally {
      setBusy(false)
    }
  }, [draft, state, moduleId, history, queryClient])

  /** 离开设计态：有未保存改动时二次确认（与浏览器关闭/刷新的保护一致）。 */
  const exit = useCallback(() => {
    if (history.dirty && !window.confirm('有未保存的修改，确定离开表单设计？')) return
    onExit()
  }, [history.dirty, onExit])

  const discard = useCallback(() => {
    if (!state) return
    if (history.dirty && !window.confirm('放弃未保存的修改？')) return
    history.rebase(toDraft(state))
    setIssues([])
    setStatus(null)
  }, [state, history])

  /**
   * 选择器确认：把选中的字段追加到该表末尾。
   * 已经在表单里的（含本次草稿刚加的）跳过并说明，避免重复点击后静默无反应；
   * 已移出表单的字段仍留在字段池里（供选择器列出），选中它 = 原位放回表单。
   */
  const addPickedFields = (table: DesignTable, rows: PickedFieldRow[]) => {
    if (!draft) return
    const pool = table === 'master' ? draft.masterPool : draft.detailPool
    let next = draft
    let added = 0
    const skipped: string[] = []
    for (const row of rows) {
      const key = String(row.F_ID ?? '').trim()
      if (key.length === 0) continue
      const placed = (table === 'master' ? next.master : next.detail).find(item => item.key === key)
      if (placed) {
        if (!placed.hidden) {
          skipped.push(key)
          continue
        }
        next = setHidden(next, table, key, false)
        added += 1
        continue
      }
      if (!pool.some(field => field.key === key)) {
        skipped.push(key)
        continue
      }
      next = addFromPool(next, table, key)
      added += 1
    }
    if (added > 0) {
      apply(next)
      // 新字段落在常驻的 1 号页签上，切过去才能看见刚加的东西
      if (table === 'master') setActiveTabNo(1)
    }
    setStatus(
      added === 0
        ? { tone: 'warn', text: '所选字段都已经在表单里了。' }
        : skipped.length > 0
          ? { tone: 'warn', text: `已加入 ${added} 个字段；${skipped.join('、')} 已在表单里，未重复加入。` }
          : { tone: 'ok', text: `已加入 ${added} 个字段，保存后生效。` },
    )
    setPickerOpen(false)
  }

  const moveBy = (table: DesignTable, key: string, delta: number) => apply(moveRow(draft!, table, key, delta))

  const setRowHidden = (table: DesignTable, key: string, hidden: boolean) => apply(setHidden(draft!, table, key, hidden))

  const toggleNewLine = (key: string) => {
    const row = draft?.master.find(item => item.key === key)
    if (!row) return
    apply(setPlacement(draft!, key, { newLine: !row.newLine }))
  }

  const moveToTab = (key: string, tabNo: number) => apply(moveRowToTab(draft!, key, tabNo))

  /**
   * 删除页签：其中的字段回到常驻页签（不丢字段），删除前确认。
   * 1 号页签不可删——版式校验要求常驻页签必须在（服务端同口径，前端先挡一道）。
   */
  const removeTab = (no: number) => {
    if (!draft || no === RESIDENT_TAB_NO) return
    const label = tabTitle(draft.tabs.find(tab => tab.no === no) ?? { no, title: '', columns: null })
    if (!window.confirm(`删除页签「${label}」？`)) return
    apply(deleteTab(draft, no))
    if (activeTabNo === no) setActiveTabNo(RESIDENT_TAB_NO)
  }

  const mergeWith = (mainKey: string, companionKey: string | null) => {
    if (!draft) return
    const result = mergeCompanion(draft, mainKey, companionKey)
    if (result.rejected) {
      setStatus({ tone: 'warn', text: result.rejected })
      return
    }
    apply(result.draft)
  }

  /** 移出复合格：整组一起拆——只拆当前字段会让同组另一半失去主字段（从表单上消失）。 */
  const detachFromCell = (key: string) => {
    if (!draft) return
    const group = draft.master.find(row => row.key === key)?.cellGroup ?? null
    apply({
      ...draft,
      master: draft.master.map(row =>
        row.key === key || (group !== null && row.cellGroup === group)
          ? { ...row, cellGroup: null, cellRole: 0 }
          : row,
      ),
    })
  }

  if (loadError) {
    return (
      <div className="erp-designer">
        <div className="erp-designer-error">{loadError}</div>
        <div className="erp-designer-toolbar">
          <Button size="sm" className="erp-command-btn" icon={<IconRefresh size={16} />} onClick={() => void load()}>
            重试
          </Button>
          <Button size="sm" className="erp-command-btn" icon={<IconArrowLeft size={16} />} onClick={exit}>
            返回表单
          </Button>
        </div>
      </div>
    )
  }

  if (!draft || !state) {
    return <div className="erp-designer erp-designer-loading">正在加载版式…</div>
  }

  // 拖拽预览版式：插入落点先按结果重排渲染，后方字段随之让位（松手前就能看出落点结果）
  const canvasDraft = previewDraft ?? draft
  // 打开方式与窗体尺寸：与运行态同一处解析（弹窗才谈得上尺寸，其余方式画板铺满）
  const isDialogDraft = normalizeFormOpenMode(draft.openMode) === 'DIALOG'
  const dialogSize = resolveDialogSize(draft.dialogWidth, draft.dialogHeight)
  // 页签行右端那颗按钮上的摘要：不点开也能看出"这张表单怎么开、窗体多大"
  const presentationSummary = isDialogDraft
    ? `弹窗 ${dialogSize.width}×${dialogSize.height}`
    : normalizeFormOpenMode(draft.openMode) === 'NEWTAB' ? '新页签' : '本页签'

  return (
    <div className="erp-designer">
      <div className="erp-designer-toolbar">
        {/* 返回是工具条首位（浏览类页面的统一位置）：更靠前于破坏性动作，避免误触保存/重置 */}
        <ErpCommandBar
          className="erp-designer-commands"
          ariaLabel="表单设计命令栏"
          items={[
            { action: 'back', label: '返回表单', icon: <IconArrowLeft size={16} />, variant: 'ghost', onClick: exit },
            { action: 'save', label: '保存', variant: 'primary', disabled: busy || !history.dirty, loading: busy, onClick: () => void save() },
            { action: 'cancel', label: '放弃', disabled: busy || !history.dirty, onClick: discard },
            { action: 'reset-layout', label: '重置', icon: <IconRestore size={16} />, title: '重置为默认版式（清除本模块的版式定制）', disabled: busy, onClick: () => void reset() },
            { action: 'undo', label: '撤销', icon: <IconArrowBackUp size={16} />, title: '撤销（Ctrl+Z）', disabled: !history.canUndo, onClick: history.undo },
            { action: 'redo', label: '重做', icon: <IconArrowForwardUp size={16} />, title: '重做（Ctrl+Y）', disabled: !history.canRedo, onClick: history.redo },
            { action: 'compact', label: '紧凑', icon: <IconLayoutRows size={16} />, title: '紧凑：允许后续字段回填空洞', variant: compact ? 'primary' : 'secondary', onClick: () => setCompact(value => !value) },
          ] satisfies ErpCommandItem[]}
        />
        <span className="erp-designer-toolbar-divider" />
        <ErpCommandBar
          className="erp-designer-commands"
          ariaLabel="版式文件命令栏"
          items={[
            { action: 'apply-template', label: '套用来源…', icon: <IconCopyPlus size={16} />, title: '套用共用同一主表的其它模块的版式', disabled: busy, onClick: () => void loadTemplates() },
            {
              action: 'import-layout',
              render: () => (
                <label className="btn btn-outline-secondary btn-sm erp-command-btn" title="把导出的版式文件载入画布">
                  <IconFileUpload size={16} />
                  导入
                  <input
                    type="file"
                    accept="application/json"
                    hidden
                    onChange={event => {
                      const file = event.target.files?.[0]
                      if (file) void importFile(file)
                      event.target.value = ''
                    }}
                  />
                </label>
              ),
            },
            { action: 'export-layout', label: '导出', icon: <IconFileExport size={16} />, title: '把当前版式导出为文件', disabled: busy, onClick: exportFile },
          ] satisfies ErpCommandItem[]}
        />
      </div>

      {status ? <div className={`erp-designer-status is-${status.tone}`}>{status.text}</div> : null}
      {templates ? (
        <div className="erp-designer-templates">
          {templates.length === 0 ? (
            <span className="erp-designer-muted">没有共用同一主表的其它模块。</span>
          ) : (
            templates.map(item => (
              <Button
                key={item.moduleId}
                size="sm"
                className="erp-command-btn"
                title={`把模块 ${item.moduleId} 的版式套到本模块（字段按本模块裁剪）`}
                onClick={() => {
                  void (async () => {
                    try {
                      const source = await apiClient.get<DesignState>(`/admin/form-layout/${item.moduleId}`)
                      apply(applyTemplateDraft(draft, source))
                      setStatus({ tone: 'ok', text: `已套用模块 ${item.moduleId} 的版式，确认后再点保存。` })
                    } catch (error) {
                      setStatus({ tone: 'error', text: String(error) })
                    }
                    setTemplates(null)
                  })()
                }}
              >
                {item.moduleId} {item.title}
              </Button>
            ))
          )}
          <Button size="sm" className="erp-command-btn" onClick={() => setTemplates(null)}>
            取消
          </Button>
        </div>
      ) : null}
      {issues.length > 0 ? (
        <div className="erp-designer-status is-warn">
          保存前请先处理：{issues.slice(0, 3).join(' ')}
          {issues.length > 3 ? ` 等 ${issues.length} 处` : ''}
        </div>
      ) : null}

      {/* 表单呈现配置：入口在页签行最右端（原本的空白处），弹窗里改打开方式与窗体尺寸。
          它随版式同一笔保存、同一次重发布 ⇒ **保存即生效**——不像模块管理里的元数据草稿
          那样要人工点发布（见 docs/guide/42 §三）。画板尺寸随之切换，所见即所得。
          栅格列数**不在这里**：它是页签级事实，走页签右键「布局列数」（各页签可以不同）。 */}
      {presentationOpen ? (
        <Modal
          title="表单呈现"
          ariaLabel="表单呈现"
          onClose={() => setPresentationOpen(false)}
          footer={
            <Button size="sm" onClick={() => setPresentationOpen(false)}>
              完成
            </Button>
          }
        >
          <div className="erp-designer-present-fields">
            <label className="erp-designer-field">
              <span>打开方式</span>
              <select
                className="form-select form-select-sm"
                aria-label="打开方式"
                disabled={busy}
                value={normalizeFormOpenMode(draft.openMode)}
                onChange={event => apply(setOpenMode(draft, event.target.value))}
              >
                <option value="TAB">本页签（默认）</option>
                <option value="NEWTAB">新页签</option>
                <option value="DIALOG">弹窗</option>
              </select>
            </label>
            <label className="erp-designer-field">
              <span>弹窗宽度</span>
              <input
                type="number"
                className="form-control form-control-sm"
                aria-label="弹窗宽度（px）"
                placeholder={String(DEFAULT_DIALOG_WIDTH)}
                disabled={busy || !isDialogDraft}
                value={draft.dialogWidth == null ? '' : String(draft.dialogWidth)}
                onChange={event => apply(setDialogSize(draft, { width: toOptionalInt(event.target.value), height: undefined }))}
              />
            </label>
            <label className="erp-designer-field">
              <span>弹窗高度</span>
              <input
                type="number"
                className="form-control form-control-sm"
                aria-label="弹窗高度（px）"
                placeholder={String(DEFAULT_DIALOG_HEIGHT)}
                disabled={busy || !isDialogDraft}
                value={draft.dialogHeight == null ? '' : String(draft.dialogHeight)}
                onChange={event => apply(setDialogSize(draft, { width: undefined, height: toOptionalInt(event.target.value) }))}
              />
            </label>
            <p className="erp-designer-muted mb-0">
              {isDialogDraft
                ? `画板按弹窗 ${dialogSize.width}×${dialogSize.height} 排布（留空即默认 ${DEFAULT_DIALOG_WIDTH}×${DEFAULT_DIALOG_HEIGHT}）`
                : '本页签 / 新页签下表单占满可用区域，画板同样铺满'}
              ｜ 保存即生效（随定义快照一并重发布）
              ｜ 一行几列按页签定：页签上右键「布局列数」
            </p>
          </div>
        </Modal>
      ) : null}

      <DndContext
        sensors={sensors}
        collisionDetection={closestCenter}
        onDragStart={handleDragStart}
        onDragMove={handleDragMove}
        onDragEnd={handleDragEnd}
        onDragCancel={() => {
          dragRef.current = null
          setDragging(null)
          setDropTarget(null)
          setPreviewDraft(null)
        }}
      >
        <div className="erp-designer-body">
          <div className="erp-designer-main">
            <DesignCanvas
              draft={canvasDraft}
              activeTabNo={activeTabNo}
              onActiveTabChange={setActiveTabNo}
              selectedKey={selected?.table === 'master' ? selected.key : null}
              onSelect={key => setSelected(key ? { table: 'master', key } : null)}
              compact={compact}
              draggingKey={dragging?.key ?? null}
              ghostKey={previewDraft && dragging?.table === 'master' ? dragging.key : null}
              dropTarget={dropTarget}
              onAddField={() => setPickerOpen(true)}
              onRowContextMenu={(key, x, y) => setMenu({ kind: 'cell', table: 'master', key, x, y })}
              onSectionContextMenu={(sectionId, x, y) => setMenu({ kind: 'section', sectionId, x, y })}
              onRenameTab={(no, title) => apply(renameTab(draft, no, title))}
              // 新页签沿用**当前页签**的列数：在一个两列的表单里加页签，得到的是两列页签
              onAddTab={() => apply(addTab(draft, `页签 ${draft.tabs.length + 1}`, tabColumns(draft, activeTabNo)))}
              onDeleteTab={removeTab}
              onOpenPresentation={() => setPresentationOpen(true)}
              presentationSummary={presentationSummary}
              onTabContextMenu={(no, x, y) => setMenu({ kind: 'tab', no, x, y })}
            />

            {state.detailTable ? (
              <DetailColumnPanel
                table={state.detailTable}
                rows={canvasDraft.detail}
                selectedKey={selected?.table === 'detail' ? selected.key : null}
                draggingKey={dragging?.table === 'detail' ? dragging.key : null}
                dropTarget={dropTarget}
                onSelect={key => setSelected({ table: 'detail', key })}
                onManageFields={() => setFieldManagerOpen(true)}
                onRowContextMenu={(key, x, y) => setMenu({ kind: 'cell', table: 'detail', key, x, y })}
              />
            ) : null}
          </div>
        </div>
        {/* 右键精修：纯拖拽对精细操作不友好，右键给全量动作（与「+」选择器同一批草稿操作） */}
        {menu ? (
          <div
            ref={menuPlacement.ref}
            className="erp-designer-menu"
            style={{ left: menuPlacement.left, top: menuPlacement.top }}
            onMouseLeave={closeMenu}
            onClick={event => event.stopPropagation()}
          >
            {menu.kind === 'cell' && menuRow ? (
              <>
                <button type="button" disabled={menuRow.orderNo <= 1} onClick={() => runMenu(() => moveBy(menu.table, menuRow.key, -1))}>
                  前移
                </button>
                <button type="button" onClick={() => runMenu(() => moveBy(menu.table, menuRow.key, 1))}>
                  后移
                </button>
                {menu.table === 'master' ? (
                  <>
                    <button type="button" onClick={() => runMenu(() => toggleNewLine(menuRow.key))}>
                      {menuRow.newLine ? '取消另起一行' : '另起一行'}
                    </button>
                    <div className="erp-designer-menu-label">宽度</div>
                    <div className="erp-designer-menu-row">
                      {/* 可选宽度 = **该行所属页签**的栅格列数（页签级事实），不写死 1..4 */}
                      {Array.from({ length: tabColumns(draft, menuRow.tabNo) }, (_, index) => index + 1).map(span => (
                        <button
                          key={span}
                          type="button"
                          className={menuRow.span === span ? 'is-active' : ''}
                          onClick={() => runMenu(() => apply(setPlacement(draft, menuRow.key, { span })))}
                        >
                          {span} 段
                        </button>
                      ))}
                    </div>
                    <div className="erp-designer-menu-label">行高</div>
                    <div className="erp-designer-menu-row">
                      {[1, 2, 3].map(rowSpan => (
                        <button
                          key={rowSpan}
                          type="button"
                          className={menuRow.rowSpan === rowSpan ? 'is-active' : ''}
                          onClick={() => runMenu(() => apply(setPlacement(draft, menuRow.key, { rowSpan })))}
                        >
                          {rowSpan} 行
                        </button>
                      ))}
                    </div>
                    {draft.tabs.length > 1 ? (
                      <>
                        <div className="erp-designer-menu-label">移动到页签</div>
                        <div className="erp-designer-menu-row is-wrap">
                          {draft.tabs.map(tab => (
                            <button
                              key={tab.no}
                              type="button"
                              className={menuRow.tabNo === tab.no ? 'is-active' : ''}
                              onClick={() => runMenu(() => moveToTab(menuRow.key, tab.no))}
                            >
                              {tabTitle(tab)}
                            </button>
                          ))}
                        </div>
                      </>
                    ) : null}
                    {menuRow.cellRole === 0 && menuPrev?.hasChooser ? (
                      <button type="button" onClick={() => runMenu(() => mergeWith(menuPrev.key, menuRow.key))}>
                        与「{menuPrev.label}」合并为一格
                      </button>
                    ) : null}
                    {menuRow.cellRole !== 0 ? (
                      <button type="button" onClick={() => runMenu(() => detachFromCell(menuRow.key))}>
                        移出复合格
                      </button>
                    ) : null}
                    <button
                      type="button"
                      onClick={() => {
                        const name = window.prompt('分节名称（留空 = 无分节）', menuRow.sectionId ?? '')
                        if (name !== null) runMenu(() => apply(setSection(draft, menuRow.key, name.trim() || null)))
                      }}
                    >
                      归入分节…
                    </button>
                  </>
                ) : null}
                <button
                  type="button"
                  disabled={menuRow.locked}
                  title={menuRow.locked ? (menuRow.lockReason ?? '不允许移出表单') : undefined}
                  onClick={() => runMenu(() => setRowHidden(menu.table, menuRow.key, !menuRow.hidden))}
                >
                  {/* 明细里已移出的列不在表头上（没有可右键的对象），故明细只有"移出"这一态 */}
                  {menu.table === 'detail'
                    ? '移出表单'
                    : menuRow.hidden ? '放回表单' : '移出表单'}
                </button>
                <button type="button" onClick={() => runMenu(() => apply(resetRow(draft, menu.table, menuRow.key)))}>
                  {menu.table === 'detail' ? '恢复该列默认排版' : '恢复该字段默认排版'}
                </button>
              </>
            ) : null}
            {menu.kind === 'tab' ? (
              <>
                <button
                  type="button"
                  onClick={() => {
                    const current = draft?.tabs.find(tab => tab.no === menu.no)
                    const name = window.prompt('页签名称', current ? tabTitle(current) : '')
                    if (name && name.trim()) runMenu(() => apply(renameTab(draft!, menu.no, name)))
                  }}
                >
                  页签改名…
                </button>
                {/* 布局列数：页签级事实——同一表单的不同页签可以一行几列各不相同。
                    与"宽度/行高"同一套就地选择；改完自动把该页签内越界的跨度夹回来（调整即合规） */}
                <div className="erp-designer-menu-label">布局列数</div>
                <div className="erp-designer-menu-row">
                  {[1, 2, 3, 4].map(count => (
                    <button
                      key={count}
                      type="button"
                      className={draft && tabColumns(draft, menu.no) === count ? 'is-active' : ''}
                      aria-label={`一行 ${count} 列`}
                      onClick={() => runMenu(() => apply(setTabColumns(draft!, menu.no, count)))}
                    >
                      {`${count} 列`}
                    </button>
                  ))}
                </div>
                <button
                  type="button"
                  disabled={menu.no === RESIDENT_TAB_NO}
                  title={menu.no === RESIDENT_TAB_NO ? '默认页签不可删除（其余页签删掉后字段都回到这里）' : undefined}
                  onClick={() => runMenu(() => removeTab(menu.no))}
                >
                  删除页签
                </button>
              </>
            ) : null}
            {menu.kind === 'section' ? (
              <>
                <button
                  type="button"
                  onClick={() => {
                    const name = window.prompt('分节名称', menu.sectionId)
                    if (name && name.trim()) runMenu(() => apply(renameSection(draft, menu.sectionId, name)))
                  }}
                >
                  分节改名…
                </button>
                <button type="button" onClick={() => runMenu(() => apply(removeSection(draft, menu.sectionId)))}>
                  删除分节（字段回到无分节）
                </button>
              </>
            ) : null}
          </div>
        ) : null}
        {/* 拖拽时跟手的半透明卡片：不改变画布尺寸，只说明"正在拖谁" */}
        <DragOverlay dropAnimation={null}>
          {dragging ? <div className="erp-designer-drag-card">{draggedLabel}</div> : null}
        </DragOverlay>
      </DndContext>

      {/* 主表加字段走系统统一选择器：候选 = 本模块该表的可排字段 − 当前草稿里显示中的字段
          （排除项由 placedKeysForPicker 按草稿给出，已移出表单的字段因此回到候选里） */}
      <UnifiedChooser<PickedFieldRow>
        open={pickerOpen}
        mode="multi"
        title={`添加字段（${state.masterTable}）`}
        searchPlaceholder="搜索字段名/描述/类型"
        source={{
          kind: 'sourceKey',
          key: 'form-designer.fields',
          args: { moduleId: String(moduleId), table: 'master', exclude: placedKeysForPicker },
        }}
        emptyText="该表字段都已在表单里；刚移出表单的字段会回到这里（在画布上也可以右键它选「放回表单」）。运行态不显示的字段不进这里。"
        getRowId={row => String(row.F_ID ?? '')}
        onPick={rows => addPickedFields('master', rows)}
        onClose={() => setPickerOpen(false)}
      />

      {/* 明细「字段管理」：双栏（待选/已选）+ 上下排序，替代明细列的拖拽排序 */}
      {fieldManagerOpen && state.detailTable ? (
        <ErpColumnSelector
          open
          title={`字段管理（${state.detailTable}）`}
          groups={detailColumnGroups}
          onClose={() => setFieldManagerOpen(false)}
          onSave={applyDetailColumnSelection}
        />
      ) : null}
    </div>
  )
}
