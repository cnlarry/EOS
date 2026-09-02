import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnDef, RowSelectionState, SortingState } from '@tanstack/react-table'
import { useCallback, useEffect, useRef, useState, type CSSProperties, type KeyboardEvent as ReactKeyboardEvent } from 'react'
import { useBlocker, useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { IconTrash } from '@tabler/icons-react'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { Button } from '../../components/ui/Button'
import { ErpCommandBar, type ErpCommandItem } from '../../components/common/ErpCommandBar'
import { ErpTable } from '../../components/common/ErpTable'
import { UnifiedChooser, type UnifiedChooserRow } from '../../components/common/UnifiedChooser'
import { useFormBreadcrumb } from '../../components/layout/FormBreadcrumbContext'
import { AttachmentDialog } from './AttachmentDialog'
import { parseWorkbenchKey, workbenchAction, workbenchCopy, workbenchEdit, workbenchList, workbenchNew, workbenchView } from './workbenchPath'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'
import { FormFieldRenderer } from './FormFieldRenderer'
import type { FormDefinition, FormFieldDefinition } from './formDefinition'
import { buildFormCells, buildFormRows, buildFormSections } from './formLayout'
import { fieldVariant } from './formFieldKind'
import { validateDetailRows, validateMasterFields, type FieldErrors } from './formValidation'
import { AMOUNT_COLUMN_KEYS, AMOUNT_TRIGGER_KEYS, previewDetailAmount, previewMasterAmounts } from './amountCalculator'
import {
  buildKey, canonicalizeDecimalValue, chooserTitle, describeError, detailControlMinWidth, emptyValue, extractDocNo, newIdempotencyKey,
  parseReturnItems,
  summarizeFieldErrors, writableFields, type DetailGridRow, type RecordBundle, type RecordSaveResponse, type SaveRecordRequest,
} from './formEditorUtils'

// 统一表单主表布局列数（2026-08-30 用户拍板）：全局固定一行四列，忽略各模块 FORM_COLUMNS 元数据
//（含显式配置 3 列的 113 个模块），与旧系统密集表单观感保持一致。
const UNIFIED_FORM_COLUMNS = 4

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

const HISTORY_STATE_LABEL: Record<string, string> = {
  Y: '同意',
  N: '驳回',
  S: '跳过',
  W: '撤回',
  A: '送审',
}

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
    // 相邻记录按进入浏览态时的列表当前顺序（旧系统 GoPrior/GoNext 语义），不回退到物理顺序
    navigate(workbenchView(moduleId, navKeys[next]),
      { state: { navKeys, navIndex: next } satisfies ViewNavState })
  }
  const originalRef = useRef<Record<string, string>>({})
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
  /** 多来源「各是各的入口」：先弹来源菜单（ADR-008 §2）。 */
  const [sourceMenu, setSourceMenu] = useState<
    | { kind: 'master'; field: FormFieldDefinition }
    | { kind: 'detail'; index: number; field: FormFieldDefinition }
    | null
  >(null)
  /** 标签右键「字段设置」菜单（仅 canSetup 时触发） */
  const [fieldSetupMenu, setFieldSetupMenu] = useState<{ field: FormFieldDefinition; x: number; y: number } | null>(null)
  const [selectedDetailRows, setSelectedDetailRows] = useState<Set<number>>(new Set())
  const [detailSort, setDetailSort] = useState<{ key: string; dir: 1 | -1 } | null>(null)
  const [activeTab, setActiveTab] = useState(1)
  const [attachOpen, setAttachOpen] = useState(false)
  // 明细列宽统一走服务端（FIELDS.DISPLAY_LENGTH，与工作台一致），拖拽后批量保存
  const widthBatch = useRef<Record<string, number>>({})
  const widthTimer = useRef<number | null>(null)

  const formQuery = useQuery({
    queryKey: ['workbench', moduleId, 'form-definition', isEdit ? 'edit' : isView ? 'view' : 'new'],
    queryFn: () => apiClient.get<FormDefinition>(`/document-workbench/${moduleId}/form-definition?mode=${isEdit ? 'edit' : isView ? 'view' : 'new'}`),
  })
  const recordQuery = useQuery({
    queryKey: ['workbench', moduleId, 'record', keyParam ?? copyFrom],
    queryFn: () => apiClient.get<RecordBundle>(`/document-workbench/${moduleId}/record`, { query: { key: keyParam ?? copyFrom ?? '' } }),
    enabled: (isEdit || isView || isCopy) && Boolean(keyParam ?? copyFrom) && formQuery.isSuccess,
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
    const prefillRaw = sessionStorage.getItem(`erp-assistant-prefill-${moduleId}`)
    if (prefillRaw) {
      sessionStorage.removeItem(`erp-assistant-prefill-${moduleId}`)
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
    for (const field of writableFields(formQuery.data.masterFields)) originalRef.current[field.key] = master[field.key] ?? ''
    setMasterValues(master)
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

  useEffect(() => {
    if (!dirty) return
    const handler = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = '' }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [dirty])

  const blocker = useBlocker(dirty)
  useEffect(() => {
    if (blocker.state !== 'blocked') return
    if (window.confirm('有未保存的修改，确定离开吗？')) blocker.proceed()
    else blocker.reset()
  }, [blocker])

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
      const message = cause instanceof ApiError ? cause.body.message : '操作失败，请稍后重试。'
      window.alert(message)
    },
  })

  // C1（2026-08-27）：送审意见弹窗 + 审批历史（流程信息）。审批历史沿用 /workflow/{moduleId}/history 时间线端点。
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
      const message = cause instanceof ApiError ? cause.body.message : '操作失败，请稍后重试。'
      window.alert(message)
    },
  })

  const deleteRecord = async () => {
    // 键取 URL 路径主键（keyParam，同步权威）：masterValues 为异步回填，record 未返回时点击会得到空键
    if (!keyParam) return
    if (!window.confirm('确定删除该单据吗？删除后不可恢复。')) return
    try {
      await apiClient.delete(`/document-workbench/${moduleId}/record?key=${encodeURIComponent(keyParam)}`, { headers: { 'X-Idempotency-Key': newIdempotencyKey() } })
      // After delete, return to the list and refresh; cross-module browse goes back to the source list
      await queryClient.invalidateQueries({ queryKey: ['workbench', moduleId] })
      navigate(fromModuleId ? workbenchList(fromModuleId) : workbenchList(moduleId))
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
      const message = cause instanceof ApiError ? cause.body.message : '撤回失败，请稍后重试。'
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
   * 错误 UX（ADR-006 决策 2.2/背景 4）：提交校验失败时自动切换到首个错误所在页签，
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

  // Ctrl+S to save (edit/new mode); re-mount listener on each render to capture the latest closure
  useEffect(() => {
    if (isView) return
    const handler = (event: KeyboardEvent) => {
      if (!(event.ctrlKey || event.metaKey) || event.key.toLowerCase() !== 's') return
      event.preventDefault()
      if (save.isPending) return
      if (validateClient()) save.mutate()
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  })

  /** 主表 Enter 下一字段（textarea/select/checkbox/日期原生控件不拦截——决策 5） */
  const handleMasterKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    if (isView || event.key !== 'Enter') return
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

  /** 明细网格 Enter：同列下一行继续；末行则新增行后聚焦同列（决策 5 键盘规则） */
  const handleDetailGridKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    if (event.key !== 'Enter') return
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
    addDetailRow()
    window.setTimeout(() => {
      tbody.querySelector('tr:last-of-type')?.querySelectorAll<HTMLElement>('td input.form-control')[columnIndex]?.focus()
    }, 30)
  }

  const openPrint = () => {
    if (!formQuery.data) return
    const key = buildKey(formQuery.data, masterValues)
    window.open(`/print/${moduleId}?key=${encodeURIComponent(JSON.stringify(key))}`, '_blank')
  }

  const applyChooser = (field: FormFieldDefinition, row: UnifiedChooserRow) => {
    const source = field.choosers.find(item => item.active && item.table && item.serialNo === chooserSerial)
      ?? field.choosers.find(item => item.active && item.table)
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

  const updateDetail = (index: number, key: string, value: string) => {
    const updatedRow = { ...detailRows[index], [key]: value }
    // 金额联动：QTY/PRICE/税率/税型/折扣变更时重算该行金额（服务端保存时权威复算）
    const nextRows = AMOUNT_TRIGGER_KEYS.has(key.toUpperCase())
      ? recalcRowAmounts(detailRows, index, updatedRow)
      : detailRows.map((row, i) => i === index ? updatedRow : row)
    setDetailRows(nextRows)
    syncMasterPreview(nextRows)
    setDetailErrors(current => current.map((rowErrors, i) => {
      if (i !== index) return rowErrors
      const next = { ...rowErrors }
      delete next[key]
      return next
    }))
    setDirty(true)
  }

  /** 明细行金额预览：按行内/主表 TAX_RATE/TAX_TYPE 重算 AMOUNT/TAX_SUM/AMOUNT_TAX。 */
  const recalcRowAmounts = (rows: Record<string, string>[], index: number, updatedRow: Record<string, string>): Record<string, string>[] => {
    if (!formQuery.data) return rows
    const patch = previewDetailAmount(formQuery.data.detailFields, updatedRow, masterValues)
    return rows.map((row, i) => i === index ? (patch ? { ...updatedRow, ...patch } as Record<string, string> : updatedRow) : row)
  }

  /** 主表金额汇总预览（明细 SUM，保存后服务端权威聚合覆盖）。 */
  const syncMasterPreview = (rows: Record<string, string>[]) => {
    if (!formQuery.data) return
    setMasterValues(current => ({ ...current, ...previewMasterAmounts(formQuery.data.masterFields, rows) }))
  }

  /** 明细列宽拖拽：防抖批量保存到服务端（FIELDS.DISPLAY_LENGTH，与工作台 column-widths 一致）。 */
  const saveDetailWidth = useCallback((fieldKey: string, width: number) => {
    if (!formQuery.data) return
    widthBatch.current[fieldKey] = width
    if (widthTimer.current != null) window.clearTimeout(widthTimer.current)
    widthTimer.current = window.setTimeout(() => {
      const detail = widthBatch.current
      widthBatch.current = {}
      void apiClient.put<void>(`/document-workbench/${moduleId}/column-widths`, { master: {}, detail }).catch(() => {})
    }, 300)
  }, [formQuery.data, moduleId])

  const buildEmptyDetailRow = (): Record<string, string> => {
    if (!formQuery.data) return {}
    const row: Record<string, string> = {}
    for (const field of formQuery.data.detailFields) {
      // 主表同名值自动带入新明细行（对齐旧系统 setTRKeyValue 随主表联动带值）
      if (field.isVisible) row[field.key] = masterValues[field.key] ?? emptyValue(field)
    }
    return row
  }

  const addDetailRow = () => {
    if (!formQuery.data) return
    const missing = formQuery.data.detailNoFields
      .split(';')
      .map(field => field.trim())
      .filter(field => field && !(masterValues[field] ?? '').trim())
    if (missing.length > 0) {
      setSaveError(`请先填写主表字段：${missing.join('、')}，再新增明细。`)
      return
    }
    const nextRows = [...detailRows, buildEmptyDetailRow()]
    setDetailRows(nextRows)
    syncMasterPreview(nextRows)
    setDetailErrors(current => [...current, {}])
    setDirty(true)
  }

  const applyDetailChooser = (index: number, field: FormFieldDefinition, rows: UnifiedChooserRow[]) => {
    const source = field.choosers.find(item => item.active && item.table && item.serialNo === detailChooserSerial)
      ?? field.choosers.find(item => item.active && item.table)
    const mapping = parseReturnItems(source?.returnMapping)
    const applyMapping = (target: Record<string, string>, row: UnifiedChooserRow) => {
      for (const item of mapping) {
        if (row[item.column] === undefined) continue
        target[item.target] = String(row[item.column] ?? '')
      }
      return target
    }
    // 明细多选：逐条追加明细行（对齐旧系统 ReturnMultiValue 的 addTR 语义）
    if (field.chooseMultiple && rows.length > 1) {
      const newRows = rows.map(row => applyMapping(buildEmptyDetailRow(), row))
      const mergedRows = [...detailRows, ...newRows]
      setDetailRows(mergedRows)
      syncMasterPreview(mergedRows)
      setDetailErrors(current => [...current, ...newRows.map(() => ({}))])
      setDirty(true)
      setDetailChooser(null)
      return
    }
    const row = rows[0]
    if (mapping.length > 0) {
      const updatedRow = applyMapping({ ...detailRows[index] }, row)
      const nextRows = recalcRowAmounts(detailRows, index, updatedRow)
      setDetailRows(nextRows)
      syncMasterPreview(nextRows)
      setDirty(true)
    }
    setDetailChooser(null)
  }

  const removeDetailRow = (index: number) => {
    const nextRows = detailRows.filter((_, i) => i !== index)
    setDetailRows(nextRows)
    syncMasterPreview(nextRows)
    setDetailErrors(current => current.filter((_, i) => i !== index))
    setSelectedDetailRows(current => new Set([...current].filter(i => i !== index).map(i => i > index ? i - 1 : i)))
    setDirty(true)
  }

  /** 多来源「各是各的入口」：1 个直接打开，多个先弹来源菜单（ADR-008 §2）。 */
  const openChooser = (field: FormFieldDefinition, kind: 'master' | 'detail', detailIndex?: number) => {
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
  }

  const removeSelectedDetailRows = () => {
    const nextRows = detailRows.filter((_, index) => !selectedDetailRows.has(index))
    setDetailRows(nextRows)
    syncMasterPreview(nextRows)
    setDetailErrors(current => current.filter((_, index) => !selectedDetailRows.has(index)))
    setSelectedDetailRows(new Set())
    setDirty(true)
  }

  const handleDetailSortingChange = (next: SortingState) => {
    const sort = next[0]
    setDetailSort(sort ? { key: sort.id, dir: sort.desc ? -1 : 1 } : null)
  }

  const handleDetailRowSelectionChange = (next: RowSelectionState) => {
    setSelectedDetailRows(new Set(
      Object.keys(next)
        .filter(id => next[id] && id.startsWith('r'))
        .map(id => Number(id.slice(1))),
    ))
  }

  if (formQuery.isPending || (isEdit && recordQuery.isPending)) return <LoadingState label="正在加载表单…" />
  if (formQuery.isError) return <section className="card"><div className="card-body text-center py-5">{describeError(formQuery.error)}</div></section>
  if (isEdit && recordQuery.isError) return <section className="card"><div className="card-body text-center py-5">{describeError(recordQuery.error)}</div></section>

  const form = formQuery.data
  if (!form) return null
  // 明细走金额汇总（明细表有 AMOUNT 列）时，主表金额列强制只读展示（保存后服务端权威聚合）
  const masterAmountLocked = form.detailFields.some(field => field.key.toUpperCase() === 'AMOUNT')
  const visibleMaster = form.masterFields.filter(field => field.isVisible)
  const visibleDetail = form.detailFields.filter(field => field.isVisible)
  const hasTabs = form.tabs.length > 0
  const activeTabNo = hasTabs ? activeTab : 1
  const masterCells = buildFormCells(visibleMaster).filter(cell => !hasTabs || cell[0].tabNo === activeTabNo)
  // Group master fields by FORM_CELL_GROUP (>=2 fields in a group forms a section; remainder go to default section)
  const masterSections = buildFormSections(masterCells)
  // Tab error badges: hidden tabs' errors shown as count badges
  const tabErrorCounts = new Map<number, number>()
  for (const field of form.masterFields) {
    if (field.isVisible && fieldErrors[field.key]) tabErrorCounts.set(field.tabNo, (tabErrorCounts.get(field.tabNo) ?? 0) + 1)
  }
  const orderedDetailIndices = (() => {
    if (!detailSort) return detailRows.map((_, index) => index)
    const { key, dir } = detailSort
    const indices = detailRows.map((_, index) => index)
    indices.sort((a, b) => {
      const va = detailRows[a][key] ?? ''
      const vb = detailRows[b][key] ?? ''
      const na = Number(va)
      const nb = Number(vb)
      const numeric = va !== '' && vb !== '' && !Number.isNaN(na) && !Number.isNaN(nb)
      const cmp = numeric ? na - nb : String(va).localeCompare(String(vb), 'zh-CN', { numeric: true })
      return cmp * dir
    })
    return indices
  })()
  const detailRowSelection = Object.fromEntries([...selectedDetailRows].map(index => [`r${index}`, true])) as RowSelectionState
  // Empty state renders a "+ Add Row" dashed entry instead of pre-filling blank rows
  const detailGridRows: DetailGridRow[] = orderedDetailIndices.map(index => ({ __id: `r${index}`, __index: index, ...detailRows[index] }))
  const detailColumns: ColumnDef<DetailGridRow, unknown>[] = [
    {
      id: '__check',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'erp-detail-check text-center', resizable: false, truncate: false },
      header: ({ table }) => (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label="全选"
          checked={table.getIsAllPageRowsSelected()}
          ref={input => { if (input) input.indeterminate = table.getIsSomePageRowsSelected() }}
          onChange={table.getToggleAllPageRowsSelectedHandler()}
        />
      ),
      cell: ({ row }) => (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label={`选择第${row.index + 1}行`}
          checked={row.getIsSelected()}
          onChange={row.getToggleSelectedHandler()}
          onClick={event => event.stopPropagation()}
        />
      ),
    },
    {
      id: '__rowNo',
      header: '序号',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'erp-detail-row-no text-center', resizable: false, truncate: false },
      cell: ({ row }) => <span className="text-secondary">{row.index + 1}</span>,
    },
    ...visibleDetail.map((field): ColumnDef<DetailGridRow, unknown> => ({
      id: field.key,
      accessorKey: field.key,
      header: field.label,
      enableSorting: true,
      meta: { minWidth: Math.max(field.displayLength, detailControlMinWidth(field)), dataType: field.dataType, minWidthFloor: true, truncate: false },
      cell: ({ row }) => {
        const index = row.original.__index
        return (
          <FormFieldRenderer
            field={field}
            value={String(row.original[field.key] ?? '')}
            error={detailErrors[index]?.[field.key]}
            onChange={value => updateDetail(index, field.key, value)}
            onChoose={fieldToChoose => openChooser(fieldToChoose, 'detail', index)}
            bare
          />
        )
      },
    })),
    {
      id: '__actions',
      header: '操作',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'erp-detail-actions text-center', resizable: false, truncate: false },
      cell: ({ row }) => (
        // Inline delete as icon button, aligned with command bar icon conventions
        <Button
          size="sm"
          variant="danger"
          icon={<IconTrash size={16} />}
          title="删除本行"
          aria-label={`删除第${row.index + 1}行`}
          onClick={() => removeDetailRow(row.original.__index)}
        />
      ),
    },
  ]
  const openFieldSetupMenu = (field: FormFieldDefinition, x: number, y: number) => setFieldSetupMenu({ field, x, y })
  const renderField = (field: FormFieldDefinition, bare = false) => (
    <FormFieldRenderer
      key={field.key}
      field={masterAmountLocked && AMOUNT_COLUMN_KEYS.has(field.key.toUpperCase()) ? { ...field, isReadonly: true } : field}
      value={masterValues[field.key] ?? ''}
      error={fieldErrors[field.key]}
      viewing={isView}
      onChange={value => {
        setMasterValues(current => ({ ...current, [field.key]: value }))
        setFieldErrors(current => { const next = { ...current }; delete next[field.key]; return next })
        setDirty(true)
      }}
      onChoose={fieldToChoose => openChooser(fieldToChoose, 'master')}
      onFieldSetup={form.canSetup ? openFieldSetupMenu : undefined}
      bare={bare}
    />
  )
  const renderCell = (cell: FormFieldDefinition[]) => {
    const [main, ...companions] = cell
    if (companions.length === 0) return renderField(main)
    const isBoolean = main.dataType.toLowerCase().includes('bit')
    return (
      <div key={main.key} className="erp-form-cell">
        <label
          className="erp-form-label"
          onContextMenu={form.canSetup ? event => { event.preventDefault(); openFieldSetupMenu(main, event.clientX, event.clientY) } : undefined}
        >
          {main.label}{!isBoolean && !main.isReadonly && !main.serverFilled && main.isRequired ? ' *' : ''}
        </label>
        <div className="erp-form-cell-controls">
          {renderField(main, true)}
          {companions.map(companion => renderField(companion, true))}
        </div>
      </div>
    )
  }

  return (
    <div className="d-flex flex-column gap-2 erp-form-page">
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
                // 已结案：解批/编辑/删除禁用；已审批：批核/编辑/删除禁用；在途流程：编辑/删除禁用（按钮禁用而非隐藏，旧系统 DxAuthentication 语义）
                const editDisabled = isFinished || isConfirmed || flowInProgress
                const deleteDisabled = isFinished || isConfirmed || flowInProgress
                const whitelistItems: ErpCommandItem[] = (form.buttons && form.buttons.length > 0
                  ? form.buttons.flatMap((button): ErpCommandItem[] => {
                      switch (button.action) {
                        case 'approve':
                          return form.hasWorkflow && form.canApprove && keyParam && master && master.CONFIRM_TAG !== true && !flowInProgress
                            ? [{ action: 'approve', disabled: isFinished, loading: workflow.isPending, onClick: () => openApprove() }]
                            : []
                        case 'deapprove':
                          return form.hasWorkflow && form.canDeapprove && keyParam && master && master.CONFIRM_TAG === true
                            ? [{ action: 'deapprove', disabled: isFinished, loading: workflow.isPending, onClick: () => workflow.mutate({ action: 'deapprove' }) }]
                            : []
                        case 'endcase':
                          return keyParam && form.canEndCase && master && master.FINISHED_TAG !== true
                            ? [{ action: 'endcase', loading: finish.isPending, onClick: () => finish.mutate('endcase') }]
                            : []
                        case 'unendcase':
                          return keyParam && form.canUnEndCase && master && master.FINISHED_TAG === true
                            ? [{ action: 'unendcase', loading: finish.isPending, onClick: () => finish.mutate('unendcase') }]
                            : []
                        case 'print':
                          return keyParam ? [{ action: 'print', onClick: openPrint }] : []
                        default:
                          return []
                      }
                    })
                  : [
                      // 未配置 FORM_BUTTONS 的回退集（保持既有行为：工作流/结案/打印）
                      ...(form.hasWorkflow && keyParam && master && master.CONFIRM_TAG !== true && !flowInProgress && form.canApprove
                        ? [{ action: 'approve', disabled: isFinished, loading: workflow.isPending, onClick: () => openApprove() } satisfies ErpCommandItem]
                        : []),
                      ...(form.hasWorkflow && keyParam && master && master.CONFIRM_TAG === true && form.canDeapprove
                        ? [{ action: 'deapprove', disabled: isFinished, loading: workflow.isPending, onClick: () => workflow.mutate({ action: 'deapprove' }) } satisfies ErpCommandItem]
                        : []),
                      ...(keyParam && form.canEndCase && master && master.FINISHED_TAG !== true
                        ? [{ action: 'endcase', loading: finish.isPending, onClick: () => finish.mutate('endcase') } satisfies ErpCommandItem]
                        : []),
                      ...(keyParam && form.canUnEndCase && master && master.FINISHED_TAG === true
                        ? [{ action: 'unendcase', loading: finish.isPending, onClick: () => finish.mutate('unendcase') } satisfies ErpCommandItem]
                        : []),
                      ...(keyParam ? [{ action: 'print', onClick: openPrint } satisfies ErpCommandItem] : []),
                    ])
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
                  // 删除为浏览态标准动作（对齐旧 ModifyToolBar）：权限 canDelete ∧ 单据状态，不依赖 FORM_BUTTONS 配置
                  ...(form.canDelete && keyParam
                    ? [{ action: 'delete', variant: 'danger', disabled: deleteDisabled, onClick: () => void deleteRecord() } satisfies ErpCommandItem]
                    : []),
                  ...whitelistItems.filter(item => item.action === 'approve' || item.action === 'deapprove'),
                  // A3：在途流程时显示「撤回」（发起人），撤回后可编辑并重新送审——与批核同位互斥
                  ...(flowInProgress && keyParam
                    ? [{ action: 'withdraw', loading: withdraw.isPending, onClick: () => withdraw.mutate() } satisfies ErpCommandItem]
                    : []),
                  // C1：审批历史（流程信息）——流程模块浏览态显示
                  ...(form.hasWorkflow && keyParam
                    ? [{ action: 'history', onClick: () => setHistoryOpen(true) } satisfies ErpCommandItem]
                    : []),
                  ...whitelistItems.filter(item => item.action === 'endcase' || item.action === 'unendcase'),
                  ...(form.canFileView && keyParam ? [{ action: 'attach', onClick: () => setAttachOpen(true) } satisfies ErpCommandItem] : []),
                  ...whitelistItems.filter(item => item.action === 'print'),
                  ...(form.helpUrl ? [{ action: 'help', onClick: () => window.open(form.helpUrl!, '_blank', 'noopener') } satisfies ErpCommandItem] : []),
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
          <div className="erp-form-grid" onKeyDown={handleMasterKeyDown}>
            {masterSections.map((section, sectionIndex) => (
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
        </div>
      </section>
      {form.detailFields.length > 0 ? (
        <section className="card erp-detail-card">
          <div className="card-header erp-detail-toolbar">
            <div className="d-flex gap-2">
              <Button size="sm" onClick={addDetailRow}>新增一行</Button>
              <Button size="sm" variant="danger" disabled={selectedDetailRows.size === 0} onClick={removeSelectedDetailRows}>删除所选{selectedDetailRows.size > 0 ? ` (${selectedDetailRows.size})` : ''}</Button>
              {detailSort && <span className="small text-secondary align-self-center">视图排序（保存顺序以序号 SERIAL_NO 为准）</span>}
              {/* 子表专用工具栏扩展位：生成请购单等后续加入 */}
            </div>
          </div>
          <div className="table-responsive" onKeyDown={handleDetailGridKeyDown}>
            <ErpTable
              columns={detailColumns}
              data={detailGridRows}
              getRowId={row => row.__id}
              sorting={detailSort ? [{ id: detailSort.key, desc: detailSort.dir === -1 }] : []}
              onSortingChange={handleDetailSortingChange}
              rowSelection={detailRowSelection}
              onRowSelectionChange={handleDetailRowSelectionChange}
              resizable
              storageKey={`form-detail-${moduleId}`}
              persistResize={false}
              onColumnResize={saveDetailWidth}
              className="erp-detail-grid"
              responsive={false}
              empty={
                detailRows.length === 0 && !isView ? (
                  <div className="erp-detail-empty">
                    <Button size="sm" variant="secondary" onClick={addDetailRow}>+ 新增一行</Button>
                  </div>
                ) : undefined
              }
            />
          </div>
        </section>
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
        <div className="modal modal-blur show d-block" role="dialog" aria-modal="true" aria-label="送审确认">
          <div className="modal-dialog modal-dialog-centered erp-dialog-sm">
            <div className="modal-content">
              <div className="modal-header">
                <h2 className="modal-title">送审确认</h2>
                <button type="button" className="btn-close" aria-label="关闭" onClick={() => setApproveOpen(false)} />
              </div>
              <div className="modal-body">
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
              </div>
              <div className="modal-footer">
                <Button variant="secondary" onClick={() => setApproveOpen(false)}>取消</Button>
                <Button variant="primary" loading={workflow.isPending} onClick={() => workflow.mutate({ action: 'approve', message: submitMessage })}>
                  确认送审
                </Button>
              </div>
            </div>
          </div>
        </div>
      )}
      {historyOpen && keyParam && (
        <div className="modal modal-blur show d-block" role="dialog" aria-modal="true" aria-label="审批历史">
          <div className="modal-dialog modal-dialog-centered">
            <div className="modal-content">
              <div className="modal-header">
                <h2 className="modal-title">审批历史（流程信息）</h2>
                <button type="button" className="btn-close" aria-label="关闭" onClick={() => setHistoryOpen(false)} />
              </div>
              <div className="modal-body" style={{ maxHeight: '60dvh', overflowY: 'auto' }}>
                {historyQuery.isPending ? <LoadingState label="正在加载审批历史…" /> : historyQuery.isError ? (
                  <ErrorState message="审批历史加载失败" onRetry={() => void historyQuery.refetch()} />
                ) : (historyQuery.data?.rows ?? []).length === 0 ? (
                  <div className="text-center text-secondary py-4">暂无审批记录</div>
                ) : (
                  <ul className="list-unstyled mb-0">
                    {(historyQuery.data?.rows ?? []).map((row, index) => {
                      const label = row.kind === 'confirm' ? '流程完成' : (HISTORY_STATE_LABEL[row.state] ?? row.state) || '—'
                      return (
                        <li key={index} className="d-flex gap-2 align-items-start py-2 border-bottom">
                          <span className="badge text-bg-light border mt-1" style={{ minWidth: 44 }}>{row.step || '—'}</span>
                          <div className="flex-grow-1">
                            <div className="small">
                              <span className="fw-semibold">{row.stepDesc || label}</span>
                              {row.approver && <span className="font-monospace text-secondary ms-2">{row.approver}</span>}
                              <span className={`ms-2 badge ${row.state === 'Y' || row.kind === 'confirm' ? 'text-bg-success' : row.state === 'N' || row.state === 'W' ? 'text-bg-danger' : row.state === 'A' ? 'text-bg-info' : row.state === 'S' ? 'text-bg-secondary' : 'text-bg-warning'}`}>{label}</span>
                            </div>
                            {row.message && <div className="small text-secondary">{row.message}</div>}
                          </div>
                          {row.date && <span className="small text-secondary text-nowrap">{row.date}</span>}
                        </li>
                      )
                    })}
                  </ul>
                )}
              </div>
              <div className="modal-footer">
                <Button variant="secondary" onClick={() => setHistoryOpen(false)}>关闭</Button>
              </div>
            </div>
          </div>
        </div>
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
          </div>
        </div>
      ) : null}
    </div>
  )
}
