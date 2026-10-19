import { useRef, useState } from 'react'
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnDef, RowSelectionState } from '../../lib/tanstackTable'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpCommandBar, type ErpCommandItem } from '../../components/common/ErpCommandBar'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { useToast } from '../../components/ui/toastContext'
import { apiClient } from '../../services/api'
import { copyDiagnostics, currentAppInfo } from '../../lib/diagnostics'
import { describeApiError } from '../../lib/errors'
import { formatTimestamp } from './timestamp'

interface LogFileRow {
  name: string
  sizeBytes: number
  lastWriteTime: string
}

interface LogFilesResponse {
  files: LogFileRow[]
  totalBytes: number
  levels: string[]
}

interface LogEntryRow {
  ts: string | null
  level: string | null
  category: string | null
  event: string | null
  message: string | null
  correlationId: string | null
  moduleId: string | null
  errorCode: string | null
  exception: string | null
  file: string
}

interface LogQueryResponse {
  entries: LogEntryRow[]
  count: number
  truncated: boolean
  /** 想读但读不到的文件：读失败不等于没有日志，必须让用户看得见 */
  unavailable: string[]
}

/**
 * 一次读多少条：服务端按时间倒序读到这个条数就停（`LogFileReader` 上限 2000）。
 * 不摆“最近 N 条”控件——它是**一次读取的窗口大小**，不是用户要做的决定；
 * 读更多靠缩小条件（关键字 / 级别 / 某一天），而不是把这个数字调大。
 */
const TAKE = 500

/**
 * 日志管理（定制页 /admin/logs）：读系统运行日志，一屏一张电子表格。
 *
 * 这里读的是**磁盘上的运行日志文件**（只留 Warning+），回答"这次请求为什么失败"；
 * "谁改的、改成什么"是另一层——操作审计（库表 AUDIT_EVENT，模块 2316），它是统一工作台的
 * 只读承载页（`/workbench/2316`），不在这个页面里。
 * 诊断信息（版本/构建/健康检查）不再占一张卡，改为「下载诊断包 / 复制诊断信息」两个动作随取。
 *
 * 列表与统一工作台同形制：首列复选框、列宽可拖（本地持久化）、行点击单选、表头本地排序
 * （数据是一次读进来的单个快照，没有服务端翻页）。
 *
 * 权限由路由（模块 2313 的读权限）与服务端（同一模块 2313 的 CanBrowse / CanSetup）双重把关；
 * 打包下载额外要求设置权限，未授权时后端返回 403，页面提示原因而不是静默失败。
 */
export function LogAdminPage() {
  const { notify } = useToast()
  const queryClient = useQueryClient()
  const [keyword, setKeyword] = useState('')
  const [level, setLevel] = useState('')
  // 按天切分后一个文件就是一天：空表示跨全部文件按时间倒序读
  const [file, setFile] = useState('')
  const [rowSelection, setRowSelection] = useState<RowSelectionState>({})
  const [downloading, setDownloading] = useState(false)
  const fitRef = useRef<(() => Record<string, number>) | null>(null)

  const files = useQuery({
    queryKey: ['logs-files'],
    queryFn: () => apiClient.get<LogFilesResponse>('/logs/files'),
  })

  const logs = useQuery({
    queryKey: ['logs-query', keyword, level, file],
    // 改关键字/级别时不收掉旧结果：整块转圈会让日志列表"消失-出现"闪一下
    placeholderData: keepPreviousData,
    queryFn: () => apiClient.get<LogQueryResponse>('/logs', {
      query: { take: TAKE, keyword: keyword || undefined, level: level || undefined, file: file || undefined },
    }),
  })

  const refreshAll = () => {
    setRowSelection({})
    void queryClient.invalidateQueries({ queryKey: ['logs-files'] })
    void queryClient.invalidateQueries({ queryKey: ['logs-query'] })
  }

  const downloadBundle = async () => {
    setDownloading(true)
    try {
      const blob = await apiClient.getFile('/logs/bundle')
      const url = URL.createObjectURL(blob)
      const anchor = document.createElement('a')
      anchor.href = url
      anchor.download = `eos-diagnostics-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '')}.zip`
      document.body.appendChild(anchor)
      anchor.click()
      anchor.remove()
      URL.revokeObjectURL(url)
      notify({ variant: 'success', message: '诊断包已生成并开始下载。' })
    } catch (error) {
      notify({ variant: 'danger', message: describeApiError(error, '诊断包生成失败，请稍后重试。') })
    } finally {
      setDownloading(false)
    }
  }

  const commands: ErpCommandItem[] = [
    { action: 'fit', onClick: () => fitRef.current?.() },
    { action: 'refresh', onClick: refreshAll },
    { action: 'export', title: '下载诊断包', loading: downloading, onClick: () => void downloadBundle() },
    { action: 'copy', title: '复制诊断信息', onClick: () => copyDiagnostics(currentAppInfo(), undefined, '来自日志管理页') },
  ]

  const columns: ColumnDef<LogEntryRow, unknown>[] = [
    {
      id: 'select',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'erp-select-column', frozenLeft: true, resizable: false, truncate: false },
      header: ({ table }) => (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label="选择当前页"
          checked={table.getIsAllPageRowsSelected()}
          ref={(input) => { if (input) input.indeterminate = table.getIsSomePageRowsSelected() }}
          onChange={table.getToggleAllPageRowsSelectedHandler()}
        />
      ),
      cell: ({ row }) => (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label="选择此行"
          checked={row.getIsSelected()}
          onChange={row.getToggleSelectedHandler()}
          onClick={(event) => event.stopPropagation()}
        />
      ),
    },
    {
      accessorKey: 'ts',
      header: '时间',
      // 悬停提示走默认的原始值（写盘格式带毫秒与时区后缀）：显示到秒是为了扫读
      meta: { className: 'text-nowrap' },
      cell: (info) => formatTimestamp(info.getValue() as string | null),
    },
    {
      accessorKey: 'level',
      header: '级别',
      meta: { className: 'text-nowrap' },
      cell: (info) => {
        const value = String(info.getValue() ?? '')
        // 日志级别不是单据状态，用 Bootstrap 的 lt 底色直接标（error 红、warning 黄、其余中性）
        const color = value === 'Error' || value === 'Critical' ? 'red' : value === 'Warning' ? 'yellow' : 'secondary'
        return <span className={`badge bg-${color}-lt`}>{value || '—'}</span>
      },
    },
    { accessorKey: 'event', header: '事件', cell: (info) => <span className="font-monospace small">{String(info.getValue() ?? '—')}</span> },
    { accessorKey: 'message', header: '消息', cell: (info) => String(info.getValue() ?? '—') },
    { accessorKey: 'correlationId', header: '报障编号', cell: (info) => <span className="font-monospace small">{String(info.getValue() ?? '—')}</span> },
    { accessorKey: 'moduleId', header: '模块', meta: { className: 'text-end text-nowrap' }, cell: (info) => String(info.getValue() ?? '—') },
    { accessorKey: 'errorCode', header: '错误码', cell: (info) => <span className="font-monospace small">{String(info.getValue() ?? '—')}</span> },
  ]

  const logsError = describeApiError(logs.error, '日志查询失败。')
  const unavailable = logs.data?.unavailable ?? []

  return (
    <div className="erp-full-list-page">
      <ErpListCard
        ariaLabel="运行日志查询"
        search={(
          <div className="d-flex gap-2 align-items-center">
            <select
              className="form-select form-select-sm"
              style={{ width: '15rem' }}
              value={file}
              onChange={(event) => { setRowSelection({}); setFile(event.target.value) }}
              aria-label="日志文件"
            >
              <option value="">全部日期</option>
              {(files.data?.files ?? []).map((item) => (
                <option key={item.name} value={item.name}>{item.name}（{formatBytes(item.sizeBytes)}）</option>
              ))}
            </select>
            <ErpSearchBox value={keyword} onChange={(value) => { setRowSelection({}); setKeyword(value) }} debounceMs={300} placeholder="按消息/异常/类别关键字过滤…" />
            <select
              className="form-select form-select-sm"
              style={{ width: '9rem' }}
              value={level}
              onChange={(event) => { setRowSelection({}); setLevel(event.target.value) }}
              aria-label="日志级别"
            >
              <option value="">全部级别</option>
              {(files.data?.levels ?? []).map((item) => <option key={item} value={item}>{item}</option>)}
            </select>
          </div>
        )}
        actions={<ErpCommandBar items={commands} />}
        footer={(
          <div className="d-flex align-items-center gap-2 w-100">
            <span className="text-secondary small text-nowrap">
              命中 {logs.data?.count ?? 0} 条 · {files.data?.files.length ?? 0} 个文件（{formatBytes(files.data?.totalBytes ?? 0)}）
            </span>
            {logs.data?.truncated && (
              <span className="text-secondary small text-nowrap">已达 {TAKE} 条上限，更早的记录未显示——按关键字或日期缩小范围</span>
            )}
            {unavailable.length > 0 && (
              <span className="text-danger small" title={unavailable.join('、')}>
                有 {unavailable.length} 个文件读取失败：{unavailable.join('、')}
                ——它们的内容未计入上表，不等于这些文件里没有日志。
              </span>
            )}
            <span className="text-secondary small ms-auto text-nowrap">正常请求不落盘，属预期</span>
          </div>
        )}
      >
        {logs.isPending ? <LoadingState label="正在读取日志…" /> : logs.isError ? (
          <ErrorState message={logsError} onRetry={() => void logs.refetch()} />
        ) : (
          <ErpTable
            columns={columns}
            data={logs.data?.entries ?? []}
            loading={logs.isFetching}
            getRowId={(row: LogEntryRow) => `${row.file}:${row.ts}:${row.message ?? ''}:${row.correlationId ?? ''}`}
            rowSelection={rowSelection}
            onRowSelectionChange={setRowSelection}
            rowClickSingleSelect
            clientSideSorting
            virtualize
            resizable
            fitRef={fitRef}
            storageKey="log-admin-entries"
            empty={<EmptyState title="没有命中日志" description="换个关键字或放宽级别再试；正常请求不落盘，属预期。" />}
          />
        )}
      </ErpListCard>
    </div>
  )
}

function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit += 1
  }
  return `${value.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`
}
