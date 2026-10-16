import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import type { ColumnDef } from '../../lib/tanstackTable'
import { IconRefresh } from '@tabler/icons-react'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'

interface AuditRow {
  EVENT_ID: number
  OCCURRED_AT: string | null
  CORRELATION_ID: string | null
  ACTOR_USER_ID: string | null
  ACTOR_DISPLAY_NAME: string | null
  MODULE_ID: number | null
  RESOURCE_TYPE: string | null
  RESOURCE_KEY: string | null
  ACTION: string | null
  RESULT: number | null
  ERROR_CODE: string | null
  SUMMARY: string | null
}

interface AuditResponse {
  /** 信封字段是小写驼峰（服务端默认序列化），行内键才是库列名大写 */
  rows: AuditRow[]
  total: number
  page: number
  pageSize: number
}

/**
 * 操作审计（定制页 /admin/audit）：查"谁在什么时候对什么做了什么、结果如何"。
 *
 * 与「日志管理」/admin/logs（模块 2313）是两层，也各是一个模块号：那层是**运行日志**
 * （磁盘文件，回答"这次请求为什么失败"），这层是**操作审计**（库表 AUDIT_EVENT，
 * 回答"谁改的、改成什么"）。分号是为了让审计能单独授权，不必与看日志绑在一起。
 *
 * 权限锚本页模块 2316——路由由该模块的读权限把关，服务端 AuditController 用同一模块的
 * CanBrowse 二次把关；未授权时后端返回 403 且带明说原因，页面照原样展示。
 */
export function AuditLogPage() {
  const [actor, setActor] = useState('')
  const [action, setAction] = useState('')
  const [moduleId, setModuleId] = useState('')
  const [result, setResult] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [page, setPage] = useState(1)
  const pageSize = 20

  const audit = useQuery({
    queryKey: ['audit-query', actor, action, moduleId, result, from, to, page],
    queryFn: () => apiClient.get<AuditResponse>('/audit', {
      query: {
        actor: actor || undefined,
        action: action || undefined,
        moduleId: moduleId || undefined,
        // RESULT：0 失败、1 成功；不填即不过滤
        result: result === '' ? undefined : Number(result),
        from: from || undefined,
        to: to || undefined,
        page,
        pageSize,
      },
    }),
  })

  const rows = audit.data?.rows ?? []
  const total = audit.data?.total ?? 0
  const lastPage = Math.max(1, Math.ceil(total / pageSize))

  const columns: ColumnDef<AuditRow, unknown>[] = [
    { accessorKey: 'OCCURRED_AT', header: '时间', cell: (info) => <span className="font-monospace small">{String(info.getValue() ?? '—')}</span> },
    {
      accessorKey: 'ACTOR_USER_ID',
      header: '操作人',
      cell: (info) => (
        <span title={String(info.row.original.ACTOR_DISPLAY_NAME ?? '')}>
          {String(info.getValue() ?? '—')}
        </span>
      ),
    },
    { accessorKey: 'MODULE_ID', header: '模块', cell: (info) => String(info.getValue() ?? '—') },
    { accessorKey: 'ACTION', header: '动作', cell: (info) => <span className="font-monospace small">{String(info.getValue() ?? '—')}</span> },
    {
      accessorKey: 'RESOURCE_KEY',
      header: '对象',
      cell: (info) => <span title={String(info.getValue() ?? '')}>{String(info.getValue() ?? '—')}</span>,
    },
    {
      accessorKey: 'RESULT',
      header: '结果',
      cell: (info) => {
        const value = info.getValue()
        const failed = value === 0
        return <span className={`badge bg-${failed ? 'red' : 'green'}-lt`}>{failed ? '失败' : '成功'}</span>
      },
    },
    { accessorKey: 'ERROR_CODE', header: '错误码', cell: (info) => <span className="font-monospace small">{String(info.getValue() ?? '—')}</span> },
    { accessorKey: 'CORRELATION_ID', header: '报障编号', cell: (info) => <span className="font-monospace small">{String(info.getValue() ?? '—')}</span> },
    { accessorKey: 'SUMMARY', header: '摘要', cell: (info) => <span title={String(info.getValue() ?? '')}>{String(info.getValue() ?? '—')}</span> },
  ]

  return (
    <div className="erp-full-list-page">
      <ErpListCard
        ariaLabel="操作审计"
        search={(
          <div className="d-flex flex-wrap gap-2 align-items-center">
            <ErpSearchBox value={actor} onChange={(value) => { setActor(value); setPage(1) }} placeholder="按操作人过滤…" />
            <ErpSearchBox value={action} onChange={(value) => { setAction(value); setPage(1) }} placeholder="按动作过滤…" />
            <input
              className="form-control form-control-sm"
              style={{ width: '8rem' }}
              value={moduleId}
              onChange={(event) => { setModuleId(event.target.value); setPage(1) }}
              placeholder="模块号"
              inputMode="numeric"
              aria-label="模块号"
            />
            <select
              className="form-select form-select-sm"
              style={{ width: '8rem' }}
              value={result}
              onChange={(event) => { setResult(event.target.value); setPage(1) }}
              aria-label="结果"
            >
              <option value="">全部结果</option>
              <option value="1">成功</option>
              <option value="0">失败</option>
            </select>
            <input
              className="form-control form-control-sm"
              style={{ width: '10rem' }}
              type="date"
              value={from}
              onChange={(event) => { setFrom(event.target.value); setPage(1) }}
              aria-label="起始日期"
            />
            <span className="text-secondary small">至</span>
            <input
              className="form-control form-control-sm"
              style={{ width: '10rem' }}
              type="date"
              value={to}
              onChange={(event) => { setTo(event.target.value); setPage(1) }}
              aria-label="截止日期"
            />
          </div>
        )}
        actions={(
          <div className="d-flex gap-2 align-items-center">
            <span className="text-secondary small">共 {total} 条 · 第 {page}/{lastPage} 页</span>
            <Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void audit.refetch()}>刷新</Button>
            <Button size="sm" variant="secondary" disabled={page <= 1} onClick={() => setPage((current) => Math.max(1, current - 1))}>上一页</Button>
            <Button size="sm" variant="secondary" disabled={page >= lastPage} onClick={() => setPage((current) => current + 1)}>下一页</Button>
          </div>
        )}
        header={(
          <div className="px-3 pt-2 small text-secondary">
            操作审计记的是"谁改的、改成什么"（库表 AUDIT_EVENT）；
            "这次请求为什么失败"请看「日志管理」（运行日志，按文件留存）。
          </div>
        )}
      >
        {audit.isPending ? <LoadingState label="正在读取操作审计…" /> : audit.isError ? (
          <ErrorState message={describeApiError(audit.error, '操作审计加载失败。')} onRetry={() => void audit.refetch()} />
        ) : (
          <ErpTable
            columns={columns}
            data={rows}
            getRowId={(row: AuditRow) => String(row.EVENT_ID)}
            empty={<EmptyState title="没有命中的审计记录" description="换个操作人或放宽时间范围再试。" />}
            clientSideSorting
            storageKey="audit-log-entries"
          />
        )}
      </ErpListCard>
    </div>
  )
}
