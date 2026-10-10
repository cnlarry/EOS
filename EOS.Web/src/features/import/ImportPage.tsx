import {
  IconArrowsExchange,
  IconArrowLeft,
  IconArrowRight,
  IconDatabaseImport,
  IconFileSpreadsheet,
  IconLayoutGrid,
  IconTable,
} from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { useCallback, useEffect, useMemo, useRef, useState, type ComponentType } from 'react'
import type { ColumnDef } from '../../lib/tanstackTable'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { radioSelectColumn } from '../../components/common/erpRadioSelectColumn'
import { Button } from '../../components/ui/Button'
import { describeApiError } from '../../lib/errors'
import {
  downloadBlob,
  fetchImportDefinition,
  fetchImportMapping,
  fetchImportReadiness,
  fetchImportTargets,
  fetchImportTemplate,
  parseImportFile,
  runImport,
  saveImportMapping,
} from './importApi'
import { applySavedMapping, missingRequiredKeys, toMappingEntries } from './importMapping'
import { buildRowStates, countStatuses, filterIndexes, workSignature, type RowState } from './importWorkbench'
import type { ImportDefinitionInfo, ImportFileData, ImportMappingSnapshot, ImportRunResult, ImportTarget } from './types'

type Busy = 'parse' | 'preview' | 'import' | 'mapping' | 'template'
type StatusFilter = 'all' | 'failed'
type LogTone = 'ok' | 'bad'

interface MappingRow {
  index: number
  source: string
}

interface GridRow {
  rowKey: string
  index: number
  cells: string[]
}

interface LogEntry {
  at: string
  text: string
  tone?: LogTone
}

interface StepMeta {
  label: string
  Icon: ComponentType<{ size?: number }>
  title: string
}

/** 一次导入的最大步数：一步一步来，前一步的界面在下一次点「下一步」时消失。 */
const STEPS: StepMeta[] = [
  { label: '选择模块', Icon: IconLayoutGrid, title: '第 1 步：选择目标模块' },
  { label: '选择文档', Icon: IconFileSpreadsheet, title: '第 2 步：选择数据文档' },
  { label: '字段映射', Icon: IconArrowsExchange, title: '第 3 步：字段映射' },
  { label: '数据预览', Icon: IconTable, title: '第 4 步：数据预览' },
  { label: '开始导入', Icon: IconDatabaseImport, title: '第 5 步：开始导入' },
]

/** 日志里逐行列出的上限：再多就不是给人看的了，失败行另有导出。 */
const MAX_LOG_ROWS = 1000

function csvCell(value: string) {
  return value.includes(',') || value.includes('"') || value.includes('\n') || value.includes('\r')
    ? `"${value.replace(/"/g, '""')}"`
    : value
}

function stamp() {
  return new Date().toLocaleTimeString('zh-CN', { hour12: false })
}

export function ImportPage() {
  const [step, setStep] = useState(1)
  const [target, setTarget] = useState<ImportTarget | null>(null)
  const [fileData, setFileData] = useState<ImportFileData | null>(null)
  // null = 用"记住的映射 / 自动匹配"的结果；用户改过某一列后转入显式映射
  const [manualMapping, setManualMapping] = useState<Record<number, string> | null>(null)
  const [preview, setPreview] = useState<ImportRunResult | null>(null)
  // 预演结论对应的映射指纹：等价于"这份结论还算不算数"，进入预览步按它决定要不要重跑
  const [previewSignature, setPreviewSignature] = useState<string | null>(null)
  const [executed, setExecuted] = useState<ImportRunResult | null>(null)
  const [busy, setBusy] = useState<Busy | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('all')
  const [log, setLog] = useState<LogEntry[]>([])
  const [dragOver, setDragOver] = useState(false)
  // 模块搜索词放在**页面级** state：从第 2 步退回第 1 步时关键词与筛出的结果都还在
  // （放步骤组件里会随步骤卸载而丢）
  const [keyword, setKeyword] = useState('')
  const fileInputRef = useRef<HTMLInputElement | null>(null)
  const mappingInputRef = useRef<HTMLInputElement | null>(null)
  const logRef = useRef<HTMLDivElement | null>(null)

  const targets = useQuery({ queryKey: ['import', 'targets'], queryFn: fetchImportTargets })
  const moduleId = target?.moduleId ?? null
  const definition = useQuery({
    queryKey: ['import', 'definition', moduleId],
    enabled: moduleId != null,
    retry: false,
    queryFn: () => {
      if (moduleId == null) throw new Error('未选择目标模块。')
      return fetchImportDefinition(moduleId)
    },
  })
  const definitionReady = definition.isSuccess && definition.data != null
  const savedMapping = useQuery({
    queryKey: ['import', 'mapping', moduleId],
    enabled: moduleId != null && definitionReady,
    retry: false,
    queryFn: () => (moduleId == null ? null : fetchImportMapping(moduleId)),
  })
  const readiness = useQuery({
    queryKey: ['import', 'readiness', moduleId],
    enabled: moduleId != null && definitionReady,
    retry: false,
    queryFn: () => (moduleId == null ? [] : fetchImportReadiness(moduleId)),
  })

  const definitionData: ImportDefinitionInfo | null = definitionReady ? definition.data : null
  const definitionError = definition.isError ? describeApiError(definition.error, '这个模块无法导入。') : null
  const savedSnapshot: ImportMappingSnapshot | null = savedMapping.data ?? null

  const baselineMapping = useMemo(
    () => (fileData && definitionData ? applySavedMapping(fileData.columns, savedSnapshot, definitionData) : {}),
    [fileData, definitionData, savedSnapshot],
  )
  const mapping = manualMapping ?? baselineMapping
  const updateMapping = useCallback((index: number, key: string) => {
    setManualMapping((current) => ({ ...(current ?? baselineMapping), [index]: key }))
    // 映射变了，之前的预演结论与导入结果都不再对应当前数据
    setPreview(null)
    setPreviewSignature(null)
    setExecuted(null)
  }, [baselineMapping])

  const missingKeys = useMemo(
    () => (definitionData ? missingRequiredKeys(definitionData, mapping) : []),
    [definitionData, mapping],
  )

  const signature = fileData ? workSignature(fileData.columns, mapping, fileData.rows.length) : ''
  const needsPreview = fileData != null && executed == null && previewSignature !== signature
  const rowStates = useMemo(
    () => buildRowStates(executed ?? preview, fileData?.rows.length ?? 0, mapping),
    [executed, preview, fileData, mapping],
  )
  const counts = useMemo(() => countStatuses(rowStates), [rowStates])
  const gridRows: GridRow[] = useMemo(() => {
    if (!fileData) return []
    return filterIndexes(rowStates, statusFilter).map((index) => ({
      rowKey: `r${index}`,
      index,
      cells: fileData.rows[index] ?? [],
    }))
  }, [fileData, rowStates, statusFilter])

  useEffect(() => {
    if (logRef.current) logRef.current.scrollTop = logRef.current.scrollHeight
  }, [log])

  const appendLog = useCallback((entries: LogEntry[]) => {
    setLog((current) => [...current, ...entries])
  }, [])

  const buildRequest = useCallback(() => {
    if (!fileData) return null
    return {
      columns: fileData.columns,
      mapping: fileData.columns.map((_, index) => mapping[index] ?? null),
      rows: fileData.rows,
      sourceName: fileData.sourceName,
    }
  }, [fileData, mapping])

  const pickTarget = useCallback((row: ImportTarget) => {
    setTarget(row)
    setError(null)
    setFileData(null)
    setManualMapping(null)
    setPreview(null)
    setPreviewSignature(null)
    setExecuted(null)
    setLog([])
    setStatusFilter('all')
  }, [])

  const upload = useCallback(async (file: File) => {
    setBusy('parse')
    setError(null)
    setManualMapping(null)
    setPreview(null)
    setPreviewSignature(null)
    setExecuted(null)
    setLog([])
    setStatusFilter('all')
    try {
      setFileData(await parseImportFile(file))
    } catch (reason) {
      setFileData(null)
      setError(describeApiError(reason, '文件解析失败。'))
    } finally {
      setBusy(null)
    }
  }, [])

  const ensurePreview = useCallback(async () => {
    if (moduleId == null || !needsPreview) return
    const request = buildRequest()
    if (!request) return
    const currentSignature = fileData ? workSignature(fileData.columns, mapping, fileData.rows.length) : ''
    setBusy('preview')
    setError(null)
    try {
      const outcome = await runImport(moduleId, request, true)
      setPreview(outcome)
      setPreviewSignature(currentSignature)
      setStatusFilter(outcome.failed > 0 ? 'failed' : 'all')
      // 这份映射已被证明确实可用：记住它（补导、换机器回来不用重配）
      if (fileData) {
        void saveImportMapping(moduleId, fileData.sourceName, toMappingEntries(fileData.columns, mapping)).catch(() => undefined)
      }
    } catch (reason) {
      setError(describeApiError(reason, '预演校验失败。'))
    } finally {
      setBusy(null)
    }
  }, [buildRequest, fileData, mapping, moduleId, needsPreview])

  const startImport = useCallback(async () => {
    if (moduleId == null || !fileData) return
    const request = buildRequest()
    if (!request) return
    setBusy('import')
    setError(null)
    const mapped = fileData.columns
      .map((column, index) => (mapping[index] ? `${column} → ${mapping[index]}` : null))
      .filter((text): text is string => text != null)
    setLog([
      { at: stamp(), text: `目标模块：${definitionData?.title ?? ''}（${moduleId}）｜主表 ${definitionData?.masterTable ?? ''}` },
      { at: stamp(), text: `来源文件：${fileData.sourceName}（${fileData.rows.length} 行 × ${fileData.columns.length} 列）` },
      { at: stamp(), text: `字段映射：${mapped.join('，') || '（无）'}` },
      { at: stamp(), text: `开始导入 ${preview?.succeeded ?? 0} 行（逐行独立提交，失败行不影响已成功的行）…` },
    ])
    try {
      const outcome = await runImport(moduleId, request, false)
      setExecuted(outcome)
      const detail = (state: (typeof outcome.rows)[number]) =>
        (state.fieldErrors ?? []).length > 0
          ? `（${(state.fieldErrors ?? []).map((issue) => `${issue.field}：${issue.message}`).join('；')}）`
          : ''
      const lines: LogEntry[] = outcome.rows.slice(0, MAX_LOG_ROWS).map((row) => row.ok
        ? { at: stamp(), text: `第 ${row.rowNumber} 行 ✓ 已导入`, tone: 'ok' as const }
        : {
          at: stamp(),
          text: `第 ${row.rowNumber} 行 ✗ ${row.message ?? '导入失败'}${row.code ? ` [${row.code}]` : ''}${detail(row)}`,
          tone: 'bad' as const,
        })
      if (outcome.rows.length > MAX_LOG_ROWS) {
        lines.push({ at: stamp(), text: `…其余 ${outcome.rows.length - MAX_LOG_ROWS} 行未逐行列出（失败行可导出成 CSV）` })
      }
      lines.push({
        at: stamp(),
        text: `导入完成：成功 ${outcome.succeeded} 行、失败 ${outcome.failed} 行（已提交）`,
        tone: outcome.failed > 0 ? 'bad' : 'ok',
      })
      lines.push({ at: stamp(), text: '审计：本次已记入操作审计（逐行 INSERT + 一条批次 IMPORT）' })
      appendLog(lines)
    } catch (reason) {
      const message = describeApiError(reason, '导入失败。')
      setError(message)
      appendLog([{ at: stamp(), text: `导入失败：${message}`, tone: 'bad' }])
    } finally {
      setBusy(null)
    }
  }, [appendLog, buildRequest, definitionData, fileData, mapping, moduleId, preview])

  const restart = useCallback(() => {
    setStep(1)
    setTarget(null)
    setFileData(null)
    setManualMapping(null)
    setPreview(null)
    setPreviewSignature(null)
    setExecuted(null)
    setError(null)
    setLog([])
    setStatusFilter('all')
  }, [])

  const downloadTemplate = useCallback(async () => {
    if (moduleId == null) return
    setBusy('template')
    setError(null)
    try {
      downloadBlob(await fetchImportTemplate(moduleId), `${definitionData?.title ?? '导入'}-导入模板.csv`)
    } catch (reason) {
      setError(describeApiError(reason, '模板下载失败。'))
    } finally {
      setBusy(null)
    }
  }, [definitionData, moduleId])

  const exportFailures = useCallback(() => {
    const source = executed ?? preview
    if (!fileData || source == null) return
    const lines = [['行号', ...fileData.columns, '失败原因', '错误码', '字段级说明'].map(csvCell).join(',')]
    for (const outcome of source.rows) {
      if (outcome.ok) continue
      const cells = fileData.rows[outcome.rowNumber - 2] ?? []
      const fields = (outcome.fieldErrors ?? []).map((issue) => `${issue.field}：${issue.message}`).join('；')
      lines.push([
        String(outcome.rowNumber),
        ...fileData.columns.map((_, columnIndex) => cells[columnIndex] ?? ''),
        outcome.message ?? '',
        outcome.code ?? '',
        fields,
      ].map(csvCell).join(','))
    }
    downloadBlob(new Blob([`\ufeff${lines.join('\r\n')}\r\n`], { type: 'text/csv;charset=utf-8' }), '导入失败明细.csv')
  }, [executed, fileData, preview])

  const exportMapping = useCallback(() => {
    if (!fileData || !definitionData || moduleId == null) return
    downloadBlob(
      new Blob([JSON.stringify({
        version: 1,
        moduleId,
        title: definitionData.title,
        masterTable: definitionData.masterTable,
        entries: toMappingEntries(fileData.columns, mapping),
      }, null, 2)], { type: 'application/json' }),
      `${definitionData.title}-映射.json`,
    )
  }, [definitionData, fileData, mapping, moduleId])

  const importMappingFile = useCallback(async (file: File) => {
    if (!fileData || !definitionData) return
    try {
      const raw = (await file.text()).replace(/^\ufeff/, '')
      const parsed = JSON.parse(raw) as { entries?: { column?: unknown; field?: unknown }[] }
      const entries = (parsed.entries ?? [])
        .map((entry) => ({ column: String(entry.column ?? ''), field: entry.field == null ? null : String(entry.field) }))
        .filter((entry) => entry.column.length > 0)
      if (entries.length === 0) {
        setError('这份映射文件里没有可用的条目。')
        return
      }
      setManualMapping(applySavedMapping(fileData.columns, {
        moduleId: definitionData.moduleId,
        sourceName: file.name,
        entries,
        updatedAt: null,
        updatedBy: '',
      }, definitionData))
      setPreview(null)
      setPreviewSignature(null)
      setExecuted(null)
    } catch {
      setError('映射文件解析失败：请确认它是本页「导出映射」产出的 JSON。')
    }
  }, [definitionData, fileData])

  // 进入某一步该做什么：预览步要先把结论算出来，最后一步要真的开始写库
  const enterStep = useCallback((next: number) => {
    setError(null)
    setStep(next)
    if (next === 4) void ensurePreview()
    if (next === 5) void startImport()
  }, [ensurePreview, startImport])

  /** 下一步的可用性与"为什么不能走"：禁用必须给得出原因。 */
  const nextGate = (() => {
    switch (step) {
      case 1:
        return target == null ? '先在上面点一行选中目标模块' : null
      case 2:
        return fileData == null ? '先选择要导入的数据文件' : null
      case 3:
        return missingKeys.length > 0 ? `还有必须映射的字段没有对应列：${missingKeys.join('、')}` : null
      case 4:
        if (busy === 'preview') return '正在校验…'
        if (preview == null) return '还没有校验结论'
        return preview.succeeded === 0 ? '预演里没有可导入的行' : null
      default:
        return null
    }
  })()
  const canGoBack = step > 1 && busy == null
  const canGoNext = nextGate == null && busy == null
  // 第 4 步的按钮推进的是**写库**，所以它不叫「下一步」——就四个字。
  // 不带行数：`开始导入 2 行` 会被读成"只导 2 行"（实际是这一批里通过的全导），
  // 而"这一批有多少行、能导多少行"这一屏的计数条已经写清楚了（共 N 行：可导入 M ｜ 失败 K）。
  // 不能推进时按钮禁用，原因由 title 给出（正在校验… / 还没有校验结论 / 预演里没有可导入的行）。
  const nextLabel = step === 4 ? '开始导入' : '下一步'

  const goBack = () => {
    if (!canGoBack) return
    setError(null)
    setStep(step - 1)
  }

  const goNext = () => {
    if (!canGoNext || step >= 5) return
    enterStep(step + 1)
  }

  /** 进度条节点：可点回看（已经过的那几步），不可达的节点说明原因。 */
  const runNode = (index: number) => {
    const candidate = index + 1
    if (candidate === step) return
    if (busy != null) return
    const blocked = (() => {
      if (candidate === 1) return null
      if (candidate === 2) return target == null ? '先选择目标模块' : null
      if (candidate === 3) return fileData == null ? '先选择数据文件' : null
      if (candidate === 4) {
        if (fileData == null) return '先选择数据文件'
        return missingKeys.length > 0 ? '字段映射还没配齐' : null
      }
      if (executed != null) return null
      if (preview == null) return '先做一次数据预览'
      return preview.succeeded === 0 ? '预演里没有可导入的行' : null
    })()
    if (blocked != null) {
      setError(`还不能${STEPS[index].label}：${blocked}。`)
      return
    }
    setError(null)
    setStep(candidate)
    if (candidate === 4) void ensurePreview()
    if (candidate === 5) void startImport()
  }

  // 模块候选全量已在前端（约 260 条），因此搜索就是本地过滤：不发请求、输入即出结果，
  // 顺带让"关键词保持"就等于"筛选结果保持"（结果的唯一来源就是关键词）
  const filteredTargets = useMemo(() => {
    const all = targets.data ?? []
    const text = keyword.trim().toLowerCase()
    if (text.length === 0) return all
    return all.filter((item) =>
      String(item.moduleId).includes(text)
      || item.title.toLowerCase().includes(text)
      || item.masterTable.toLowerCase().includes(text))
  }, [keyword, targets.data])
  const selectedTargetId = target != null ? String(target.moduleId) : null
  const pickTargetById = useCallback((rowId: string) => {
    const row = (targets.data ?? []).find((item) => String(item.moduleId) === rowId)
    if (row != null) pickTarget(row)
  }, [pickTarget, targets.data])

  const targetColumns = useMemo<ColumnDef<ImportTarget, unknown>[]>(() => [
    // 首列是**单选**列（与统一选择器单选模式同款）：目标模块只能选一个，给复选框是错的语义。
    // 单选态由页面自己维护（selectedTargetId），不借用表格的多选行状态。
    radioSelectColumn<ImportTarget>('import-target', selectedTargetId, pickTargetById),
    {
      id: 'moduleId',
      header: '模块号',
      accessorFn: (row) => row.moduleId,
      cell: ({ row }) => row.original.moduleId,
      meta: { resizable: false },
    },
    {
      id: 'title',
      header: '模块名',
      accessorFn: (row) => row.title,
      cell: ({ row }) => <span className="fw-medium">{row.original.title}</span>,
    },
    {
      id: 'masterTable',
      header: '主表',
      accessorFn: (row) => row.masterTable,
    },
  ], [pickTargetById, selectedTargetId])

  const mappingColumns = useMemo<ColumnDef<MappingRow, unknown>[]>(() => {
    if (!definitionData) return []
    const required = new Set(definitionData.requiredKeys)
    const options = [...definitionData.fields].sort((left, right) => {
      const rank = (field: { key: string }) => (required.has(field.key) ? 0 : 1)
      return rank(left) - rank(right) || left.label.localeCompare(right.label, 'zh-Hans-CN')
    })
    return [
      {
        id: 'source',
        header: '数据文件列',
        cell: ({ row }) => <span className="fw-medium">{row.original.source}</span>,
        meta: { resizable: false },
      },
      {
        id: 'target',
        header: '导入到字段',
        cell: ({ row }) => (
          <select
            className="form-select form-select-sm"
            aria-label={`${row.original.source} 映射到`}
            value={mapping[row.original.index] ?? ''}
            onChange={(event) => updateMapping(row.original.index, event.target.value)}
          >
            <option value="">不导入</option>
            {options.map((item) => (
              <option key={item.key} value={item.key}>
                {item.label}{required.has(item.key) ? '（必填）' : ''}（{item.key}）
              </option>
            ))}
          </select>
        ),
        meta: { resizable: false },
      },
    ]
  }, [definitionData, mapping, updateMapping])

  const gridColumns = useMemo<ColumnDef<GridRow, unknown>[]>(() => {
    if (!fileData) return []
    const columns: ColumnDef<GridRow, unknown>[] = [
      {
        id: '__status',
        header: '状态',
        enableSorting: false,
        meta: { resizable: false, frozenLeft: true },
        cell: ({ row }) => <RowBadge state={rowStates[row.original.index]} />,
      },
    ]
    fileData.columns.forEach((column, index) => {
      columns.push({
        id: `c${index}`,
        header: column,
        cell: ({ row }) => {
          const issue = rowStates[row.original.index]?.cellIssues[index]
          const text = row.original.cells[index] ?? ''
          return issue ? <span className="erp-import-cell-bad" title={issue}>{text}</span> : text
        },
      })
    })
    columns.push({
      id: '__verdict',
      header: '校验结论',
      enableSorting: false,
      cell: ({ row }) => <RowVerdict state={rowStates[row.original.index]} />,
    })
    return columns
  }, [fileData, rowStates])

  // 向导导航只此两个按钮，与「第 N 步」标题同一行、靠右对齐；进度条上的节点是唯一的例外（可点回看）
  const navigation = (
    <>
      <Button size="sm" icon={<IconArrowLeft size={16} />} disabled={!canGoBack} onClick={goBack}>上一步</Button>
      {step < 5 && (
        <Button
          size="sm"
          variant="primary"
          icon={<IconArrowRight size={16} />}
          disabled={!canGoNext}
          loading={busy === 'preview'}
          title={nextGate ?? (step === 4 ? '把这一批写进库里' : '进入下一步')}
          onClick={goNext}
        >
          {nextLabel}
        </Button>
      )}
      {step === 5 && busy === 'import' && (
        <Button size="sm" variant="primary" loading>导入中…</Button>
      )}
      {step === 5 && busy !== 'import' && (
        <Button size="sm" variant="primary" icon={<IconArrowRight size={16} />} onClick={restart}>
          {executed != null ? '重新开始' : '再导一批'}
        </Button>
      )}
    </>
  )

  const current = STEPS[step - 1]

  if (targets.isPending) {
    return (
      <div className="d-grid erp-import-page">
        <ErpListCard ariaLabel="基本资料导入" search={null}><LoadingState label="正在加载可导入模块…" /></ErpListCard>
      </div>
    )
  }
  if (targets.isError) {
    return (
      <div className="d-grid erp-import-page">
        <ErpListCard ariaLabel="基本资料导入" search={null}>
          <ErrorState message={describeApiError(targets.error, '导入功能加载失败。')} onRetry={() => void targets.refetch()} />
        </ErpListCard>
      </div>
    )
  }

  const readyMilestones = [
    target != null,
    fileData != null,
    fileData != null && missingKeys.length === 0,
    (preview != null && counts.unknown === 0) || executed != null,
    executed != null,
  ]

  return (
    <div className="d-grid erp-import-page">
      <ErpListCard ariaLabel="基本资料导入">
        <div className="erp-import-body">
          <ol className="erp-import-track" aria-label="导入步骤">
            {STEPS.map((item, index) => {
              const isActive = index + 1 === step
              const isDone = !isActive && readyMilestones[index]
              const className = `erp-import-item${isActive ? ' is-active' : isDone ? ' is-done' : ''}`
              const { Icon } = item
              return (
                <li key={item.label} className={className}>
                  <button
                    type="button"
                    className="erp-import-node"
                    aria-current={isActive ? 'step' : undefined}
                    title={`第 ${index + 1} 步：${item.label}`}
                    onClick={() => runNode(index)}
                  >
                    <span className="erp-import-node-mark"><Icon size={16} /></span>
                    <span className="erp-import-node-label">{item.label}</span>
                  </button>
                </li>
              )
            })}
          </ol>

          {(busy != null || error != null) && (
            <div className="erp-import-status">
              {busy != null ? (
                <span className="erp-import-hint">
                  <span className="spinner-border spinner-border-sm me-2" role="status" aria-hidden="true" />
                  {busy === 'parse' ? '正在解析文件…'
                    : busy === 'preview' ? '正在校验（真实事务内执行后回滚）…'
                      : busy === 'import' ? '正在导入（逐行独立提交）…'
                        : busy === 'template' ? '正在生成模板…'
                          : '正在套用映射…'}
                </span>
              ) : (
                <span className="erp-import-hint text-danger">{error}</span>
              )}
            </div>
          )}

          <section className="erp-import-step">
            {/* 步骤标题与「上一步/下一步」同一行：按钮紧挨它要推进的那一步，比挂在卡片顶部更好读 */}
            <div className="erp-import-step-head">
              <span className="erp-import-step-title">{current.title}</span>
              <div className="erp-import-step-actions">{navigation}</div>
            </div>
            <div className="erp-import-step-body">
              {step === 1 && (
                <>
                  {/* 搜索框紧贴它要过滤的清单上方（不挂到卡片顶部的命令栏——那里离内容太远，
                      和「上一步/下一步」挤在一起也说不清是谁的）。模块共 260 个左右，靠翻找太慢。 */}
                  <div className="erp-import-search">
                    <ErpSearchBox
                      value={keyword}
                      onChange={setKeyword}
                      placeholder="搜索模块号 / 模块名 / 主表"
                      ariaLabel="搜索目标模块"
                    />
                    {keyword.trim().length > 0 && (
                      <span className="erp-import-hint">
                        匹配 {filteredTargets.length} / 共 {targets.data?.length ?? 0} 个模块
                      </span>
                    )}
                  </div>
                  <div className="erp-import-scroll">
                    <ErpTable
                      columns={targetColumns}
                      data={filteredTargets}
                      getRowId={(row) => String(row.moduleId)}
                      activeRowId={selectedTargetId ?? undefined}
                      onRowClick={pickTarget}
                      clientSideSorting
                      resizable
                      storageKey="import-targets"
                      empty={keyword.trim().length > 0
                        ? <EmptyState title="没有匹配的模块" description={`模块号 / 模块名 / 主表里都没有「${keyword.trim()}」，换个词试试。`} />
                        : <EmptyState title="没有可导入的模块" description="候选 = 统一表单写名单里的统一工作台模块。" />}
                    />
                  </div>
                  {/* 定义还在路上：用一行文字而不是整块 LoadingState——整块占位比它替换掉的那条
                      摘要高出近 300px，会把卡片撑起来再缩回（选模块时的抖动就是这么来的） */}
                  {target != null && definition.isPending && (
                    <span className="erp-import-hint">正在加载导入定义…</span>
                  )}
                  {definitionError != null && <div className="alert alert-warning mb-0 py-2">{definitionError}</div>}
                  {definitionData && (
                    <div className="erp-import-summary">
                      <span>已选：<span className="fw-medium">{definitionData.title}</span>（{definitionData.moduleId}）</span>
                      <span>主表 {definitionData.masterTable}</span>
                      <span>可填 {definitionData.fields.length} 列</span>
                      <span>必须映射 {definitionData.requiredKeys.join('、') || '无'}</span>
                    </div>
                  )}
                  {readiness.data != null && readiness.data.length > 0 && (
                    <div className="alert alert-warning mb-0 py-2">
                      以下前置资料还没有数据，引用它们的行会因"找不到对应记录"失败——建议先按顺序把它们导进来：
                      {readiness.data.map((item) => `${item.sourceLabel}（字段 ${item.field}）`).join('、')}。
                    </div>
                  )}
                </>
              )}

              {step === 2 && (
                <>
                  <button
                    type="button"
                    className={`erp-import-drop${dragOver ? ' is-over' : ''}`}
                    onClick={() => fileInputRef.current?.click()}
                    onDragOver={(event) => { event.preventDefault(); setDragOver(true) }}
                    onDragLeave={() => setDragOver(false)}
                    onDrop={(event) => {
                      event.preventDefault()
                      setDragOver(false)
                      const file = event.dataTransfer.files?.[0]
                      if (file) void upload(file)
                    }}
                  >
                    {busy === 'parse'
                      ? '正在解析文件…'
                      : fileData == null
                        ? '点这里选择 .csv / .txt / .tsv / .xlsx 文件，也可以直接把文件拖进来'
                        : `已载入 ${fileData.sourceName}（${fileData.rows.length} 行 × ${fileData.columns.length} 列）——点这里换一个文件`}
                  </button>
                  {fileData != null && fileData.truncated && (
                    <div className="alert alert-warning mb-0 py-2">
                      文件共 {fileData.totalRows} 行，超过单次上限，只载入了前 {fileData.rows.length} 行。请拆分后分批导入。
                    </div>
                  )}
                  {fileData != null && fileData.rows.length === 0 && (
                    <div className="alert alert-warning mb-0 py-2">文件里没有可导入的数据行（只有表头）。</div>
                  )}
                  <div className="erp-import-links">
                    <a
                      href="#"
                      onClick={(event) => { event.preventDefault(); void downloadTemplate() }}
                    >
                      下载空白模板（列名 = 字段标签）
                    </a>
                    <span className="text-secondary">模板里第一行就是表头，填好后直接拖进来即可自动匹配。</span>
                  </div>
                </>
              )}

              {step === 3 && fileData && (
                <>
                  <div className="erp-import-scroll">
                    <ErpTable
                      columns={mappingColumns}
                      data={fileData.columns.map((column, index) => ({ index, source: column }))}
                      getRowId={(row) => String(row.index)}
                      resizable
                      storageKey="import-mapping"
                      empty={<EmptyState title="文件没有列" description="请确认第一行是列名。" />}
                    />
                  </div>
                  {savedSnapshot != null && savedSnapshot.entries.length > 0 && (
                    <span className="text-secondary small">
                      已套用你上次在「{savedSnapshot.sourceName || '本模块'}」上确认过的映射（按列名比对，列顺序变了也照常对上）。
                    </span>
                  )}
                  {missingKeys.length > 0 && (
                    <div className="alert alert-warning mb-0 py-2">
                      还有必须映射的字段没有对应列：{missingKeys.join('、')}。补齐后才能进入下一步。
                    </div>
                  )}
                  <div className="erp-import-links">
                    <a href="#" onClick={(event) => { event.preventDefault(); exportMapping() }}>导出映射</a>
                    <a href="#" onClick={(event) => { event.preventDefault(); mappingInputRef.current?.click() }}>导入映射</a>
                    <span className="text-secondary">导出成 JSON 可以给同一个客户的其它实施人员复用。</span>
                  </div>
                </>
              )}

              {step === 4 && fileData && (
                <>
                  <div className="erp-import-panel-head">
                    <span className="erp-import-panel-title">
                      共 {fileData.rows.length} 行：可导入 {counts.ok}
                      {counts.failed > 0 && <> ｜ 失败 <span className="text-danger fw-medium">{counts.failed}</span></>}
                      {counts.unknown > 0 && <> ｜ 未校验 {counts.unknown}</>}
                    </span>
                    <select
                      className="form-select form-select-sm erp-import-filter-select"
                      aria-label="显示范围"
                      value={statusFilter}
                      onChange={(event) => setStatusFilter(event.target.value as StatusFilter)}
                    >
                      <option value="all">显示全部</option>
                      <option value="failed">只看失败</option>
                    </select>
                  </div>
                  <div className="erp-import-scroll">
                    <ErpTable
                      columns={gridColumns}
                      data={gridRows}
                      getRowId={(row) => row.rowKey}
                      rowClassName={(row) => (rowStates[row.index]?.status === 'failed' ? 'erp-import-row-bad' : undefined)}
                      resizable
                      storageKey="import-grid"
                      empty={<EmptyState title="没有符合当前筛选的行" description="把显示范围切回「显示全部」看看。" />}
                    />
                  </div>
                  {preview != null && preview.failed > 0 && (
                    <div className="erp-import-links">
                      <a href="#" onClick={(event) => { event.preventDefault(); exportFailures() }}>导出失败明细（原文件全部列 + 失败原因）</a>
                      <span className="text-secondary">改好后原样再传同一个文件：已导入的行会被主键冲突挡下，修好的行照常进库。</span>
                    </div>
                  )}
                </>
              )}

              {step === 5 && (
                <>
                  <div className="erp-import-log" ref={logRef} role="log" aria-label="导入日志">
                    {log.map((entry, index) => (
                      <div key={index} className={`erp-import-log-line${entry.tone ? ` is-${entry.tone}` : ''}`}>
                        <span className="erp-import-log-at">{entry.at}</span>
                        <span>{entry.text}</span>
                      </div>
                    ))}
                  </div>
                  {executed != null && (
                    <div className="erp-import-links">
                      {executed.failed > 0 && (
                        <a href="#" onClick={(event) => { event.preventDefault(); exportFailures() }}>导出失败明细</a>
                      )}
                      <span className="text-secondary">
                        {executed.failed > 0
                          ? '按明细改好后点「上一步」回到选择文档，换文件再导（映射已记住）。'
                          : '这一批已全部导入成功。'}
                      </span>
                    </div>
                  )}
                </>
              )}
            </div>
          </section>
        </div>
      </ErpListCard>

      <input
        ref={fileInputRef}
        type="file"
        accept=".csv,.txt,.tsv,.xlsx,.xlsm"
        className="d-none"
        onChange={(event) => {
          const file = event.target.files?.[0]
          event.target.value = ''
          if (file) void upload(file)
        }}
      />
      <input
        ref={mappingInputRef}
        type="file"
        accept=".json,application/json"
        className="d-none"
        onChange={(event) => {
          const file = event.target.files?.[0]
          event.target.value = ''
          if (file) void importMappingFile(file)
        }}
      />
    </div>
  )
}

function RowBadge({ state }: { state: RowState | undefined }) {
  if (state == null || state.status === 'unknown') return <span className="text-secondary">—</span>
  return state.status === 'ok'
    ? <span className="erp-import-badge-ok">可导入</span>
    : <span className="erp-import-badge-bad">失败</span>
}

function RowVerdict({ state }: { state: RowState | undefined }) {
  if (state == null || state.status === 'unknown') return <span className="text-secondary">未校验</span>
  if (state.status === 'ok') return <span className="text-secondary">—</span>
  return (
    <>
      <span className="erp-import-cell-bad">{state.message}</span>
      {state.code && <span className="text-secondary small d-block">{state.code}</span>}
      {state.rowIssues.map((issue) => <span key={issue} className="text-secondary small d-block">{issue}</span>)}
    </>
  )
}
