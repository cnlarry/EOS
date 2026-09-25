import { useCallback, useEffect, useMemo, useState } from 'react'
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
import { ApiError } from '../../types/api'
import { apiClient } from '../../services/api'
import { createId } from '../../lib/uuid'
import { applyDrop, parseDragId, zoneOf, type DragSource, type DropTarget } from './formDesignerDrag'
import DesignCanvas from './DesignCanvas'
import DetailColumnPanel from './DetailColumnPanel'
import FieldPool, { type PoolEntry } from './FieldPool'
import PropertyPanel from './PropertyPanel'
import { useDesignerHistory } from './useDesignerHistory'
import {
  addFromPool,
  addTab,
  deleteTab,
  mergeCompanion,
  moveRow,
  moveRowToTab,
  renameTab,
  resetRow,
  setHidden,
  setPlacement,
  setSection,
  tabTitle,
  toDraft,
  toSavePayload,
  validateDraft,
} from './formDesignerDraft'
import type { DesignDraft, DesignState, DesignTable, SaveResponse } from './types'
import './form-designer.css'

interface FormDesignerPageProps {
  moduleId: number
  /** 退出设计态（回到表单页）。 */
  onExit: () => void
}

/**
 * 表单设计态：右键【表单设计】进入，与运行态**同一套渲染**，但输入控件不可填、
 * 值用字段代号占位。保存即生效（服务端同请求内重发布该模块快照），无需另行发布。
 *
 * 只改版式：顺序、占位、复合格、分节、页签、表单内隐藏；字段自身的属性在字段维护里改。
 */
export default function FormDesignerPage({ moduleId, onExit }: FormDesignerPageProps) {
  const [state, setState] = useState<DesignState | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const history = useDesignerHistory<DesignDraft | null>(null)
  const draft = history.value
  const [activeTable, setActiveTable] = useState<DesignTable>('master')
  const [activeTabNo, setActiveTabNo] = useState(1)
  const [selectedKey, setSelectedKey] = useState<string | null>(null)
  const [compact, setCompact] = useState(true)
  const [preview, setPreview] = useState(false)
  const [busy, setBusy] = useState(false)
  const [issues, setIssues] = useState<string[]>([])
  const [status, setStatus] = useState<{ tone: 'ok' | 'warn' | 'error'; text: string } | null>(null)
  const [dragging, setDragging] = useState<DragSource | null>(null)
  const [dropTarget, setDropTarget] = useState<DropTarget>(null)
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 4 } }))


  const draggedLabel = useMemo(() => {
    if (!draft || !dragging) return ''
    const rows = dragging.table === 'master' ? draft.master : draft.detail
    const row = rows.find(item => item.key === dragging.key)
    if (row) return row.label
    const pool = dragging.table === 'master' ? draft.masterPool : draft.detailPool
    return pool.find(field => field.key === dragging.key)?.label ?? dragging.key
  }, [draft, dragging])

  const handleDragStart = (event: DragStartEvent) => {
    setDragging(parseDragId(String(event.active.id)))
  }

  /** 落点判定放在这里：只有"鼠标落在格的哪一段"这类几何问题需要实时算，语义都在 applyDrop。 */
  const handleDragMove = (event: DragMoveEvent) => {
    const over = event.over
    if (!over) {
      setDropTarget(null)
      return
    }
    const data = over.data.current as { kind?: string; key?: string; tabNo?: number; sectionId?: string | null } | undefined
    if (data?.kind === 'cell' && data.key) {
      const pointerX = (event.activatorEvent as PointerEvent).clientX + event.delta.x
      const zone = zoneOf(pointerX - over.rect.left, over.rect.width)
      setDropTarget(zone === 'merge' ? { kind: 'merge', key: data.key } : { kind: 'insert', key: data.key, before: zone === 'before' })
      return
    }
    if (data?.kind === 'tab' && typeof data.tabNo === 'number') {
      setDropTarget({ kind: 'tab', tabNo: data.tabNo })
      return
    }
    if (data?.kind === 'section') {
      setDropTarget({ kind: 'section', sectionId: data.sectionId ?? null })
      return
    }
    setDropTarget(data?.kind === 'pool' ? { kind: 'remove' } : null)
  }

  const handleDragEnd = (event: DragEndEvent) => {
    const source = parseDragId(String(event.active.id))
    const target = dropTarget
    setDragging(null)
    setDropTarget(null)
    if (!source || !target || !draft) return
    const result = applyDrop(draft, source, target)
    if ('rejected' in result) {
      setStatus({ tone: 'warn', text: result.rejected })
      return
    }
    apply(result.draft)
    setSelectedKey(source.key)
  }

  const load = useCallback(async () => {
    setLoadError(null)
    try {
      const next = await apiClient.get<DesignState>(`/admin/form-layout/${moduleId}`)
      setState(next)
      history.rebase(toDraft(next))
      setActiveTabNo(1)
      setSelectedKey(null)
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

  const poolEntries = useMemo<PoolEntry[]>(() => {
    if (!draft) return []
    const rows = activeTable === 'master' ? draft.master : draft.detail
    const pool = activeTable === 'master' ? draft.masterPool : draft.detailPool
    return [
      ...rows.map(row => ({
        key: row.key,
        label: row.label,
        dataType: row.dataType,
        placed: true,
        userVisible: row.userVisible,
        required: row.required,
        isPrimaryKey: row.isPrimaryKey,
        isVirtual: row.isVirtual,
      })),
      ...pool.map(field => ({
        key: field.key,
        label: field.label,
        dataType: field.dataType,
        placed: false,
        userVisible: field.userVisible,
        required: field.required,
        isPrimaryKey: field.isPrimaryKey,
        isVirtual: field.isVirtual,
      })),
    ]
  }, [draft, activeTable])

  const selectedRow = useMemo(() => {
    if (!draft || !selectedKey) return null
    const rows = activeTable === 'master' ? draft.master : draft.detail
    return rows.find(row => row.key === selectedKey) ?? null
  }, [draft, activeTable, selectedKey])

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
  }, [draft, state, moduleId, history])

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
      setStatus({ tone: 'ok', text: response.message ?? '已重置为默认版式。' })
    } catch (error) {
      setStatus({ tone: 'error', text: error instanceof ApiError ? error.message : '重置失败。' })
    } finally {
      setBusy(false)
    }
  }, [draft, state, moduleId, history])

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

  const addField = (key: string) => {
    apply(addFromPool(draft!, activeTable, key))
    setSelectedKey(key)
  }

  const handleMove = (key: string, delta: number) => apply(moveRow(draft!, activeTable, key, delta))

  const handleHide = (key: string) => apply(setHidden(draft!, activeTable, key, true))

  const handleForceNewLine = (key: string) => {
    const row = draft?.master.find(item => item.key === key)
    if (!row) return
    apply(setPlacement(draft!, key, { newLine: !row.newLine }))
  }

  const handleMerge = (companionKey: string | null) => {
    if (!draft || !selectedKey) return
    const result = mergeCompanion(draft, selectedKey, companionKey)
    if (result.rejected) {
      setStatus({ tone: 'warn', text: result.rejected })
      return
    }
    apply(result.draft)
  }

  if (loadError) {
    return (
      <div className="erp-designer">
        <div className="erp-designer-error">{loadError}</div>
        <div className="erp-designer-toolbar">
          <button type="button" className="erp-command-btn" onClick={() => void load()}>
            重试
          </button>
          <button type="button" className="erp-command-btn" onClick={exit}>
            返回表单
          </button>
        </div>
      </div>
    )
  }

  if (!draft || !state) {
    return <div className="erp-designer erp-designer-loading">正在加载版式…</div>
  }

  return (
    <div className="erp-designer">
      <div className="erp-designer-toolbar">
        <button type="button" className="erp-command-btn is-primary" disabled={busy || !history.dirty} onClick={() => void save()}>
          保存
        </button>
        <button type="button" className="erp-command-btn" disabled={busy || !history.dirty} onClick={discard}>
          放弃
        </button>
        <button type="button" className="erp-command-btn" disabled={busy} onClick={() => void reset()}>
          重置为默认版式
        </button>
        <button type="button" className="erp-command-btn" disabled={!history.canUndo} onClick={history.undo} title="Ctrl+Z">
          撤销
        </button>
        <button type="button" className="erp-command-btn" disabled={!history.canRedo} onClick={history.redo} title="Ctrl+Y">
          重做
        </button>
        <label className="erp-designer-check">
          <input type="checkbox" checked={compact} onChange={event => setCompact(event.target.checked)} />
          紧凑排列
        </label>
        <label className="erp-designer-check">
          <input type="checkbox" checked={preview} onChange={event => setPreview(event.target.checked)} />
          预览
        </label>
        <span className="erp-designer-spacer" />
        <span className="erp-designer-muted">
          {state.title} · {state.masterTable}
          {state.detailTable ? ` / ${state.detailTable}` : ''} · {state.columns} 列
        </span>
        <button type="button" className="erp-command-btn" onClick={exit}>
          返回表单
        </button>
      </div>

      {status ? <div className={`erp-designer-status is-${status.tone}`}>{status.text}</div> : null}
      <div className="erp-designer-status is-info">
        {activeTable === 'master'
          ? state.master.customized ? '本表已有定制版式' : '本表当前是推导默认（保存后即为定制）'
          : state.detail.customized ? '本表已有定制版式' : '本表当前是推导默认（保存后即为定制）'}
      </div>
      {issues.length > 0 && !preview ? (
        <div className="erp-designer-status is-warn">
          保存前请先处理：{issues.slice(0, 3).join(' ')}
          {issues.length > 3 ? ` 等 ${issues.length} 处` : ''}
        </div>
      ) : null}

      <DndContext
        sensors={sensors}
        collisionDetection={closestCenter}
        onDragStart={handleDragStart}
        onDragMove={handleDragMove}
        onDragEnd={handleDragEnd}
        onDragCancel={() => {
          setDragging(null)
          setDropTarget(null)
        }}
      >
        <div className="erp-designer-body">
          {!preview ? (
            <FieldPool
              entries={poolEntries}
              activeTable={activeTable}
              hasDetail={state.detailTable !== null}
              onTableChange={table => {
                setActiveTable(table)
                setSelectedKey(null)
              }}
              onAdd={addField}
              disabled={busy}
            />
          ) : null}

          <div className="erp-designer-main">
            <DesignCanvas
              draft={draft}
              activeTabNo={activeTabNo}
              onActiveTabChange={setActiveTabNo}
              selectedKey={activeTable === 'master' ? selectedKey : null}
              onSelect={key => {
                setActiveTable('master')
                setSelectedKey(key)
              }}
              compact={compact}
              preview={preview}
              draggingKey={dragging?.key ?? null}
              dropTarget={dropTarget}
              onMove={handleMove}
              onHide={handleHide}
              onForceNewLine={handleForceNewLine}
              onRenameTab={(no, title) => apply(renameTab(draft, no, title))}
              onAddTab={() => apply(addTab(draft, `页签 ${draft.tabs.length + 1}`))}
              onDeleteTab={no => {
                if (!window.confirm(`删除页签「${tabTitle(draft.tabs.find(tab => tab.no === no) ?? { no, title: '' })}」？其中的字段会回到默认页签。`)) return
                apply(deleteTab(draft, no))
                if (activeTabNo === no) setActiveTabNo(1)
              }}
            />

            {state.detailTable ? (
              <DetailColumnPanel
                table={state.detailTable}
                rows={draft.detail}
                selectedKey={activeTable === 'detail' ? selectedKey : null}
                preview={preview}
                draggingKey={dragging?.table === 'detail' ? dragging.key : null}
                dropTarget={dropTarget}
                onSelect={key => {
                  setActiveTable('detail')
                  setSelectedKey(key)
                }}
                onMove={(key, delta) => apply(moveRow(draft, 'detail', key, delta))}
                onHidden={(key, hidden) => apply(setHidden(draft, 'detail', key, hidden))}
              />
            ) : null}
          </div>

        {!preview ? (
          <PropertyPanel
            draft={draft}
            table={activeTable}
            row={selectedRow}
            disabled={busy}
            onPlacement={(span, rowSpan) => {
              if (!selectedRow) return
              apply(setPlacement(draft, selectedRow.key, { span, rowSpan }))
            }}
            onNewLine={value => {
              if (!selectedRow) return
              apply(setPlacement(draft, selectedRow.key, { newLine: value }))
            }}
            onSection={sectionId => {
              if (!selectedRow) return
              apply(setSection(draft, selectedRow.key, sectionId))
            }}
            onTab={tabNo => {
              if (!selectedRow || activeTable !== 'master') return
              apply(moveRowToTab(draft, selectedRow.key, tabNo))
            }}
            onHidden={hidden => {
              if (!selectedRow) return
              apply(setHidden(draft, activeTable, selectedRow.key, hidden))
            }}
            onMergeCompanion={handleMerge}
            onResetRow={() => {
              if (!selectedRow) return
              apply(resetRow(draft, activeTable, selectedRow.key))
            }}
          />
        ) : null}
        </div>
        {/* 拖拽时跟手的半透明卡片：不改变画布尺寸，只说明"正在拖谁" */}
        <DragOverlay dropAnimation={null}>
          {dragging ? <div className="erp-designer-drag-card">{draggedLabel}</div> : null}
        </DragOverlay>
      </DndContext>
    </div>
  )
}
