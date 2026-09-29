import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnDef } from '@tanstack/react-table'
import { IconDownload, IconRefresh } from '@tabler/icons-react'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { useToast } from '../../components/ui/toastContext'
import { apiClient } from '../../services/api'
import { copyDiagnostics, currentAppInfo } from '../../lib/diagnostics'
import { describeApiError } from '../../lib/errors'

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
}

interface Diagnostics {
  app: { version: string; commit: string; buildTimeUtc: string; assemblyVersion: string | null; environment: string }
  process: { startTimeUtc: string; binaryWriteTimeUtc: string | null; stale: boolean; machineName: string; osVersion: string }
  logs: { path: string; totalBytes: number; files: LogFileRow[] }
  health: Record<string, { status: string; description: string | null; elapsedMs: number }>
  migrations: { appliedCount: number | null; lastScript: string | null; embeddedCount: number; error: string | null }
  configDigest: Record<string, boolean>
  generatedAtUtc: string
}

/**
 * 日志管理（定制页 /admin/logs）：
 * 看系统诊断信息（版本/构建/健康检查/迁移台账）、按条件读运行日志、下载诊断包。
 * 权限由路由（模块 110112 的读权限）与服务端（模块 11 的 CanBrowse / CanSetup）双重把关；
 * 打包下载额外要求设置权限，未授权时后端返回 403，页面提示原因而不是静默失败。
 */
export function LogAdminPage() {
  const { notify } = useToast()
  const queryClient = useQueryClient()
  const [keyword, setKeyword] = useState('')
  const [level, setLevel] = useState('')
  const [take, setTake] = useState(200)
  const [downloading, setDownloading] = useState(false)

  const files = useQuery({
    queryKey: ['logs-files'],
    queryFn: () => apiClient.get<LogFilesResponse>('/logs/files'),
  })

  const diagnostics = useQuery({
    queryKey: ['logs-diagnostics'],
    queryFn: () => apiClient.get<Diagnostics>('/logs/diagnostics'),
  })

  const logs = useQuery({
    queryKey: ['logs-query', keyword, level, take],
    queryFn: () => apiClient.get<LogQueryResponse>('/logs', {
      query: { take, keyword: keyword || undefined, level: level || undefined },
    }),
  })

  const refreshAll = () => {
    void queryClient.invalidateQueries({ queryKey: ['logs-files'] })
    void queryClient.invalidateQueries({ queryKey: ['logs-diagnostics'] })
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

  const columns: ColumnDef<LogEntryRow, unknown>[] = [
    { accessorKey: 'ts', header: '时间', cell: (info) => <span className="font-monospace small">{String(info.getValue() ?? '—')}</span> },
    {
      accessorKey: 'level',
      header: '级别',
      cell: (info) => {
        const value = String(info.getValue() ?? '')
        // 日志级别不是单据状态，用 Bootstrap 的 lt 底色直接标（error 红、warning 黄、其余中性）
        const color = value === 'Error' || value === 'Critical' ? 'red' : value === 'Warning' ? 'yellow' : 'secondary'
        return <span className={`badge bg-${color}-lt`}>{value || '—'}</span>
      },
    },
    { accessorKey: 'event', header: '事件', cell: (info) => <span className="font-monospace small">{String(info.getValue() ?? '—')}</span> },
    { accessorKey: 'message', header: '消息', cell: (info) => <span title={String(info.getValue() ?? '')}>{String(info.getValue() ?? '—')}</span> },
    { accessorKey: 'correlationId', header: '报障编号', cell: (info) => <span className="font-monospace small">{String(info.getValue() ?? '—')}</span> },
    { accessorKey: 'moduleId', header: '模块', cell: (info) => String(info.getValue() ?? '—') },
    { accessorKey: 'errorCode', header: '错误码', cell: (info) => <span className="font-monospace small">{String(info.getValue() ?? '—')}</span> },
  ]

  const fileColumns: ColumnDef<LogFileRow, unknown>[] = [
    { accessorKey: 'name', header: '文件', cell: (info) => <span className="font-monospace small">{String(info.getValue() ?? '')}</span> },
    { accessorKey: 'sizeBytes', header: '大小', cell: (info) => formatBytes(Number(info.getValue() ?? 0)) },
    { accessorKey: 'lastWriteTime', header: '最后写入', cell: (info) => <span className="font-monospace small">{String(info.getValue() ?? '')}</span> },
  ]

  const info = currentAppInfo()
  const filesError = describeApiError(files.error, '日志文件列表加载失败。')
  const logsError = describeApiError(logs.error, '日志查询失败。')

  return (
    <div className="erp-full-list-page">
      <ErpListCard
        ariaLabel="系统诊断信息"
        actions={(
          <div className="d-flex gap-2">
            <Button size="sm" icon={<IconRefresh size={16} />} onClick={refreshAll}>刷新</Button>
            <Button size="sm" variant="secondary" icon={<IconDownload size={16} />} loading={downloading} onClick={() => void downloadBundle()}>
              下载诊断包
            </Button>
            <Button size="sm" variant="ghost" onClick={() => copyDiagnostics(info, undefined, '来自日志管理页')}>复制诊断信息</Button>
          </div>
        )}
        header={(
          <div className="px-3 pt-2">
            {diagnostics.isPending ? <LoadingState label="正在读取系统诊断信息…" /> : diagnostics.isError ? (
              <ErrorState message={describeApiError(diagnostics.error, '诊断信息加载失败。')} onRetry={() => void diagnostics.refetch()} />
            ) : diagnostics.data && (
              <div className="row g-2 small">
                <InfoItem label="产品版本" value={diagnostics.data.app.version} />
                <InfoItem label="提交" value={diagnostics.data.app.commit} />
                <InfoItem label="构建时间" value={diagnostics.data.app.buildTimeUtc} />
                <InfoItem label="进程启动" value={diagnostics.data.process.startTimeUtc} />
                <InfoItem label="环境" value={diagnostics.data.app.environment} />
                <InfoItem
                  label="运行中的构建"
                  value={diagnostics.data.process.stale ? '落后于磁盘二进制（建议重启服务）' : '与磁盘二进制一致'}
                  tone={diagnostics.data.process.stale ? 'warning' : 'neutral'}
                />
                <InfoItem
                  label="迁移台账"
                  value={diagnostics.data.migrations.error
                    ? `读取失败：${diagnostics.data.migrations.error}`
                    : `已执行 ${diagnostics.data.migrations.appliedCount ?? '—'} 个 / 内嵌 ${diagnostics.data.migrations.embeddedCount} 个`}
                  tone={diagnostics.data.migrations.error ? 'warning' : 'neutral'}
                />
                <InfoItem label="日志目录" value={diagnostics.data.logs.path} />
                <InfoItem label="日志占用" value={formatBytes(diagnostics.data.logs.totalBytes)} />
                <InfoItem label="健康检查" value={Object.entries(diagnostics.data.health).map(([key, value]) => `${key}:${value.status}`).join(' · ')} />
              </div>
            )}
          </div>
        )}
      >
        <div className="px-3 py-2">
          <ErpTable
            columns={fileColumns}
            data={files.data?.files ?? []}
            getRowId={(row: LogFileRow) => row.name}
            empty={<EmptyState title="暂无日志文件" description="应用尚未产生 Warning 及以上的日志（正常请求只进控制台）。" />}
            clientSideSorting
            storageKey="log-admin-files"
          />
        </div>
      </ErpListCard>

      <ErpListCard
        ariaLabel="运行日志查询"
        search={(
          <div className="d-flex gap-2 align-items-center">
            <ErpSearchBox value={keyword} onChange={setKeyword} placeholder="按消息/异常/类别关键字过滤…" />
            <select className="form-select form-select-sm" style={{ width: '9rem' }} value={level} onChange={(event) => setLevel(event.target.value)} aria-label="日志级别">
              <option value="">全部级别</option>
              {(files.data?.levels ?? []).map((item) => <option key={item} value={item}>{item}</option>)}
            </select>
            <select className="form-select form-select-sm" style={{ width: '7rem' }} value={take} onChange={(event) => setTake(Number(event.target.value))} aria-label="条数">
              {[100, 200, 500, 1000, 2000].map((item) => <option key={item} value={item}>最近 {item} 条</option>)}
            </select>
          </div>
        )}
        actions={(
          <span className="text-secondary small">
            {files.isError ? filesError : `共 ${files.data?.files.length ?? 0} 个文件`}
          </span>
        )}
        header={(
          <div className="px-3 pt-2 small text-secondary">
            命中 {logs.data?.count ?? 0} 条{logs.data?.truncated ? '（已达条数上限，更早的记录未显示）' : ''}
          </div>
        )}
      >
        {logs.isPending ? <LoadingState label="正在读取日志…" /> : logs.isError ? (
          <ErrorState message={logsError} onRetry={() => void logs.refetch()} />
        ) : (
          <ErpTable
            columns={columns}
            data={logs.data?.entries ?? []}
            getRowId={(row: LogEntryRow) => `${row.file}:${row.ts}:${row.message ?? ''}:${row.correlationId ?? ''}`}
            empty={<EmptyState title="没有命中日志" description="换个关键字或放宽级别再试；正常请求不落盘，属预期。" />}
            clientSideSorting
            storageKey="log-admin-entries"
          />
        )}
      </ErpListCard>
    </div>
  )
}

const TONE_CLASS: Record<string, string> = {
  neutral: 'text-secondary',
  warning: 'text-warning',
  danger: 'text-danger',
}

function InfoItem({ label, value, tone = 'neutral' }: { label: string; value: string; tone?: 'neutral' | 'warning' | 'danger' }) {
  return (
    <div className="col-12 col-md-6 col-xl-4">
      <span className="text-secondary">{label}：</span>
      <span className={`font-monospace ${TONE_CLASS[tone]}`}>{value}</span>
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
