import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnDef, RowSelectionState, SortingState } from '@tanstack/react-table'
import { memo, useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type KeyboardEvent as ReactKeyboardEvent, type ReactNode } from 'react'
import { useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { IconPlayerPlay, IconTrash } from '@tabler/icons-react'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { ErpCommandBar, type ErpCommandItem } from '../../components/common/ErpCommandBar'
import { ErpTable } from '../../components/common/ErpTable'
import { UnifiedChooser, type UnifiedChooserRow } from '../../components/common/UnifiedChooser'
import { useFormBreadcrumb } from '../../components/layout/FormBreadcrumbContext'
import { useTabDirty } from '../../components/layout/workspaceDirty'
import { AttachmentDialog } from './AttachmentDialog'
import { WorkflowTimeline, type WorkflowTimelineRow } from '../workflow/WorkflowTimeline'
import { parseWorkbenchKey, workbenchAction, workbenchCopy, workbenchEdit, workbenchList, workbenchNew, workbenchView } from './workbenchPath'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'
import { assistantPrefillKey } from '../../lib/storageKeys'
import { FormFieldRenderer } from './FormFieldRenderer'
import type { FormDefinition, FormFieldDefinition } from './formDefinition'
import { alignClass, formatFieldValue } from './fieldFormat'
import { buildFormCells, buildFormRows, buildFormSections, moveLifecycleToTail } from './formLayout'
import { fieldVariant } from './formFieldKind'
import { validateDetailRows, validateMasterFields, type FieldErrors } from './formValidation'
import { buildViewToolbarItems } from './formToolbar'
import { useDocumentActionRunner } from './documentActionRunner'
import { AMOUNT_COLUMN_KEYS, AMOUNT_TRIGGER_KEYS, previewDetailAmount, previewMasterAmounts } from './amountCalculator'
import { describeApiError } from '../../lib/errors'
import {
  buildKey, canonicalizeDecimalValue, chooserTitle, describeError, detailControlMinWidth, emptyValue, extractDocNo, newIdempotencyKey,
  parseReturnItems, readDetailChooserSources,
  summarizeFieldErrors, withDetailChooserSource, writableFields, type DetailGridRow, type RecordBundle, type RecordSaveResponse, type SaveRecordRequest,
} from './formEditorUtils'

// 统一表单主表布局列数：全局固定一行四列，忽略各模块 FORM_COLUMNS 元数据
//（含显式配置 3 列的 113 个模块），
const UNIFIED_FORM_COLUMNS = 4

/** 明细视图排序（快照）：返回按字段排序的物理行序。仅在切换排序/增删行时重算，编辑中不随值漂移。 */
function sortDetailIndices(rows: Record<string, string>[], key: string, dir: 1 | -1): number[] {
  const indices = rows.map((_, index) => index)
  indices.sort((a, b) => {
    const va = rows[a][key] ?? ''
    const vb = rows[b][key] ?? ''
    const na = Number(va)
    const nb = Number(vb)
    const numeric = va !== '' && vb !== '' && !Number.isNaN(na) && !Number.isNaN(nb)
    const cmp = numeric ? na - nb : String(va).localeCompare(String(vb), 'zh-CN', { numeric: true })
    return cmp * dir
  })
  return indices
}

/** 按单审批历史时间线行（/workflow/{moduleId}/history 返回）：task=审批动作 / confirm=流程完成确认。 */
interface WorkflowHistoryRow {
  kind: 'task' | 'confirm'
  step: string
  stepDesc: string
  approver: string | null
  state: string
  message: string | null
  date: string | null
}

interface MasterFieldProps {
  field: FormFieldDefinition
  value: string
  error?: string
  bare: boolean
  viewing: boolean
  canSetup: boolean
  masterAmountLocked: boolean
  onFieldChange: (key: string, value: string) => void
  onOpenChooser: (field: FormFieldDefinition) => void
  onFieldSetup: (field: FormFieldDefinition, x: number, y: number) => void
}

/** 主表单字段 memo 单元：仅字段值/错误/可见性等变化时才重渲染，阻断键入时无关字段的级联更新 */
const MasterField = memo(function MasterField({ field, value, error, bare, viewing, canSetup, masterAmountLocked, onFieldChange, onOpenChooser, onFieldSetup }: MasterFieldProps) {
  const effective = masterAmountLocked && AMOUNT_COLUMN_KEYS.has(field.key.toUpperCase()) ? { ...field, isReadonly: true } : field
  return (
    <FormFieldRenderer
      field={effective}
      value={value}
      error={error}
      viewing={viewing}
      onChange={next => onFieldChange(field.key, next)}
      onChoose={onOpenChooser}
      onFieldSetup={canSetup ? onFieldSetup : undefined}
      bare={bare}
    />
  )
})

interface MasterFormGridProps {
  form: FormDefinition
  activeTabNo: number
  hasTabs: boolean
  masterValues: Record<string, string>
  fieldErrors: FieldErrors
  viewing: boolean
  canSetup: boolean
  masterAmountLocked: boolean
  onFieldChange: (key: string, value: string) => void
  onOpenChooser: (field: FormFieldDefinition) => void
  onFieldSetup: (field: FormFieldDefinition, x: number, y: number) => void
}

/** 主表字段网格（memo）：细节随主表值/错误变化时才重渲染，与明细网格相互隔离 */
const MasterFormGrid = memo(function MasterFormGrid({ form, activeTabNo, hasTabs, masterValues, fieldErrors, viewing, canSetup, masterAmountLocked, onFieldChange, onOpenChooser, onFieldSetup }: MasterFormGridProps) {
  /** 主表 Enter 下一字段（textarea/select/checkbox/日期原生控件不拦截） */
  const handleKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    if (viewing || event.key !== 'Enter') return
    const target = event.target as HTMLElement
    if (target.tagName === 'TEXTAREA' || target.tagName === 'SELECT' || target.tagName === 'BUTTON') return
    const inputType = (target as HTMLInputElement).type
    if (inputType === 'checkbox' || inputType === 'date' || inputType === 'datetime-local') return
    event.preventDefault()
    const focusables = Array.from(
      event.currentTarget.querySelectorAll<HTMLElement>('input.form-control:not([disabled]), select.form-select:not([disabled])'),
    )
    const index = focusables.indexOf(target)
    ;(focusables[index + 1] ?? focusables[0])?.focus()
  }
  const visibleMaster = form.masterFields.filter(field => field.isVisible)
  const cells = buildFormCells(visibleMaster).filter(cell => !hasTabs || cell[0].tabNo === activeTabNo)
  // 浏览态：建立/修改/审核/结案的人·日期·状态排在其它内容之后
  const sections = viewing ? moveLifecycleToTail(buildFormSections(cells)) : buildFormSections(cells)
  const renderCell = (cell: FormFieldDefinition[]) => {
    const [main, ...companions] = cell
    if (companions.length === 0) {
      return (
        <MasterField
          key={main.key}
          field={main}
          value={masterValues[main.key] ?? ''}
          error={fieldErrors[main.key]}
          bare={false}
          viewing={viewing}
          canSetup={canSetup}
          masterAmountLocked={masterAmountLocked}
          onFieldChange={onFieldChange}
          onOpenChooser={onOpenChooser}
          onFieldSetup={onFieldSetup}
        />
      )
    }
    const isBoolean = main.dataType.toLowerCase().includes('bit')
    return (
      <div key={main.key} className="erp-form-cell">
        <label
          className="erp-form-label"
          onContextMenu={canSetup ? event => { event.preventDefault(); onFieldSetup(main, event.clientX, event.clientY) } : undefined}
        >
          {main.label}{!isBoolean && !main.isReadonly && !main.serverFilled && main.isRequired ? ' *' : ''}
        </label>
        <div className="erp-form-cell-controls">
          {[main, ...companions].map(field => (
            <MasterField
              key={field.key}
              field={field}
              value={masterValues[field.key] ?? ''}
              error={fieldErrors[field.key]}
              bare
              viewing={viewing}
              canSetup={canSetup}
              masterAmountLocked={masterAmountLocked}
              onFieldChange={onFieldChange}
              onOpenChooser={onOpenChooser}
              onFieldSetup={onFieldSetup}
            />
          ))}
        </div>
      </div>
    )
  }
  return (
    <div className="erp-form-grid" onKeyDown={handleKeyDown}>
      {sections.map((section, sectionIndex) => (
        <section className="erp-form-group" key={section.title ?? `default-${sectionIndex}`}>
          {section.title ? <div className="erp-form-group-title">{section.title}</div> : null}
          {buildFormRows(section.cells, UNIFIED_FORM_COLUMNS).map((row, rowIndex) => (
            <div className="erp-form-row" key={rowIndex} style={{ '--erp-form-cols': UNIFIED_FORM_COLUMNS } as CSSProperties}>
              {row.map(cell => renderCell(cell))}
            </div>
          ))}
        </section>
      ))}
    </div>
  )
})

interface DetailFieldCellProps {
  field: FormFieldDefinition
  value: string
  error?: string
  index: number
  onFieldChange: (index: number, key: string, value: string) => void
  onChoose: (index: number, field: FormFieldDefinition) => void
}

/** 明细格 memo 单元：输入一个格子时其余行/格不重渲染（仅编辑态使用） */
const DetailFieldCell = memo(function DetailFieldCell({ field, value, error, index, onFieldChange, onChoose }: DetailFieldCellProps) {
  return (
    <FormFieldRenderer
      field={field}
      value={value}
      error={error}
      onChange={next => onFieldChange(index, field.key, next)}
      onChoose={choosable => onChoose(index, choosable)}
      bare
    />
  )
})

interface DetailFormGridProps {
  form: FormDefinition
  detailRows: Record<string, string>[]
  detailErrors: FieldErrors[]
  sortedIndices: number[] | null
  detailSort: { key: string; dir: 1 | -1 } | null
  selectedDetailRows: Set<number>
  viewing: boolean
  /** 子表标题栏右侧的扩展位：浏览态放明细级自定义按钮，编辑态该位置是「新增一行/删除所选」。 */
  actions?: ReactNode
  storageKey: string
  onAddRow: () => void
  onRemoveRow: (index: number) => void
  onRemoveSelected: () => void
  onFieldChange: (index: number, key: string, value: string) => void
  onChoose: (index: number, field: FormFieldDefinition) => void
  onSortChange: (next: SortingState) => void
  onSelectionChange: (next: RowSelectionState) => void
  onResize: (fieldKey: string, width: number) => void
}

/** 明细卡（memo）：主表字段输入等不涉及明细行的状态变化时不重渲染；浏览态只读展示，不提供增删入口 */
const DetailFormGrid = memo(function DetailFormGrid({ form, detailRows, detailErrors, sortedIndices, detailSort, selectedDetailRows, viewing, actions, storageKey, onAddRow, onRemoveRow, onRemoveSelected, onFieldChange, onChoose, onSortChange, onSelectionChange, onResize }: DetailFormGridProps) {
  /** 明细网格 Enter：同列下一行继续；末行则新增行后聚焦同列（浏览态无输入框，直接跳过） */
  const handleKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    if (viewing || event.key !== 'Enter') return
    const target = event.target as HTMLElement
    if (!(target instanceof HTMLInputElement)) return
    if (target.type === 'checkbox' || target.type === 'date' || target.type === 'datetime-local') return
    const row = target.closest('tr')
    const tbody = target.closest('tbody')
    if (!row || !tbody) return
    event.preventDefault()
    const rows = Array.from(tbody.querySelectorAll('tr'))
    const rowIndex = rows.indexOf(row)
    const cellInputs = Array.from(row.querySelectorAll<HTMLElement>('td input.form-control'))
    const columnIndex = Math.max(0, cellInputs.indexOf(target))
    if (rowIndex < rows.length - 1) {
      rows[rowIndex + 1].querySelectorAll<HTMLElement>('td input.form-control')[columnIndex]?.focus()
      return
    }
    onAddRow()
    window.setTimeout(() => {
      tbody.querySelector('tr:last-of-type')?.querySelectorAll<HTMLElement>('td input.form-control')[columnIndex]?.focus()
    }, 30)
  }
  const visibleDetail = form.detailFields.filter(field => field.isVisible)
  const viewIndices = sortedIndices && sortedIndices.length === detailRows.length
    ? sortedIndices
    : detailRows.map((_, index) => index)
  const rowSelection = Object.fromEntries([...selectedDetailRows].map(index => [`r${index}`, true])) as RowSelectionState
  const gridRows: DetailGridRow[] = viewIndices.map(index => ({ __id: `r${index}`, __index: index, ...detailRows[index] }))
  const columns: ColumnDef<DetailGridRow, unknown>[] = [
    // 浏览态不允许改明细：隐藏选择列与行操作列，只展示内容
    ...(!viewing ? [{
      id: '__check',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'erp-detail-check text-center', resizable: false, truncate: false },
      header: ({ table }: { table: { getIsAllPageRowsSelected: () => boolean; getIsSomePageRowsSelected: () => boolean; getToggleAllPageRowsSelectedHandler: () => (event: unknown) => void } }) => (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label="全选"
          checked={table.getIsAllPageRowsSelected()}
          ref={input => { if (input) input.indeterminate = table.getIsSomePageRowsSelected() }}
          onChange={table.getToggleAllPageRowsSelectedHandler()}
        />
      ),
      cell: ({ row }: { row: { getIsSelected: () => boolean; getToggleSelectedHandler: () => (event: unknown) => void; index: number } }) => (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label={`选择第${row.index + 1}行`}
          checked={row.getIsSelected()}
          onChange={row.getToggleSelectedHandler()}
          onClick={event => event.stopPropagation()}
        />
      ),
    } as ColumnDef<DetailGridRow, unknown>] : []),
    {
      id: '__rowNo',
      header: '序号',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'erp-detail-row-no text-center', resizable: false, truncate: false },
      cell: ({ row }) => <span className="text-secondary">{row.index + 1}</span>,
    },
    ...visibleDetail.map((field): ColumnDef<DetailGridRow, unknown> => {
      const isBit = (field.dataType ?? '').toLowerCase() === 'bit'
      // 浏览态与工作台子表同口径：纯文本展示（bit 用禁用复选框），保留电子表格能力
      //（表头排序/列宽拖拽/复制/键盘导航/吸顶表头由 ErpTable 默认提供；title/copyText 取展示文本）
      if (viewing) {
        const formatted = (value: unknown) => formatFieldValue(value, field.dataType, field.displayFormat)
        return {
          id: field.key,
          accessorKey: field.key,
          header: field.label,
          enableSorting: true,
          meta: {
            minWidth: field.displayLength,
            dataType: field.dataType,
            cellClassName: isBit ? 'text-center' : alignClass(undefined, field.dataType),
            truncate: isBit ? false : undefined,
            title: ({ value }) => formatted(value) || undefined,
            copyText: ({ value }) => formatted(value) || '—',
          },
          cell: ({ row }) => {
            const raw = row.original[field.key]
            if (isBit) {
              const checked = raw === true || raw === '1' || String(raw ?? '').toLowerCase() === 'true'
              return <input type="checkbox" className="form-check-input" checked={checked} disabled aria-label={field.label} />
            }
            const text = formatted(raw)
            if (!text) return '—'
            return text
          },
        }
      }
      return {
        id: field.key,
        accessorKey: field.key,
        header: field.label,
        enableSorting: true,
        meta: { minWidth: Math.max(field.displayLength, detailControlMinWidth(field)), dataType: field.dataType, minWidthFloor: true, truncate: false },
        cell: ({ row }) => {
          const index = row.original.__index
          return (
            <DetailFieldCell
              key={`${row.original.__id}-${field.key}`}
              field={field}
              value={String(row.original[field.key] ?? '')}
              error={detailErrors[index]?.[field.key]}
              index={index}
              onFieldChange={onFieldChange}
              onChoose={onChoose}
            />
          )
        },
      }
    }),
    ...(!viewing ? [{
      id: '__actions',
      header: '操作',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'erp-detail-actions text-center', resizable: false, truncate: false },
      cell: ({ row }: { row: { original: DetailGridRow; index: number } }) => (
        // Inline delete as icon button, aligned with command bar icon conventions
        <Button
          size="sm"
          variant="danger"
          icon={<IconTrash size={16} />}
          title="删除本行"
          aria-label={`删除第${row.index + 1}行`}
          onClick={() => onRemoveRow(row.original.__index)}
        />
      ),
    } as ColumnDef<DetailGridRow, unknown>] : []),
  ]
  // 浏览态默认不显示这一排；有明细级自定义按钮（或视图排序提示）时它才是自定义按钮的落点。
  const showToolbar = !viewing || Boolean(detailSort) || Boolean(actions)
  return (
    <section className="card erp-detail-card">
      {showToolbar ? (
        <div className="card-header erp-detail-toolbar">
          <div className="d-flex gap-2 w-100 align-items-center">
            {!viewing ? (
              <>
                <Button size="sm" onClick={onAddRow}>新增一行</Button>
                <Button size="sm" variant="danger" disabled={selectedDetailRows.size === 0} onClick={onRemoveSelected}>删除所选{selectedDetailRows.size > 0 ? ` (${selectedDetailRows.size})` : ''}</Button>
              </>
            ) : null}
            {detailSort && <span className="small text-secondary align-self-center">视图排序（保存顺序以序号 SERIAL_NO 为准）</span>}
            {/* 子表标题栏右侧：编辑态是「新增一行/删除所选」，浏览态改为明细级自定义按钮 */}
            {actions ? <div className="ms-auto d-flex gap-2">{actions}</div> : null}
          </div>
        </div>
      ) : null}
      <div className="table-responsive" onKeyDown={handleKeyDown}>
        <ErpTable
          columns={columns}
          data={gridRows}
          getRowId={row => row.__id}
          sorting={detailSort ? [{ id: detailSort.key, desc: detailSort.dir === -1 }] : []}
          onSortingChange={onSortChange}
          rowSelection={rowSelection}
          onRowSelectionChange={onSelectionChange}
          // 浏览态只读明细可窗口化（行数多时只渲染可视区）；编辑态保留全量 DOM 供 Enter/新增行交互
          virtualize={viewing}
          resizable
          storageKey={storageKey}
          persistResize={false}
          onColumnResize={onResize}
          className="erp-detail-grid"
          responsive={false}
          empty={
            detailRows.length === 0 && !viewing ? (
              <div className="erp-detail-empty">
                <Button size="sm" variant="secondary" onClick={onAddRow}>+ 新增一行</Button>
              </div>
            ) : undefined
          }
        />
      </div>
    </section>
  )
})

export function FormEditorPage() {
  const params = useParams()
  const { moduleId = '' } = params
  const splat = params['*'] ?? ''
  const location = useLocation()
  const [searchParams] = useSearchParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { setBreadcrumb } = useFormBreadcrumb()
  const wbAction = workbenchAction(location.pathname)
  const isEdit = wbAction === 'edit'
  const isView = wbAction === 'view'
  const isCopy = wbAction === 'copy'
  // 记录主键以路径段表达（主键序）：/workbench/{moduleId}/view/{k1}/{k2}；
  // API 调用仍以 ?key=[...] 传给后端（API 契约不变）。
  const keyParam = (() => {
    const pathKey = parseWorkbenchKey(splat)
    return pathKey ? JSON.stringify(pathKey) : null
  })()
  const copyFrom = searchParams.get('copyFrom')
  // 跨模块关联字段浏览（FieldBrowseLink 带入 from）：来源工作台模块。返回与面包屑按来源呈现
  //（URL 参数携带，刷新不丢；仅当为合法模块 ID 且不同于当前模块时生效）。
  const fromModuleId = (() => {
    const raw = searchParams.get('from')
    if (!raw || !/^\d+$/.test(raw) || raw === moduleId) return null
    return raw
  })()
  // 无主键段的 view/edit 直达（如 /workbench/1405/view）重定向到列表，避免空白表单
  useEffect(() => {
    if ((isEdit || isView) && !keyParam) navigate(workbenchList(moduleId), { replace: true })
  }, [isEdit, isView, keyParam, moduleId, navigate])
  // Route state carries cross-page context: save warnings and list navigation (previous/next)
  interface ViewNavState { navKeys?: string[][]; navIndex?: number; warnings?: { code: string; message: string }[] | null }
  const locationState = (location.state ?? null) as ViewNavState | null
  const navKeys = locationState?.navKeys
  const navIndex = locationState?.navIndex
  const [warningsDismissed, setWarningsDismissed] = useState(false)
  useEffect(() => { setWarningsDismissed(false) }, [location.key])
  const warnings = isView && !warningsDismissed ? locationState?.warnings ?? null : null
  const goNeighbor = (delta: number) => {
    if (!navKeys || navIndex == null) return
    const next = navIndex + delta
    if (next < 0 || next >= navKeys.length) return
    // 相邻记录按进入浏览态时的列表当前顺序，不回退到物理顺序
    navigate(workbenchView(moduleId, navKeys[next]),
      { state: { navKeys, navIndex: next } satisfies ViewNavState })
  }
  const originalRef = useRef<Record<string, string>>({})
  // 本会话在界面上选过的来源（字段键 → 来源序号）：随保存下发，服务端据此记住
  // 「这张单的这个字段当初从哪个来源选入」；记录切换时清空，避免串到另一张单。
  const masterChooserSourcesRef = useRef<Record<string, number>>({})
  // Idempotency key = one user save intent; rotate on success/cancel, reuse on validation failure
  const idempotencyRef = useRef(newIdempotencyKey())
  const [masterValues, setMasterValues] = useState<Record<string, string>>({})
  const [detailRows, setDetailRows] = useState<Record<string, string>[]>([])
  const [chooserField, setChooserField] = useState<FormFieldDefinition | null>(null)
  const [chooserSerial, setChooserSerial] = useState<number | null>(null)
  const [dirty, setDirty] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({})
  const [detailErrors, setDetailErrors] = useState<FieldErrors[]>([])
  const [detailChooser, setDetailChooser] = useState<{ index: number; field: FormFieldDefinition } | null>(null)
  const [detailChooserSerial, setDetailChooserSerial] = useState<number | null>(null)
  /** 多来源「各是各的入口」：先弹来源菜单。 */
  const [sourceMenu, setSourceMenu] = useState<
    | { kind: 'master'; field: FormFieldDefinition }
    | { kind: 'detail'; index: number; field: FormFieldDefinition }
    | null
  >(null)
  /** 标签右键「字段设置」菜单（仅 canSetup 时触发） */
  const [fieldSetupMenu, setFieldSetupMenu] = useState<{ field: FormFieldDefinition; x: number; y: number } | null>(null)
  const [selectedDetailRows, setSelectedDetailRows] = useState<Set<number>>(new Set())
  const [detailSort, setDetailSort] = useState<{ key: string; dir: 1 | -1 } | null>(null)
  /** 明细视图排序快照（物理行序）。null=未排序（视图顺序=物理顺序）。 */
  const [sortedIndices, setSortedIndices] = useState<number[] | null>(null)
  const [activeTab, setActiveTab] = useState(1)
  const [attachOpen, setAttachOpen] = useState(false)
  // 明细列宽统一走服务端（FIELDS.DISPLAY_LENGTH，与工作台一致），拖拽后批量保存
  const widthBatch = useRef<Record<string, number>>({})
  const widthTimer = useRef<number | null>(null)
  // 稳定回调所需的最新值快照（渲染期同步，仅用于读）
  const detailRowsRef = useRef<Record<string, string>[]>([])
  detailRowsRef.current = detailRows
  const masterValuesRef = useRef<Record<string, string>>({})
  masterValuesRef.current = masterValues

  const formQuery = useQuery({
    queryKey: ['workbench', moduleId, 'form-definition', isEdit ? 'edit' : isView ? 'view' : 'new'],
    queryFn: () => apiClient.get<FormDefinition>(`/document-workbench/${moduleId}/form-definition?mode=${isEdit ? 'edit' : isView ? 'view' : 'new'}`),
  })
  const recordQuery = useQuery({
    queryKey: ['workbench', moduleId, 'record', keyParam ?? copyFrom],
    queryFn: () => apiClient.get<RecordBundle>(`/document-workbench/${moduleId}/record`, { query: { key: keyParam ?? copyFrom ?? '' } }),
    enabled: (isEdit || isView || isCopy) && Boolean(keyParam ?? copyFrom) && formQuery.isSuccess,
  })
  // 单据操作执行器（必须在任何 early return 之前调用）：只在浏览态可用，键取 URL 路径主键（同步权威）。
  const documentAction = useDocumentActionRunner({
    moduleId,
    keyValues: isView ? parseWorkbenchKey(splat) : null,
    dirty,
    onRefreshed: async () => { await recordQuery.refetch() },
    onNavigate: (targetModuleId, targetKey) => navigate(workbenchView(String(targetModuleId), targetKey)),
  })

  // 上抛单据面包屑给 AppShell：编辑/查看带单号，新增/复制不显示单号
  useEffect(() => {
    if (!formQuery.data) return
    const master = recordQuery.data?.master
    const values: Record<string, string> = {}
    for (const field of formQuery.data.masterFields) {
      if (field.isVisible && master && master[field.key] != null) values[field.key] = String(master[field.key])
    }
    const docNo = isEdit || isView ? extractDocNo(formQuery.data, values) : null
    setBreadcrumb({ moduleTitle: formQuery.data.title, docNo })
    return () => setBreadcrumb(null)
  }, [formQuery.data, recordQuery.data, isEdit, isView, isCopy, setBreadcrumb])

  useEffect(() => {
    if (!formQuery.data || isEdit || isView || isCopy) return
    const initial: Record<string, string> = {}
    const defaults = formQuery.data.defaultValues ?? {}
    for (const field of formQuery.data.masterFields) {
      if (field.isVisible) initial[field.key] = defaults[field.key] ?? emptyValue(field)
    }
    // Assistant pre-fill: one-shot sessionStorage channel; assistant values override defaults
    // (server-filled fields are already excluded by draft generation), final validation by the save pipeline
    const prefillRaw = sessionStorage.getItem(assistantPrefillKey(moduleId))
    if (prefillRaw) {
      sessionStorage.removeItem(assistantPrefillKey(moduleId))
      try {
        const prefill = JSON.parse(prefillRaw) as Record<string, unknown>
        for (const [key, value] of Object.entries(prefill)) {
          if (key in initial && value != null) initial[key] = String(value)
        }
      } catch {
        // 非法草稿直接忽略，不影响正常新增
      }
    }
    setMasterValues(initial)
  }, [formQuery.data, isEdit, isView, isCopy, moduleId])

  useEffect(() => {
    if (formQuery.data) setActiveTab(formQuery.data.tabs[0]?.no ?? 1)
  }, [formQuery.data])

  useEffect(() => {
    if (!formQuery.data || !recordQuery.data) return
    const master: Record<string, string> = {}
    for (const field of formQuery.data.masterFields) {
      if (field.isVisible) master[field.key] = recordQuery.data.master[field.key] == null ? '' : String(recordQuery.data.master[field.key])
    }
    if (isCopy) {
      // 复制（IF_COPY）：主键/自动单号由服务端重新生成，CAN_COPY=0 字段不带出；
      // 应用新增默认值（单别/单号/日期），保存走新增管线
      for (const field of formQuery.data.masterFields) {
        if (!field.isVisible) continue
        if (formQuery.data.masterPkOrder.some(pk => pk.toLowerCase() === field.key.toLowerCase()) || !field.canCopy) master[field.key] = ''
      }
      const defaults = formQuery.data.defaultValues ?? {}
      for (const field of formQuery.data.masterFields) {
        if (field.isVisible && defaults[field.key] != null) master[field.key] = defaults[field.key]
      }
    }
    originalRef.current = {}
    masterChooserSourcesRef.current = {}
    for (const field of writableFields(formQuery.data.masterFields)) originalRef.current[field.key] = master[field.key] ?? ''
    setMasterValues(master)
    setSortedIndices(null)
    setDetailRows(recordQuery.data.details.map(detail => {
      const row: Record<string, string> = {}
      for (const field of formQuery.data?.detailFields ?? []) {
        if (!field.isVisible) continue
        // 复制时：主表主键关联列（服务端从主表带入）与 CAN_COPY=0 字段不带出
        if (isCopy && (formQuery.data.masterPkOrder.some(pk => pk.toLowerCase() === field.key.toLowerCase()) || !field.canCopy)) {
          row[field.key] = ''
          continue
        }
        row[field.key] = detail[field.key] == null ? '' : String(detail[field.key])
      }
      return row
    }))
  }, [formQuery.data, recordQuery.data, isCopy])

  // 未保存改动登记给外壳：关闭标签、离开当前标签时的确认与保存由外壳统一处理
  useTabDirty(dirty, {
    save: async () => {
      if (!validateClient()) throw new Error('表单校验未通过。')
      await save.mutateAsync()
    },
    discard: () => setDirty(false),
  })

  const save = useMutation({
    mutationFn: async () => {
      if (!formQuery.data) throw new Error('表单定义未加载。')
      // Normalize decimal values before submit: strip thousands separators and full-width digits
      const toSubmit = (field: FormFieldDefinition): string => {
        const raw = masterValues[field.key] ?? ''
        return fieldVariant(field) === 'decimal' ? canonicalizeDecimalValue(raw) : raw.trim()
      }
      const values: Record<string, string> = {}
      for (const field of writableFields(formQuery.data.masterFields)) values[field.key] = toSubmit(field)
      const details = detailRows.map(row => {
        const detail: Record<string, string> = {}
        for (const field of writableFields(formQuery.data?.detailFields ?? [])) {
          const raw = row[field.key] ?? ''
          detail[field.key] = fieldVariant(field) === 'decimal' ? canonicalizeDecimalValue(raw) : raw.trim()
        }
        return detail
      })
      const body: SaveRecordRequest = { values, details, idempotencyKey: idempotencyRef.current }
      const chooserSources = Object.keys(masterChooserSourcesRef.current).length > 0
        ? { ...masterChooserSourcesRef.current }
        : null
      const detailChooserSources = detailRows.map(row => readDetailChooserSources(row))
      // 明细项次是行身份：把各行**原有的**项次回传（新行没有则给 null），
      // 服务端据此保留既有号、只给新行分配未占用的号——删行不再让其余行静默改号。
      const detailSerials = detailRows.map(row => (row.SERIAL_NO && String(row.SERIAL_NO).trim()) || null)
      // 只在本会话确实选过来源时才下发，未重选的字段保持服务端既有记忆
      if (chooserSources) body.chooserSources = chooserSources
      if (detailChooserSources.some(item => item !== null)) body.detailChooserSources = detailChooserSources
      if (detailSerials.some(item => item !== null)) body.detailSerials = detailSerials
      if (isEdit) {
        body.original = originalRef.current
        const key = buildKey(formQuery.data, masterValues)
        return apiClient.put<RecordSaveResponse>(`/document-workbench/${moduleId}/record?key=${encodeURIComponent(JSON.stringify(key))}`, body)
      }
      return apiClient.post<RecordSaveResponse>(`/document-workbench/${moduleId}/record`, body)
    },
    onSuccess: async response => {
      setDirty(false)
      idempotencyRef.current = newIdempotencyKey()
      await queryClient.invalidateQueries({ queryKey: ['workbench', moduleId] })
      // After save, enter browse mode. Key comes from the server (authoritative); the preview bill
      // number from auto-numbering may differ from the final number.
      const key = response?.key?.length ? response.key : buildKey(formQuery.data!, masterValues)
      navigate(workbenchView(moduleId, key),
        { state: { warnings: response?.warnings ?? null } satisfies ViewNavState })
    },
    onError: cause => {
      if (cause instanceof ApiError && cause.status === 400 && Array.isArray(cause.body.fieldErrors) && cause.body.fieldErrors.length > 0) {
        const masterKeys = new Set((formQuery.data?.masterFields ?? []).map(field => field.key))
        const master: FieldErrors = {}
        const detail: FieldErrors = {}
        for (const error of cause.body.fieldErrors) {
          if (!error.field) continue
          if (masterKeys.has(error.field)) master[error.field] = error.message ?? '校验失败。'
          else detail[error.field] = error.message ?? '校验失败。'
        }
        setFieldErrors(master)
        setDetailErrors(Object.keys(detail).length > 0 ? [detail] : [])
        setSaveError(`数据校验未通过：${summarizeFieldErrors(master, [detail])}`)
        return
      }
      setSaveError(cause instanceof Error ? cause.message : '保存失败。')
    },
  })

  const workflow = useMutation({
    mutationFn: async ({ action, message }: { action: 'approve' | 'deapprove'; message?: string }) => {
      if (!formQuery.data) throw new Error('表单定义未加载。')
      const key = buildKey(formQuery.data, masterValues)
      return apiClient.post<RecordSaveResponse>(`/document-workbench/${moduleId}/${action}`, {
        key: JSON.stringify(key),
        idempotencyKey: newIdempotencyKey(),
        message: message?.trim() || undefined,
      })
    },
    onSuccess: async (_, { action }) => {
      window.alert(action === 'approve' ? (form.hasWorkflow ? '已送审，单据进入审批链。' : '批核成功。') : '解批成功。')
      setApproveOpen(false)
      setSubmitMessage('')
      await recordQuery.refetch()
    },
    onError: cause => {
      const message = describeApiError(cause, '操作失败，请稍后重试。')
      window.alert(message)
    },
  })

  // 送审意见弹窗 + 审批历史（流程信息）。审批历史沿用 /workflow/{moduleId}/history 时间线端点。
  const [approveOpen, setApproveOpen] = useState(false)
  const [submitMessage, setSubmitMessage] = useState('')
  const [historyOpen, setHistoryOpen] = useState(false)
  const historyQuery = useQuery({
    queryKey: ['workflow-history', moduleId, keyParam],
    queryFn: () => apiClient.get<{ rows: WorkflowHistoryRow[] }>(`/workflow/${moduleId}/history?key=${encodeURIComponent(keyParam ?? '')}`),
    enabled: historyOpen && Boolean(keyParam),
  })
  const openApprove = () => { setSubmitMessage(''); setApproveOpen(true) }

  const finish = useMutation({
    mutationFn: async (action: 'endcase' | 'unendcase') => {
      if (!formQuery.data) throw new Error('表单定义未加载。')
      const key = buildKey(formQuery.data, masterValues)
      return apiClient.post<RecordSaveResponse>(`/document-workbench/${moduleId}/${action}`, { key: JSON.stringify(key), idempotencyKey: newIdempotencyKey() })
    },
    onSuccess: async (_, action) => {
      window.alert(action === 'endcase' ? '结案成功。' : '取消结案成功。')
      await recordQuery.refetch()
    },
    onError: cause => {
      const message = describeApiError(cause, '操作失败，请稍后重试。')
      window.alert(message)
    },
  })

  const deleteRecord = async () => {
    // 键取 URL 路径主键（keyParam，同步权威）：masterValues 为异步回填，record 未返回时点击会得到空键
    if (!keyParam) return
    if (!window.confirm('确定删除该单据吗？删除后不可恢复。')) return
    try {
      await apiClient.delete(`/document-workbench/${moduleId}/record?key=${encodeURIComponent(keyParam)}`, { headers: { 'X-Idempotency-Key': newIdempotencyKey() } })
      window.alert('删除成功。')
      // 删除后沿进入浏览态时的列表顺序定向：下一条；被删的是最后一条则回到第一条；
      // 无剩余记录或顺序不可用则返回工作台列表。按当前主键值定位，不轻信传入的 navIndex
      //（顺序可能过期；定错会导航回被删记录自身、同 URL 跳转即页面纹丝不动）。
      // 定长字符主键（nchar）尾空格在拼 URL 时已修剪，比较时同样忽略尾空格。
      let remaining: string[][] | null = null
      let targetIndex = 0
      if (navKeys && navKeys.length > 0) {
        let currentKey: string[] | null = null
        try { currentKey = JSON.parse(keyParam) as string[] } catch { currentKey = null }
        const sameKey = (a: string[], b: string[]) =>
          a.length === b.length && a.every((value, i) => value.trimEnd() === b[i].trimEnd())
        const found = currentKey ? navKeys.findIndex(candidate => sameKey(candidate, currentKey!)) : -1
        if (found >= 0) {
          remaining = navKeys.filter(candidate => !sameKey(candidate, currentKey!))
          targetIndex = found < remaining.length ? found : 0
        }
      }
      // 先跳转，再做缓存清理：被删记录若参与整体失效，其重取 404 会走默认 3 次重试退避，
      // 等它完成会把导航拖住数秒（看起来像“删完没跳转”），故导航不同步等待后台刷新。
      if (remaining && remaining.length > 0) {
        navigate(workbenchView(moduleId, remaining[targetIndex]),
          { state: { navKeys: remaining, navIndex: targetIndex } })
      } else {
        // 无列表顺序上下文（如直接地址进入）或顺序已对不上：回到列表，避免停留在已删除记录的空白浏览态
        navigate(fromModuleId ? workbenchList(fromModuleId) : workbenchList(moduleId))
      }
      // 被删记录的缓存查询直接移除（不再重取）；列表/定义后台失效刷新，不阻塞导航
      queryClient.removeQueries({ queryKey: ['workbench', moduleId, 'record', keyParam] })
      void queryClient.invalidateQueries({ queryKey: ['workbench', moduleId] })
    } catch (cause) {
      window.alert(cause instanceof Error ? `删除失败：${cause.message}` : '删除失败。')
    }
  }

  // 发起人撤回在途流程（v2.1）：表单浏览态在途时显示「撤回」，撤回后单据可编辑并重新送审
  const withdraw = useMutation({
    mutationFn: async () => {
      if (!formQuery.data) throw new Error('表单定义未加载。')
      const key = buildKey(formQuery.data, masterValues)
      return apiClient.post<{ message?: string }>('/workflow/withdraw', { moduleId, key })
    },
    onSuccess: async (response) => {
      window.alert(response.message ?? '流程已撤回，单据可修改后重新提交。')
      await recordQuery.refetch()
    },
    onError: cause => {
      const message = describeApiError(cause, '撤回失败，请稍后重试。')
      window.alert(message)
    },
  })

  const validateClient = (): boolean => {
    if (!formQuery.data) return false
    const master = validateMasterFields(formQuery.data.masterFields, masterValues)
    const details = validateDetailRows(formQuery.data.detailFields, detailRows, formQuery.data.detailDfVerify)
    setFieldErrors(master)
    setDetailErrors(details)
    const hasErrors = Object.keys(master).length > 0 || details.some(row => Object.keys(row).length > 0)
    if (hasErrors) {
      setSaveError(`数据校验未通过：${summarizeFieldErrors(master, details)}`)
      focusFirstError(formQuery.data.masterFields, master, details)
    } else setSaveError(null)
    return !hasErrors
  }

  /**
   * 错误 UX：提交校验失败时自动切换到首个错误所在页签，
   * 滚动并聚焦首个错误控件；明细错误滚动到明细网格首错单元格。
   */
  const focusFirstError = (fields: FormFieldDefinition[], master: FieldErrors, details: FieldErrors[]) => {
    const firstMaster = fields.find(field => field.isVisible && master[field.key])
    const firstDetailRow = details.find(row => Object.keys(row).length > 0)
    const detailKey = Object.keys(firstDetailRow ?? {})[0]
    if (firstMaster && form.tabs.length > 0 && firstMaster.tabNo !== activeTab) setActiveTab(firstMaster.tabNo)
    const targetKey = firstMaster?.key ?? detailKey
    if (!targetKey) return
    const scope = firstMaster ? '.erp-form-card' : '.erp-detail-card'
    window.setTimeout(() => {
      const selector = ['input', 'select', 'textarea']
        .map(tag => `${scope} [data-field-key="${CSS.escape(targetKey)}"] ${tag}`)
        .join(',')
      const element = document.querySelector(selector) as HTMLElement | null
      // jsdom 无 scrollIntoView 实现：可选调用，真实浏览器中滚动并聚焦
      element?.scrollIntoView?.({ block: 'center', behavior: 'smooth' })
      element?.focus({ preventScroll: true })
    }, 60)
  }

  const back = () => {
    // 跨模块关联浏览：返回来源工作台列表；常规浏览/编辑返回当前模块列表
    navigate(fromModuleId ? workbenchList(fromModuleId) : workbenchList(moduleId))
  }

  // Ctrl+S to save (edit/new mode): listener attached once; latest closure kept via ref
  const saveHotkeyRef = useRef<() => void>(() => undefined)
  saveHotkeyRef.current = () => {
    if (save.isPending) return
    if (validateClient()) save.mutate()
  }
  useEffect(() => {
    if (isView) return
    const handler = (event: KeyboardEvent) => {
      if (!(event.ctrlKey || event.metaKey) || event.key.toLowerCase() !== 's') return
      event.preventDefault()
      saveHotkeyRef.current()
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [isView])

  const openPrint = () => {
    if (!formQuery.data) return
    const key = buildKey(formQuery.data, masterValues)
    window.open(`/print/${moduleId}?key=${encodeURIComponent(JSON.stringify(key))}`, '_blank')
  }

  const applyChooser = (field: FormFieldDefinition, row: UnifiedChooserRow) => {
    const source = field.choosers.find(item => item.active && item.table && item.serialNo === chooserSerial)
      ?? field.choosers.find(item => item.active && item.table)
    // 记住这次选的来源（多来源字段回显时按它解析同组伴生字段）
    if (source?.serialNo != null) masterChooserSourcesRef.current[field.key] = source.serialNo
    const mapping = parseReturnItems(source?.returnMapping)
    if (mapping.length > 0) {
      setMasterValues(current => {
        const next = { ...current }
        for (const item of mapping) {
          if (row[item.column] === undefined) continue
          next[item.target] = String(row[item.column] ?? '')
        }
        return next
      })
      setDirty(true)
    }
    setChooserField(null)
  }

  /** 主表字段值更新（稳定回调，供 memo 化主表网格使用） */
  const changeMasterValue = useCallback((key: string, value: string) => {
    setMasterValues(current => ({ ...current, [key]: value }))
    setFieldErrors(current => {
      const next = { ...current }
      delete next[key]
      return next
    })
    setDirty(true)
  }, [])

  /** 主表是否含金额汇总列：无则明细编辑无需同步预览（避免无意义的状态更新拖累整页渲染） */
  const hasMasterAmountColumns = useMemo(
    () => (formQuery.data?.masterFields ?? []).some(field => AMOUNT_COLUMN_KEYS.has(field.key.toUpperCase())),
    [formQuery.data],
  )
  const formDefRef = useRef<FormDefinition | null>(null)
  formDefRef.current = formQuery.data ?? null
  const hasMasterAmountColumnsRef = useRef(false)
  hasMasterAmountColumnsRef.current = hasMasterAmountColumns
  const selectedDetailRowsRef = useRef<Set<number>>(new Set())
  selectedDetailRowsRef.current = selectedDetailRows

  /** 主表金额汇总预览（明细 SUM，保存后服务端权威聚合覆盖；结果不变时跳过 setState 避免无意义渲染） */
  const syncMasterPreviewRows = useCallback((rows: Record<string, string>[]) => {
    const def = formDefRef.current
    if (!def || !hasMasterAmountColumnsRef.current) return
    const patch = previewMasterAmounts(def.masterFields, rows)
    const current = masterValuesRef.current
    let changed = false
    for (const key of Object.keys(patch)) {
      if (current[key] !== patch[key]) {
        changed = true
        break
      }
    }
    if (!changed) return
    setMasterValues(currentState => ({ ...currentState, ...patch }))
  }, [])

  /** 明细行值更新（稳定回调）：合并变更、金额联动预览、清除行内错误，其余行对象保持不变 */
  const updateDetailRowValues = useCallback((index: number, patch: Record<string, string>) => {
    const rows = detailRowsRef.current
    const row = rows[index]
    if (!row) return
    const updated = { ...row, ...patch }
    const nextRows = rows.slice()
    const def = formDefRef.current
    // 金额联动：QTY/PRICE/税率/税型/折扣变更时重算该行金额（服务端保存时权威复算）
    const amountPatch: Partial<Record<string, string>> | null = def && Object.keys(patch).some(key => AMOUNT_TRIGGER_KEYS.has(key.toUpperCase()))
      ? previewDetailAmount(def.detailFields, updated, masterValuesRef.current)
      : null
    nextRows[index] = amountPatch ? { ...updated, ...amountPatch } as Record<string, string> : updated
    detailRowsRef.current = nextRows
    setDetailRows(nextRows)
    syncMasterPreviewRows(nextRows)
    const touched = Object.keys(patch)
    setDetailErrors(current => {
      const rowErrors = current[index]
      if (!rowErrors || !touched.some(key => key in rowErrors)) return current
      const nextRow = { ...rowErrors }
      for (const key of touched) delete nextRow[key]
      const copy = current.slice()
      copy[index] = nextRow
      return copy
    })
    setDirty(true)
  }, [syncMasterPreviewRows])

  const updateDetailField = useCallback((index: number, key: string, value: string) => {
    updateDetailRowValues(index, { [key]: value })
  }, [updateDetailRowValues])

  /** 明细列宽拖拽：防抖批量保存到服务端（FIELDS.DISPLAY_LENGTH，与工作台 column-widths 一致）。 */
  const saveDetailWidth = useCallback((fieldKey: string, width: number) => {
    if (!formDefRef.current) return
    widthBatch.current[fieldKey] = width
    if (widthTimer.current != null) window.clearTimeout(widthTimer.current)
    widthTimer.current = window.setTimeout(() => {
      const detail = widthBatch.current
      widthBatch.current = {}
      void apiClient.put<void>(`/document-workbench/${moduleId}/column-widths`, { master: {}, detail }).catch(() => {})
    }, 300)
  }, [moduleId])

  const addDetailRow = useCallback(() => {
    const def = formDefRef.current
    if (!def) return
    const missing = def.detailNoFields
      .split(';')
      .map(field => field.trim())
      .filter(field => field && !(masterValuesRef.current[field] ?? '').trim())
    if (missing.length > 0) {
      setSaveError(`请先填写主表字段：${missing.join('、')}，再新增明细。`)
      return
    }
    // 主表同名值自动带入新明细行
    const empty: Record<string, string> = {}
    for (const field of def.detailFields) {
      if (field.isVisible) empty[field.key] = masterValuesRef.current[field.key] ?? emptyValue(field)
    }
    const currentRows = detailRowsRef.current
    const nextRows = [...currentRows, empty]
    detailRowsRef.current = nextRows
    setDetailRows(nextRows)
    // 快照视图：新行追加到视图末尾
    setSortedIndices(current => current ? [...current, currentRows.length] : current)
    syncMasterPreviewRows(nextRows)
    setDetailErrors(current => [...current, {}])
    setDirty(true)
  }, [syncMasterPreviewRows])

  const applyDetailChooser = (index: number, field: FormFieldDefinition, rows: UnifiedChooserRow[]) => {
    const source = field.choosers.find(item => item.active && item.table && item.serialNo === detailChooserSerial)
      ?? field.choosers.find(item => item.active && item.table)
    const mapping = parseReturnItems(source?.returnMapping)
    const mappedOf = (target: Record<string, string>, row: UnifiedChooserRow) => {
      for (const item of mapping) {
        if (row[item.column] === undefined) continue
        target[item.target] = String(row[item.column] ?? '')
      }
      return target
    }
    // 明细多选：逐条追加明细行
    if (field.chooseMultiple && rows.length > 1) {
      const currentRows = detailRowsRef.current
      const def = formDefRef.current
      const newRows = rows.map(row => {
        const empty: Record<string, string> = {}
        for (const fieldDef of def?.detailFields ?? []) {
          if (fieldDef.isVisible) empty[fieldDef.key] = masterValuesRef.current[fieldDef.key] ?? emptyValue(fieldDef)
        }
        return mappedOf(empty, row)
      })
      const mergedRows = [...currentRows, ...newRows.map(row => withDetailChooserSource(row, field.key, source?.serialNo))]
      detailRowsRef.current = mergedRows
      setDetailRows(mergedRows)
      // 快照视图：批量追加的行按序追加到视图末尾
      setSortedIndices(current => current ? [...current, ...newRows.map((_, offset) => currentRows.length + offset)] : current)
      syncMasterPreviewRows(mergedRows)
      setDetailErrors(current => [...current, ...newRows.map(() => ({}))])
      setDirty(true)
      setDetailChooser(null)
      return
    }
    const row = rows[0]
    if (mapping.length > 0) {
      const patch: Record<string, string> = {}
      for (const item of mapping) {
        if (row[item.column] === undefined) continue
        patch[item.target] = String(row[item.column] ?? '')
      }
      updateDetailRowValues(index, withDetailChooserSource(patch, field.key, source?.serialNo))
    }
    setDetailChooser(null)
  }

  const removeDetailRow = useCallback((index: number) => {
    const rows = detailRowsRef.current
    const nextRows = rows.filter((_, i) => i !== index)
    detailRowsRef.current = nextRows
    setDetailRows(nextRows)
    // 快照视图：移除对应行并把大于它的行号前移
    setSortedIndices(current => current ? current.filter(i => i !== index).map(i => (i > index ? i - 1 : i)) : current)
    syncMasterPreviewRows(nextRows)
    setDetailErrors(current => current.filter((_, i) => i !== index))
    setSelectedDetailRows(current => new Set([...current].filter(i => i !== index).map(i => i > index ? i - 1 : i)))
    setDirty(true)
  }, [syncMasterPreviewRows])

  const removeSelectedDetailRows = useCallback(() => {
    const removed = selectedDetailRowsRef.current
    const rows = detailRowsRef.current
    const nextRows = rows.filter((_, index) => !removed.has(index))
    detailRowsRef.current = nextRows
    setDetailRows(nextRows)
    // 快照视图：移除所选行，剩余行按「被删行数」前移
    setSortedIndices(current => current
      ? current.filter(i => !removed.has(i)).map(i => i - [...removed].filter(r => r < i).length)
      : current)
    syncMasterPreviewRows(nextRows)
    setDetailErrors(current => current.filter((_, index) => !removed.has(index)))
    setSelectedDetailRows(new Set())
    setDirty(true)
  }, [syncMasterPreviewRows])

  /** 多来源「各是各的入口」：1 个直接打开，多个先弹来源菜单。 */
  const openChooser = useCallback((field: FormFieldDefinition, kind: 'master' | 'detail', detailIndex?: number) => {
    const sources = field.choosers.filter(item => item.active && item.table)
    if (sources.length === 0) return
    if (sources.length === 1) {
      if (kind === 'master') {
        setChooserField(field)
        setChooserSerial(sources[0].serialNo)
      } else {
        setDetailChooser({ index: detailIndex!, field })
        setDetailChooserSerial(sources[0].serialNo)
      }
      return
    }
    setSourceMenu(kind === 'master' ? { kind, field } : { kind, index: detailIndex!, field })
  }, [])
  const openMasterChooser = useCallback((field: FormFieldDefinition) => openChooser(field, 'master'), [openChooser])
  const openDetailChooser = useCallback((index: number, field: FormFieldDefinition) => openChooser(field, 'detail', index), [openChooser])
  const openFieldSetup = useCallback((field: FormFieldDefinition, x: number, y: number) => setFieldSetupMenu({ field, x, y }), [])

  /** 明细视图排序：切换排序/清空时重算一次快照，编辑中保持行位置稳定（避免打字跳行）。 */
  const handleDetailSortingChange = useCallback((next: SortingState) => {
    const sort = next[0]
    if (!sort) {
      setDetailSort(null)
      setSortedIndices(null)
      return
    }
    const key = sort.id
    const dir = sort.desc ? -1 : 1
    setDetailSort({ key, dir })
    setSortedIndices(sortDetailIndices(detailRowsRef.current, key, dir))
  }, [])

  const handleDetailRowSelectionChange = useCallback((next: RowSelectionState) => {
    setSelectedDetailRows(new Set(
      Object.keys(next)
        .filter(id => next[id] && id.startsWith('r'))
        .map(id => Number(id.slice(1))),
    ))
  }, [])

  if (formQuery.isPending || (isEdit && recordQuery.isPending)) return <LoadingState label="正在加载表单…" />
  if (formQuery.isError) return <section className="card"><div className="card-body text-center py-5">{describeError(formQuery.error)}</div></section>
  if (isEdit && recordQuery.isError) return <section className="card"><div className="card-body text-center py-5">{describeError(recordQuery.error)}</div></section>
  // 浏览态记录加载失败（如记录已被删除）：不渲染陈旧空白表单，给出明确错误与返回入口
  if (isView && recordQuery.isError) {
    return (
      <section className="card">
        <div className="card-body text-center py-5 d-flex flex-column gap-3 align-items-center">
          <span>{describeError(recordQuery.error)}</span>
          <span>
            <button type="button" className="btn btn-secondary me-2" onClick={() => void recordQuery.refetch()}>重新加载</button>
            <button type="button" className="btn btn-primary" onClick={back}>返回列表</button>
          </span>
        </div>
      </section>
    )
  }

  const form = formQuery.data
  if (!form) return null
  // 明细走金额汇总（明细表有 AMOUNT 列）时，主表金额列强制只读展示（保存后服务端权威聚合）
  const masterAmountLocked = form.detailFields.some(field => field.key.toUpperCase() === 'AMOUNT')
  const hasTabs = form.tabs.length > 0
  const activeTabNo = hasTabs ? activeTab : 1
  // Tab error badges: hidden tabs' errors shown as count badges
  const tabErrorCounts = new Map<number, number>()
  for (const field of form.masterFields) {
    if (field.isVisible && fieldErrors[field.key]) tabErrorCounts.set(field.tabNo, (tabErrorCounts.get(field.tabNo) ?? 0) + 1)
  }

  // 自定义按钮（单据操作）：只在浏览态出现，界面有未保存改动时禁用（操作作用于已落库的单据状态）。
  const masterActionItems = (form.userActions ?? []).filter(action => action.placement !== 'detail')
  const detailActionItems = (form.userActions ?? []).filter(action => action.placement === 'detail')

  return (
    <div className="d-flex flex-column erp-form-page">
      {saveError ? <div className="alert alert-danger mb-0">{saveError}</div> : null}
      {warnings && warnings.length > 0 ? (
        <div className="alert alert-warning mb-0 d-flex justify-content-between align-items-center" role="alert">
          <span>{warnings.map(warning => warning.message).join('；')}</span>
          <button type="button" className="btn-close" aria-label="关闭" onClick={() => setWarningsDismissed(true)} />
        </div>
      ) : null}
      <section className="card erp-form-card">
        <div className="card-body">
          <div className="erp-form-toolbar">
            {!isView ? (
              <>
                {isCopy && <span className="small text-secondary align-self-center">复制模式：以选中记录为模板，保存后生成新单据</span>}
                <ErpCommandBar items={[
                  { action: 'save', label: '保存', variant: 'primary', loading: save.isPending, onClick: () => { if (validateClient()) save.mutate() } },
                  { action: 'cancel', label: '取消', onClick: back },
                ]} />
                {form.canFileView && keyParam && (
                  <ErpCommandBar items={[{ action: 'attach', visible: true, onClick: () => setAttachOpen(true) }]} />
                )}
              </>
            ) : (
              // Browse-mode toolbar (fixed order, not affected by FORM_BUTTONS config):
              // back/prev/next/new(primary)/copy/edit/delete/approve|deapprove/history/close|unclose/attach/print/help
              <ErpCommandBar items={(() => {
                const currentKey = buildKey(form, masterValues)
                const master = recordQuery.data?.master
                // Document status (server-returned): CONFIRM_TAG / FINISHED_TAG
                const isConfirmed = master?.CONFIRM_TAG === true
                const isFinished = master?.FINISHED_TAG === true
                // A3：在途流程状态（WF_MONITOR.WF_STATE='0'）——流程审批中的单据禁止编辑/删除，批核改显示撤回
                const flowInProgress = recordQuery.data?.flowState === 'InProgress'
                // 已结案：解批/编辑/删除禁用；已审批：批核/编辑/删除禁用；在途流程：编辑/删除禁用（按钮禁用而非隐藏，  语义）
                const editDisabled = isFinished || isConfirmed || flowInProgress
                const deleteDisabled = isFinished || isConfirmed || flowInProgress
                const whitelistItems = buildViewToolbarItems(form, { master, isConfirmed, isFinished, flowInProgress, keyParam }, {
                  openApprove: () => openApprove(),
                  deapprove: () => workflow.mutate({ action: 'deapprove' }),
                  endcase: () => finish.mutate('endcase'),
                  unendcase: () => finish.mutate('unendcase'),
                  openPrint,
                  workflowPending: workflow.isPending,
                  finishPending: finish.isPending,
                })
                return [
                  { action: 'back', onClick: back },
                  ...(navKeys && navIndex != null ? [
                    { action: 'prior', disabled: navIndex <= 0, onClick: () => goNeighbor(-1) },
                    { action: 'next', disabled: navIndex >= navKeys.length - 1, onClick: () => goNeighbor(1) },
                  ] satisfies ErpCommandItem[] : []),
                  ...(form.canAddNew && form.hasAdd
                    ? [{ action: 'new', variant: 'primary', onClick: () => navigate(workbenchNew(moduleId)) } satisfies ErpCommandItem]
                    : []),
                  ...(form.ifCopy && form.canAddNew && keyParam
                    ? [{ action: 'copy', onClick: () => navigate(workbenchCopy(moduleId, currentKey)) } satisfies ErpCommandItem]
                    : []),
                  ...(form.canEdit && form.hasEdit && keyParam
                    ? [{ action: 'edit', disabled: editDisabled, onClick: () => navigate(workbenchEdit(moduleId, currentKey)) } satisfies ErpCommandItem]
                    : []),
                  // 删除为浏览态标准动作：权限 canDelete ∧ 单据状态，不依赖 FORM_BUTTONS 配置
                  ...(form.canDelete && keyParam
                    ? [{ action: 'delete', variant: 'danger', disabled: deleteDisabled, onClick: () => void deleteRecord() } satisfies ErpCommandItem]
                    : []),
                  ...whitelistItems.filter(item => item.action === 'approve' || item.action === 'deapprove'),
                  // A3：在途流程时显示「撤回」（发起人），撤回后可编辑并重新送审——与批核同位互斥
                  ...(flowInProgress && keyParam
                    ? [{ action: 'withdraw', loading: withdraw.isPending, onClick: () => withdraw.mutate() } satisfies ErpCommandItem]
                    : []),
                  // 审批历史（流程信息）——流程模块浏览态显示
                  ...(form.hasWorkflow && keyParam
                    ? [{ action: 'history', onClick: () => setHistoryOpen(true) } satisfies ErpCommandItem]
                    : []),
                  ...whitelistItems.filter(item => item.action === 'endcase' || item.action === 'unendcase'),
                  ...(form.canFileView && keyParam ? [{ action: 'attach', onClick: () => setAttachOpen(true) } satisfies ErpCommandItem] : []),
                  ...whitelistItems.filter(item => item.action === 'print'),
                  ...(form.helpUrl ? [{ action: 'help', onClick: () => window.open(form.helpUrl!, '_blank', 'noopener') } satisfies ErpCommandItem] : []),
                  // 自定义按钮（单据级）固定排在标准动作之后，顺序由配置的 SEQ 决定
                  //（配置只决定动作有无与相对顺序，不改变标准动作的固定位置）。
                  ...masterActionItems.map(action => ({
                    action: `doc-action:${action.key}`,
                    icon: <IconPlayerPlay size={16} />,
                    title: documentAction.canRun ? action.label : '界面有未保存改动，请先保存',
                    label: action.label,
                    disabled: !documentAction.canRun,
                    loading: documentAction.busyKey === action.key,
                    onClick: () => void documentAction.run(action),
                  } satisfies ErpCommandItem)),
                ]
              })()} />
            )}
          </div>
          {hasTabs ? (
            <ul className="nav nav-tabs erp-form-tabs">
              {form.tabs.map(tab => {
                const errorCount = tabErrorCounts.get(tab.no) ?? 0
                return (
                  <li className="nav-item" key={tab.no}>
                    <button type="button" className={`nav-link${activeTabNo === tab.no ? ' active' : ''}`} onClick={() => setActiveTab(tab.no)}>
                      {tab.title}
                      {errorCount > 0 ? <span className="erp-tab-error-badge">{errorCount}</span> : null}
                    </button>
                  </li>
                )
              })}
            </ul>
          ) : null}
          <MasterFormGrid
            form={form}
            activeTabNo={activeTabNo}
            hasTabs={hasTabs}
            masterValues={masterValues}
            fieldErrors={fieldErrors}
            viewing={isView}
            canSetup={form.canSetup}
            masterAmountLocked={masterAmountLocked}
            onFieldChange={changeMasterValue}
            onOpenChooser={openMasterChooser}
            onFieldSetup={openFieldSetup}
          />
        </div>
      </section>
      {form.detailFields.length > 0 ? (
        <DetailFormGrid
          form={form}
          detailRows={detailRows}
          detailErrors={detailErrors}
          sortedIndices={sortedIndices}
          detailSort={detailSort}
          selectedDetailRows={selectedDetailRows}
          viewing={isView}
          actions={isView && detailActionItems.length > 0 ? (
            <div className="d-flex gap-1">
              {detailActionItems.map(action => (
                <Button
                  key={action.key}
                  size="sm"
                  icon={<IconPlayerPlay size={14} />}
                  disabled={!documentAction.canRun}
                  title={documentAction.canRun ? action.label : '界面有未保存改动，请先保存'}
                  onClick={() => void documentAction.run(action)}
                >
                  {action.label}
                </Button>
              ))}
            </div>
          ) : null}
          storageKey={`form-detail-${moduleId}`}
          onAddRow={addDetailRow}
          onRemoveRow={removeDetailRow}
          onRemoveSelected={removeSelectedDetailRows}
          onFieldChange={updateDetailField}
          onChoose={openDetailChooser}
          onSortChange={handleDetailSortingChange}
          onSelectionChange={handleDetailRowSelectionChange}
          onResize={saveDetailWidth}
        />
      ) : null}
      {chooserField ? (
        <UnifiedChooser
          open
          title={chooserTitle(chooserField)}
          source={{ kind: 'formField', moduleId, fieldKey: chooserField.key, serialNo: chooserSerial }}
          mode={chooserField.chooseMultiple ? 'multi' : 'single'}
          masterValues={masterValues}
          onPick={rows => applyChooser(chooserField, rows[0])}
          onClose={() => { setChooserField(null); setChooserSerial(null) }}
          resizable
          storageKey={`chooser-${moduleId}-${chooserField.key}`}
          emptyText="没有可选数据。"
        />
      ) : null}
      {detailChooser ? (
        <UnifiedChooser
          open
          title={chooserTitle(detailChooser.field)}
          source={{ kind: 'formField', moduleId, fieldKey: detailChooser.field.key, serialNo: detailChooserSerial }}
          mode={detailChooser.field.chooseMultiple ? 'multi' : 'single'}
          masterValues={masterValues}
          detailValues={detailRows[detailChooser.index] ?? undefined}
          onPick={rows => applyDetailChooser(detailChooser.index, detailChooser.field, rows)}
          onClose={() => { setDetailChooser(null); setDetailChooserSerial(null) }}
          resizable
          storageKey={`chooser-${moduleId}-${detailChooser.field.key}`}
          emptyText="没有可选数据。"
        />
      ) : null}
      {sourceMenu ? (
        <div className="modal show d-block" tabIndex={-1} role="dialog" onClick={() => setSourceMenu(null)}>
          <div className="modal-dialog modal-sm modal-dialog-centered" role="document" onClick={event => event.stopPropagation()}>
            <div className="modal-content">
              <div className="modal-header">
                <h5 className="modal-title">选择数据来源：{sourceMenu.field.label}</h5>
                <button type="button" className="btn-close" aria-label="关闭" onClick={() => setSourceMenu(null)} />
              </div>
              <div className="modal-body d-flex flex-column gap-1">
                {sourceMenu.field.choosers.filter(item => item.active && item.table).map(source => (
                  <button
                    key={source.serialNo ?? source.table}
                    type="button"
                    className="btn btn-outline-secondary text-start"
                    onClick={() => {
                      if (sourceMenu.kind === 'master') {
                        setChooserField(sourceMenu.field)
                        setChooserSerial(source.serialNo)
                      } else {
                        setDetailChooser({ index: sourceMenu.index, field: sourceMenu.field })
                        setDetailChooserSerial(source.serialNo)
                      }
                      setSourceMenu(null)
                    }}
                  >
                    {source.description || source.table}
                  </button>
                ))}
              </div>
            </div>
          </div>
        </div>
      ) : null}
      {attachOpen && keyParam && (
        <AttachmentDialog
          moduleId={Number(moduleId)}
          masterTable={form.masterTable}
          recordKey={buildKey(form, masterValues)}
          title={form.title}
          canUpload={form.canFileUpda}
          canEdit={form.canFileEdit}
          canDelete={form.canFileDele}
          onClose={() => setAttachOpen(false)}
        />
      )}
      {approveOpen && (
        <Modal
          title="送审确认"
          onClose={() => setApproveOpen(false)}
          dialogClassName="erp-dialog-sm"
          ariaLabel="送审确认"
          footer={<>
            <Button variant="secondary" onClick={() => setApproveOpen(false)}>取消</Button>
            <Button variant="primary" loading={workflow.isPending} onClick={() => workflow.mutate({ action: 'approve', message: submitMessage })}>
              确认送审
            </Button>
          </>}
        >
          <div className="mb-2 text-secondary small">
            单据将进入审批链，审批完成前不可编辑/删除。可填写送审说明（选填）。
          </div>
          <input
            className="form-control"
            value={submitMessage}
            onChange={(event) => setSubmitMessage(event.target.value)}
            placeholder="送审说明…"
            aria-label="送审说明"
          />
        </Modal>
      )}
      {historyOpen && keyParam && (
        <Modal
          title="审批历史（流程信息）"
          onClose={() => setHistoryOpen(false)}
          ariaLabel="审批历史"
          bodyStyle={{ maxHeight: '60dvh', overflowY: 'auto' }}
          footer={<Button variant="secondary" onClick={() => setHistoryOpen(false)}>关闭</Button>}
        >
                {historyQuery.isPending ? <LoadingState label="正在加载审批历史…" /> : historyQuery.isError ? (
                  <ErrorState message="审批历史加载失败" onRetry={() => void historyQuery.refetch()} />
                ) : (historyQuery.data?.rows ?? []).length === 0 ? (
                  <div className="text-center text-secondary py-4">暂无审批记录</div>
                ) : (
                  <WorkflowTimeline rows={(historyQuery.data?.rows ?? []) as WorkflowTimelineRow[]} emptyText="暂无审批记录" />
                )}
        </Modal>
      )}
      {fieldSetupMenu ? (
        <div
          className="erp-field-setup-overlay"
          onClick={() => setFieldSetupMenu(null)}
          onContextMenu={event => { event.preventDefault(); setFieldSetupMenu(null) }}
        >
          <div
            className="erp-field-setup-menu"
            role="menu"
            style={{ left: fieldSetupMenu.x, top: fieldSetupMenu.y }}
            onClick={event => event.stopPropagation()}
          >
            <div className="erp-field-setup-header">{fieldSetupMenu.field.label}</div>
            <button
              type="button"
              className="erp-field-setup-item"
              role="menuitem"
              onClick={() => {
                navigate(`/admin/fields/${encodeURIComponent(form.masterTable)}/${encodeURIComponent(fieldSetupMenu.field.key)}?moduleId=${moduleId}`)
                setFieldSetupMenu(null)
              }}
            >
              字段设置
            </button>
            {/* 版式设计入口：无设计权的用户不渲染该菜单项（服务端写端点仍独立鉴权） */}
            {form.canFormDesign ? (
              <button
                type="button"
                className="erp-field-setup-item"
                role="menuitem"
                onClick={() => {
                  setFieldSetupMenu(null)
                  const next = new URLSearchParams(searchParams)
                  next.set('design', '1')
                  navigate(`${location.pathname}?${next.toString()}`)
                }}
              >
                表单设计
              </button>
            ) : null}
          </div>
        </div>
      ) : null}
      {/* 单据操作（自定义按钮）的参数表单与二次确认：探路返回的"将会发生什么"在这里给用户看 */}
      {documentAction.dialog}
    </div>
  )
}
