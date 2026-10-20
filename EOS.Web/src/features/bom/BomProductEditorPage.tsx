import { IconArrowsLeftRight, IconDeviceFloppy, IconFileImport, IconPlus, IconPrinter, IconSitemap, IconTrash } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useEffect, useMemo, useState, type CSSProperties } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpCommandBar, type ErpCommandItem } from '../../components/common/ErpCommandBar'
import { ErpListGrid } from '../../components/common/ErpListGrid'
import { UnifiedChooser, type UnifiedChooserRow } from '../../components/common/UnifiedChooser'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { useToast } from '../../components/ui/toastContext'
import type { ColumnDef } from '../../lib/tanstackTable'
import { describeApiError } from '../../lib/errors'
import { apiClient } from '../../services/api'
import type { FormDefinition, FormFieldDefinition } from '../document-workbench/formDefinition'
import { FormFieldRenderer, type ChooserAnchor } from '../document-workbench/FormFieldRenderer'
import {
  emptyValue,
  newIdempotencyKey,
  parseReturnItems,
  writableFields,
  type DetailGridRow,
  type RecordBundle,
  type RecordSaveResponse,
  type SaveRecordRequest,
} from '../document-workbench/formEditorUtils'
import { validateField, validateMasterFields, type FieldErrors } from '../document-workbench/formValidation'
import { EDITION_PATTERN, importBomLines, parseBomCsv, type BomImportRow } from './bomApi'

const MODULE_ID = 1204
/** BOM 主表只有这几列值得占版面；其余（历史长宽等）不排进定制页。 */
const MASTER_FIELD_ORDER = ['PRO_NO', 'EDITION', 'BATCH_QTY', 'PLAN_PERSON', 'ATTENTION', 'REMARK']
/**
 * 明细可编辑列的顺序；不在名单里的可见字段一律进「只读列」。
 *
 * `ELEMENT_EDITION` 紧跟在子件料号后面：它是"这个子件用哪一版 BOM"，
 * 与料号是一对，写在一起才读得通。半成品必填、材料留空——校验在服务端
 * （`require-when-referenced`），前端只负责给输入口。
 */
const DETAIL_FIELD_ORDER = [
  'SERIAL_NO',
  'ELEMENT_PRO_NO',
  'ELEMENT_EDITION',
  'ELEMENT_QTY',
  // 底数紧跟用量：用量是「每 BASE_QTY 个父件用几个」，两个字段连读才是一句话
  'BASE_QTY',
  'LOST_RATE',
  'REMARK',
]

/**
 * 产品 BOM 的单层维护（模块 1204 定制页，ADR-039 D1）。
 *
 * 界面自绘、**不读 `MODULE_FORM_LAYOUT`**：BOM 的版式有 25 列而真正能填的只有 5 格，
 * 照版式渲染等于把"能用"和"看得懂"对立起来。这里只排业务需要的列。
 *
 * 但**保存契约不重写**：仍走 `PUT /document-workbench/1204/record`，载荷与统一表单
 * 逐字段一致（`values` / `details` / `original` 并发快照 / `detailSerials` 项次身份）。
 * 校验也复用 `validateMasterFields` / `validateField`——两端的判定码由同一份 fixture 锁定。
 *
 * 选择器只取字段的**第一个**启用来源：BOM 的子件只挂 1201，多来源菜单在这里没有消费方。
 * 若将来真的出现多来源，再补 `ChooserSourceMenu`，不提前留分支。
 */
export function BomProductEditorPage() {
  const params = useParams<{ proNo: string; edition: string }>()
  const proNo = params.proNo ?? ''
  const edition = params.edition ?? ''
  const keyParam = useMemo(() => JSON.stringify([proNo, edition]), [proNo, edition])
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const toast = useToast()

  const [masterValues, setMasterValues] = useState<Record<string, string>>({})
  const [detailRows, setDetailRows] = useState<Record<string, string>[]>([])
  const [masterErrors, setMasterErrors] = useState<FieldErrors>({})
  const [detailErrors, setDetailErrors] = useState<FieldErrors[]>([])
  const [saveError, setSaveError] = useState<string | null>(null)
  const [dirty, setDirty] = useState(false)
  const [compareOpen, setCompareOpen] = useState(false)
  const [compareEdition, setCompareEdition] = useState('')
  const [importOpen, setImportOpen] = useState(false)
  const [importReplace, setImportReplace] = useState(false)
  const [importFileName, setImportFileName] = useState('')
  const [importRows, setImportRows] = useState<BomImportRow[]>([])
  const [importErrors, setImportErrors] = useState<string[]>([])
  const [chooser, setChooser] = useState<
    { kind: 'master'; field: FormFieldDefinition } | { kind: 'detail'; index: number; field: FormFieldDefinition } | null
  >(null)

  const formQuery = useQuery({
    // 固定 `edit`：这是可写页。浏览由结构树承担，没有只读形态。
    queryKey: ['workbench', MODULE_ID, 'form-definition', 'edit'],
    queryFn: () => apiClient.get<FormDefinition>(`/document-workbench/${MODULE_ID}/form-definition?mode=edit`),
  })
  const recordQuery = useQuery({
    queryKey: ['bom', 'record', proNo, edition],
    queryFn: () => apiClient.get<RecordBundle>(`/document-workbench/${MODULE_ID}/record`, { query: { key: keyParam } }),
    enabled: Boolean(proNo && edition),
  })

  const form = formQuery.data
  const record = recordQuery.data

  // 记录到手后灌进本地状态。主表按**全部字段**取值（只读列也要显示），
  // 明细同理；提交时才用 writableFields 裁掉服务端维护的列。
  useEffect(() => {
    if (!form || !record) return
    const values: Record<string, string> = {}
    for (const field of form.masterFields) values[field.key] = toText(record.master[field.key])
    setMasterValues(values)
    setDetailRows(record.details.map(row => {
      const mapped: Record<string, string> = {}
      for (const field of form.detailFields) mapped[field.key] = toText(row[field.key])
      return mapped
    }))
    setMasterErrors({})
    setDetailErrors([])
    setSaveError(null)
    setDirty(false)
  }, [form, record])

  const editableDetailFields = useMemo(
    () => (form ? writableFields(form.detailFields) : []),
    [form],
  )

  const masterLayout = useMemo(() => {
    if (!form) return []
    const visible = form.masterFields.filter(field => field.isVisible)
    const ordered = MASTER_FIELD_ORDER
      .map(key => visible.find(field => field.key === key))
      .filter((field): field is FormFieldDefinition => Boolean(field))
    const rest = visible.filter(field => !MASTER_FIELD_ORDER.includes(field.key))
    return [...ordered, ...rest]
  }, [form])

  const updateMaster = useCallback((key: string, value: string) => {
    setMasterValues(current => ({ ...current, [key]: value }))
    setDirty(true)
    setMasterErrors(current => {
      if (!current[key]) return current
      const next = { ...current }
      delete next[key]
      return next
    })
  }, [])

  const updateDetail = useCallback((index: number, key: string, value: string) => {
    setDetailRows(current => {
      const next = [...current]
      next[index] = { ...next[index], [key]: value }
      return next
    })
    setDirty(true)
    setDetailErrors(current => {
      const rowErrors = current[index]
      if (!rowErrors?.[key]) return current
      const next = [...current]
      next[index] = { ...rowErrors }
      delete next[index][key]
      return next
    })
  }, [])

  /** 新行：可见字段先按主表同名值带入，再落到字段默认值——与统一表单的 addDetailRow 同口径。 */
  const addDetailRow = useCallback(() => {
    if (!form) return
    const row: Record<string, string> = {}
    for (const field of form.detailFields) {
      if (field.isVisible) row[field.key] = masterValues[field.key] ?? emptyValue(field)
    }
    setDetailRows(current => [...current, row])
    setDetailErrors(current => [...current, {}])
    setDirty(true)
  }, [form, masterValues])

  const removeDetailRow = useCallback((index: number) => {
    setDetailRows(current => current.filter((_, position) => position !== index))
    setDetailErrors(current => current.filter((_, position) => position !== index))
    setDirty(true)
  }, [])

  const applyChooser = useCallback((row: UnifiedChooserRow) => {
    if (!chooser) return
    const source = chooser.field.choosers.find(item => item.active && item.table)
    const mapping = parseReturnItems(source?.returnMapping)
    const apply = (target: Record<string, string>) => {
      const next = { ...target }
      for (const item of mapping) {
        if (row[item.column] === undefined) continue
        next[item.target] = String(row[item.column] ?? '')
      }
      return next
    }
    if (chooser.kind === 'master') {
      setMasterValues(current => apply(current))
    } else {
      setDetailRows(current => {
        const next = [...current]
        next[chooser.index] = apply(next[chooser.index] ?? {})
        return next
      })
    }
    setDirty(true)
    setChooser(null)
  }, [chooser])

  const save = useMutation({
    mutationFn: async () => {
      if (!form) throw new Error('表单定义未加载。')
      const masterFieldErrors = validateMasterFields(form.masterFields, masterValues)
      const rowFieldErrors = detailRows.map(row => {
        const errors: FieldErrors = {}
        for (const field of editableDetailFields) {
          const message = validateField(field, row[field.key] ?? '')
          if (message) errors[field.key] = message
        }
        return errors
      })
      setMasterErrors(masterFieldErrors)
      setDetailErrors(rowFieldErrors)
      if (Object.keys(masterFieldErrors).length > 0 || rowFieldErrors.some(errors => Object.keys(errors).length > 0)) {
        throw new Error('有字段未通过校验，请检查标红的单元格。')
      }

      const values: Record<string, string> = {}
      for (const field of writableFields(form.masterFields)) values[field.key] = masterValues[field.key] ?? ''
      // 明细提交"可写字段"的并集：只读的父键（PRO_NO / EDITION）由服务端按主键回填，
      // 项次（SERIAL_NO）走 detailSerials 单独回传，避免被当成普通列覆盖。
      const details = detailRows.map(row => {
        const detail: Record<string, string> = {}
        for (const field of editableDetailFields) detail[field.key] = row[field.key] ?? ''
        return detail
      })
      const body: SaveRecordRequest = { values, details, idempotencyKey: newIdempotencyKey() }
      if (record) {
        // `original` 是并发快照，服务端按 `Dictionary<string,string>` 收。
        // 直接透传 `record.master` 会把 int / decimal / bit 原样发过去（JSON number / boolean），
        // 反序列化直接 400："The JSON value could not be converted to System.String"。
        // 与 `values` 同口径：一律字符串化，null 变空串。
        body.original = Object.fromEntries(
          Object.entries(record.master).map(([key, value]) => [key, value == null ? '' : String(value)]),
        )
      }
      body.detailSerials = detailRows.map(row => (row.SERIAL_NO ?? '').trim() || null)

      return apiClient.put<RecordSaveResponse>(
        `/document-workbench/${MODULE_ID}/record?key=${encodeURIComponent(keyParam)}`,
        body,
      )
    },
    onSuccess: async () => {
      setSaveError(null)
      setDirty(false)
      // 先把 BOM 相关查询全部作废再跳：结构树一进去就要读最新数据，
      // 留一份旧缓存会让"刚保存的结构"和树上看到的不一致
      await queryClient.invalidateQueries({ queryKey: ['bom'] })
      toast.notify({ message: '已保存', variant: 'success' })
      // 落盘后进**浏览态**：结构树是 1204 唯一的浏览入口（编辑页只有可写一种形态）
      navigate(`/bom/product/${encodeURIComponent(proNo)}/${encodeURIComponent(edition)}/tree`)
    },
    onError: error => {
      const message = describeApiError(error, '保存失败。')
      setSaveError(message)
      toast.notify({ message, variant: 'danger' })
    },
  })

  // 取试算表：解析在前端（逐行报错），落库在服务端（校验子件/底数/损耗率，项次由它分配）。
  const importLines = useMutation({
    mutationFn: () => importBomLines(proNo, edition, importReplace, importRows),
    onSuccess: async result => {
      setImportOpen(false)
      setImportRows([])
      setImportErrors([])
      setDirty(false)
      await recordQuery.refetch()
      toast.notify({
        message: `已导入 ${result.imported} 行${result.replaced ? '（原有明细已替换）' : ''}`,
        variant: 'success',
      })
    },
    onError: error => setImportErrors([describeApiError(error, '导入失败。')]),
  })

  const detailGridRows = useMemo<DetailGridRow[]>(
    () => detailRows.map((row, index) => ({ __id: String(index), __index: index, ...row })),
    [detailRows],
  )

  const detailColumns = useMemo<ColumnDef<DetailGridRow, unknown>[]>(() => {
    const ordered = DETAIL_FIELD_ORDER
      .map(key => editableDetailFields.find(field => field.key === key))
      .filter((field): field is FormFieldDefinition => Boolean(field))
    const rest = editableDetailFields.filter(field => !DETAIL_FIELD_ORDER.includes(field.key))
    const columns: ColumnDef<DetailGridRow, unknown>[] = [...ordered, ...rest].map(field => ({
      id: field.key,
      header: field.label,
      meta: { minWidth: field.key === 'ELEMENT_PRO_NO' ? 200 : 110 },
      cell: ({ row }) => (
        <FormFieldRenderer
          field={field}
          value={detailRows[row.original.__index]?.[field.key] ?? ''}
          error={detailErrors[row.original.__index]?.[field.key]}
          bare
          onChange={value => updateDetail(row.original.__index, field.key, value)}
          onChoose={(target, _anchor: ChooserAnchor) => setChooser({ kind: 'detail', index: row.original.__index, field: target })}
        />
      ),
    }))
    columns.push({
      id: '__ops',
      header: '',
      enableHiding: false,
      meta: { minWidth: 80, resizable: false },
      cell: ({ row }) => (
        <Button size="sm" variant="ghost" onClick={() => removeDetailRow(row.original.__index)}>
          删除
        </Button>
      ),
    })
    return columns
  }, [detailErrors, detailRows, editableDetailFields, removeDetailRow, updateDetail])

  const commands = useMemo<ErpCommandItem[]>(() => {
    const items: ErpCommandItem[] = [
      {
        action: 'save',
        icon: <IconDeviceFloppy size={16} />,
        label: dirty ? '保存 *' : '保存',
        variant: 'primary',
        loading: save.isPending,
        onClick: () => save.mutate(),
      },
    ]
    items.push(
      { action: 'bom-tree', icon: <IconSitemap size={16} />, label: '结构树', onClick: () => navigate(`/bom/product/${encodeURIComponent(proNo)}/${encodeURIComponent(edition)}/tree`) },
      { action: 'bom-compare', icon: <IconArrowsLeftRight size={16} />, label: '对比', title: '与另一个版次对比', onClick: () => { setCompareEdition(''); setCompareOpen(true) } },
    )
    items.push({
      action: 'bom-import',
      icon: <IconFileImport size={16} />,
      label: '取试算表',
      title: '从 CSV 导入明细（追加或替换）',
      onClick: () => {
        setImportReplace(false)
        setImportFileName('')
        setImportRows([])
        setImportErrors([])
        setImportOpen(true)
      },
    })
    items.push(
      { action: 'bom-print', icon: <IconPrinter size={16} />, label: '打印', onClick: () => window.open(`/print/${MODULE_ID}?key=${encodeURIComponent(keyParam)}`, '_blank') },
      { action: 'refresh', label: '刷新', onClick: () => { void recordQuery.refetch() } },
      // 返回的是 BOM 列表（定制页），不是统一工作台
      { action: 'back', label: '返回列表', onClick: () => navigate('/bom/product') },
    )
    return items
  }, [dirty, edition, keyParam, navigate, proNo, recordQuery, save])

  const chooserSource = useMemo(() => {
    if (!chooser) return null
    const source = chooser.field.choosers.find(item => item.active && item.table)
    if (source?.sourceKey) {
      // 无料号时传 undefined 而不是空对象：`Record<string, string>` 不接受 `{}`
      // （空对象的每个值都是 undefined，类型上不成立）
      return { kind: 'sourceKey' as const, key: source.sourceKey, args: proNo ? { proNo } : undefined }
    }
    return { kind: 'formField' as const, moduleId: String(MODULE_ID), fieldKey: chooser.field.key, serialNo: source?.serialNo ?? null }
  }, [chooser, proNo])

  if (!proNo || !edition) {
    return <ErrorState message="地址缺少料号或版次，无法定位记录。" />
  }
  if (formQuery.isPending || recordQuery.isPending) return <LoadingState />
  if (formQuery.isError || recordQuery.isError) {
    return (
      <ErrorState
        message={describeApiError(formQuery.error ?? recordQuery.error, '无法打开这条 BOM——可能没有编辑权，或记录已被删除。')}
        onRetry={() => { void formQuery.refetch(); void recordQuery.refetch() }}
      />
    )
  }

  return (
    <div className="erp-form-page erp-bom-form-page">
      {saveError ? <div className="alert alert-danger mb-0">{saveError}</div> : null}
      <section className="card erp-form-card">
        <div className="card-body">
          <ErpCommandBar items={commands} ariaLabel="BOM 维护操作" />
          {/* 与统一表单同一套网格：`erp-form-grid` > `erp-form-row` > 每个字段一格，
              列数由 `--erp-form-cols` 决定。不用 `row g-3` + `col-md-6`——那是
              Bootstrap 栅格，间距与断点和统一表单不是一套 */}
          <div className="erp-form-grid">
            <div className="erp-form-row" style={{ '--erp-form-cols': 2 } as CSSProperties}>
              {masterLayout.map(field => (
                <div key={field.key}>
                  <FormFieldRenderer
                    field={field}
                    value={masterValues[field.key] ?? ''}
                    error={masterErrors[field.key]}
                    onChange={value => updateMaster(field.key, value)}
                    onChoose={(target, _anchor: ChooserAnchor) => setChooser({ kind: 'master', field: target })}
                  />
                </div>
              ))}
            </div>
          </div>
        </div>
      </section>

      <section className="card erp-form-card">
        <div className="card-body">
          <div className="d-flex justify-content-between align-items-center mb-2">
            <strong>明细 · {detailRows.length} 行</strong>
            <Button size="sm" icon={<IconPlus size={16} />} onClick={addDetailRow}>新增行</Button>
          </div>
          {detailRows.length === 0 ? (
            <div className="text-muted py-3">还没有明细。点「新增行」后选子件、填用量。</div>
          ) : (
            <ErpListGrid
              plain
              listId="bom-detail-edit"
              columns={detailColumns}
              rows={detailGridRows}
              rowKey={(_row, index) => String(index)}
              tableProps={{ className: 'table-sm' }}
            />
          )}
          <div className="text-muted mt-2 d-flex align-items-center gap-1">
            <IconTrash size={14} /> 删行后未保存不会生效；用量按「每 BASE_QTY 个父件」计。
          </div>
        </div>
      </section>

      {compareOpen ? (
        <Modal
          title="与另一个版次对比"
          size="sm"
          onClose={() => setCompareOpen(false)}
          footer={
            <>
              <Button onClick={() => setCompareOpen(false)}>取消</Button>
              <Button
                variant="primary"
                disabled={!EDITION_PATTERN.test(compareEdition)}
                onClick={() => {
                  const target = compareEdition.trim()
                  setCompareOpen(false)
                  navigate(`/bom/product/${encodeURIComponent(proNo)}/${encodeURIComponent(edition)}/compare/${encodeURIComponent(target)}`)
                }}
              >
                对比
              </Button>
            </>
          }
        >
          <label className="form-label" htmlFor="bom-compare-edition">目标版次</label>
          <input
            id="bom-compare-edition"
            className="form-control"
            value={compareEdition}
            maxLength={2}
            placeholder="如 02"
            onChange={event => setCompareEdition(event.target.value.trim())}
          />
          <div className="form-text">按子件对齐，比用量 / 底数 / 损耗率 / 备注的原值。</div>
        </Modal>
      ) : null}

      {importOpen ? (
        <Modal
          title="取试算表（导入明细）"
          size="lg"
          onClose={() => setImportOpen(false)}
          footer={
            <>
              <Button onClick={() => setImportOpen(false)}>取消</Button>
              <Button
                variant="primary"
                disabled={importRows.length === 0}
                loading={importLines.isPending}
                onClick={() => importLines.mutate()}
              >
                导入 {importRows.length} 行
              </Button>
            </>
          }
        >
          <div className="mb-3">
            <label className="form-label" htmlFor="bom-import-file">CSV 文件</label>
            <input
              id="bom-import-file"
              type="file"
              accept=".csv,text/csv"
              className="form-control"
              onChange={async event => {
                const file = event.target.files?.[0]
                if (!file) return
                const text = await file.text()
                const parsed = parseBomCsv(text)
                setImportFileName(file.name)
                setImportRows(parsed.rows)
                setImportErrors(parsed.errors)
              }}
            />
            <div className="form-text">
              列顺序：料号, 子件版次, 用量, 底数, 损耗率, 备注。首行含「料号」会当作表头跳过；
              子件是半成品时版次必填（如 01），是材料时留空；项次由系统分配。
            </div>
          </div>
          <div className="form-check mb-3">
            <input
              id="bom-import-replace"
              type="checkbox"
              className="form-check-input"
              checked={importReplace}
              onChange={event => setImportReplace(event.target.checked)}
            />
            <label className="form-check-label" htmlFor="bom-import-replace">
              替换原有明细（不勾选为追加）
            </label>
          </div>
          {importErrors.length > 0 ? (
            <div className="alert alert-warning mb-2">
              {importErrors.slice(0, 8).map((message, index) => <div key={index}>{message}</div>)}
              {importErrors.length > 8 ? <div>……共 {importErrors.length} 个问题</div> : null}
            </div>
          ) : null}
          {importRows.length > 0 ? (
            <div className="text-muted">
              已解析 {importRows.length} 行{importFileName ? `（${importFileName}）` : ''}，确认后写入当前版次。
            </div>
          ) : null}
        </Modal>
      ) : null}

      {chooser && chooserSource ? (
        <UnifiedChooser
          open
          title={`${chooser.field.label}（${chooser.field.choosers.find(item => item.active && item.table)?.description ?? '选择' }）`}
          source={chooserSource}
          mode={chooser.field.chooseMultiple ? 'multi' : 'single'}
          masterValues={masterValues}
          onPick={rows => { if (rows.length > 0) applyChooser(rows[0]) }}
          onClose={() => setChooser(null)}
          resizable
          storageKey={`chooser-${MODULE_ID}-${chooser.field.key}`}
          emptyText="没有可选数据。"
        />
      ) : null}
    </div>
  )
}

/** 服务端下发的单元格值统一转成受控字符串（null → 空串、数字保真）。 */
function toText(value: unknown): string {
  if (value === null || value === undefined) return ''
  return typeof value === 'string' ? value : String(value)
}
