import { IconArrowLeft, IconSearch } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import type { ColumnDef } from '@tanstack/react-table'
import { useEffect, useMemo, useState } from 'react'
import { EmptyState, LoadingState } from '../../components/common/AsyncState'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'
import type { ReportRightsInput, ReportRightsRow } from './types'
import { describeApiError } from '../../lib/errors'

interface ReportRightsMatrixProps {
  open: boolean
  mode: 'user' | 'group'
  targetId: string
  title?: string
  onClose: () => void
  onSaved?: () => void
  /** 弹窗（默认）或完整页面（供 2305 组报表权限页）。 */
  variant?: 'modal' | 'page'
}

interface ReportDraft {
  preview: boolean
  print: boolean
  export: boolean
  dataFilter: string
}

function reportKey(row: Pick<ReportRightsRow, 'moduleId' | 'reportId'>) {
  return `${row.moduleId}:${row.reportId}`
}

export function ReportRightsMatrix({ open, mode, targetId, title, onClose, onSaved, variant = 'modal' }: ReportRightsMatrixProps) {
  const url = mode === 'user'
    ? `/admin/users/${encodeURIComponent(targetId.trim())}/report-rights`
    : `/admin/groups/${encodeURIComponent(targetId.trim())}/report-rights`
  const matrix = useQuery({
    queryKey: ['rights-admin', mode, targetId, 'report-matrix'],
    queryFn: () => apiClient.get<ReportRightsRow[]>(url),
    enabled: open,
    staleTime: Number.POSITIVE_INFINITY,
    refetchOnWindowFocus: false,
  })
  const rows = useMemo(() => matrix.data ?? [], [matrix.data])
  const [showOnlyExceptions, setShowOnlyExceptions] = useState(true)
  const [search, setSearch] = useState('')

  const filteredRows = useMemo(() => {
    const text = search.trim().toLowerCase()
    const scoped = showOnlyExceptions ? rows.filter((row) => row.hasPersonal) : rows
    if (!text) return scoped
    return scoped.filter((row) =>
      String(row.moduleId).includes(text)
      || row.moduleTitle.toLowerCase().includes(text)
      || row.reportId.toLowerCase().includes(text)
      || row.reportName.toLowerCase().includes(text))
  }, [rows, search, showOnlyExceptions])

  const [draft, setDraft] = useState<Record<string, ReportDraft>>({})
  const [dirty, setDirty] = useState<Set<string>>(new Set())
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  useEffect(() => {
    if (!open || !matrix.data) return
    const nextDraft: Record<string, ReportDraft> = {}
    matrix.data.forEach((row) => {
      nextDraft[reportKey(row)] = { preview: row.preview, print: row.print, export: row.export, dataFilter: row.dataFilter }
    })
    setDraft(nextDraft)
    setDirty(new Set())
    setError(null)
    setNotice(null)
  }, [open, matrix.data])

  const setValue = (key: string, patch: Partial<ReportDraft>) => {
    setDraft((current) => ({ ...current, [key]: { ...current[key], ...patch } }))
    setDirty((current) => new Set(current).add(key))
    setError(null)
  }

  const save = async () => {
    if (dirty.size === 0) {
      setNotice('没有需要保存的修改。')
      return
    }
    setSaving(true)
    setError(null)
    setNotice(null)
    try {
      const items: ReportRightsInput[] = [...dirty].map((key) => {
        const row = rows.find((item) => reportKey(item) === key)!
        const value = draft[key]
        return {
          moduleId: row.moduleId,
          reportId: row.reportId,
          preview: value.preview,
          print: value.print,
          export: value.export,
          dataFilter: value.dataFilter || null,
        }
      })
      await apiClient.put(url, { items })
      setNotice(`已保存 ${items.length} 个报表权限。`)
      onSaved?.()
      await matrix.refetch()
    } catch (reason) {
      setError(describeApiError(reason, '保存失败，请稍后重试。'))
    } finally {
      setSaving(false)
    }
  }

  const columns = useMemo<ColumnDef<ReportRightsRow, unknown>[]>(() => [
    {
      accessorKey: 'moduleId',
      header: '模块',
      cell: ({ row }) => (
        <span className="text-nowrap small">
          {row.original.moduleId}
          <span className="text-secondary ms-1">{row.original.moduleTitle}</span>
        </span>
      ),
    },
    {
      accessorKey: 'reportId',
      header: '报表',
      cell: ({ row }) => (
        <>
          <div className="font-monospace small">{row.original.reportId}</div>
          <div className="small text-secondary">{row.original.reportName}</div>
        </>
      ),
    },
    {
      id: 'preview',
      header: '预览',
      cell: ({ row }) => {
        const key = reportKey(row.original)
        return (
          <input
            type="checkbox"
            className="form-check-input m-0"
            checked={draft[key]?.preview ?? false}
            onChange={(event) => setValue(key, { preview: event.target.checked })}
            aria-label={`预览 ${row.original.reportId}`}
          />
        )
      },
      meta: { className: 'text-center', truncate: false },
    },
    {
      id: 'print',
      header: '列印',
      cell: ({ row }) => {
        const key = reportKey(row.original)
        return (
          <input
            type="checkbox"
            className="form-check-input m-0"
            checked={draft[key]?.print ?? false}
            onChange={(event) => setValue(key, { print: event.target.checked })}
            aria-label={`列印 ${row.original.reportId}`}
          />
        )
      },
      meta: { className: 'text-center', truncate: false },
    },
    {
      id: 'export',
      header: '导出',
      cell: ({ row }) => {
        const key = reportKey(row.original)
        return (
          <input
            type="checkbox"
            className="form-check-input m-0"
            checked={draft[key]?.export ?? false}
            onChange={(event) => setValue(key, { export: event.target.checked })}
            aria-label={`导出 ${row.original.reportId}`}
          />
        )
      },
      meta: { className: 'text-center', truncate: false },
    },
    {
      id: 'effective',
      header: '来源 / 生效',
      cell: ({ row }) => {
        const effective = row.original.effective
        const key = reportKey(row.original)
        const sourceClass = effective.source === 'personal' ? 'bg-primary-subtle text-primary' : effective.source === 'group' ? 'bg-secondary-subtle text-secondary' : 'bg-light text-secondary'
        const sourceLabel = effective.source === 'personal' ? '个人' : effective.source === 'group' ? '组' : effective.source === 'default_open' ? '默认开放' : '无'
        return (
          <span className="small">
            <span className={`badge me-1 ${sourceClass}`}>{sourceLabel}</span>
            <span className="text-secondary">
              预览{effective.preview ? '✓' : '✗'} 列印{effective.print ? '✓' : '✗'} 导出{effective.export ? '✓' : '✗'}
            </span>
            {dirty.has(key) && <span className="badge bg-warning-subtle text-warning ms-1">已修改</span>}
          </span>
        )
      },
      meta: { truncate: false },
    },
    {
      id: 'dataFilter',
      header: 'DATA_FILTER',
      cell: ({ row }) => {
        const key = reportKey(row.original)
        return (
          <input
            className="form-control form-control-sm font-monospace"
            value={draft[key]?.dataFilter ?? ''}
            onChange={(event) => setValue(key, { dataFilter: event.target.value })}
            placeholder="受控过滤表达式（非法保存时拒绝 400）"
            aria-label={`DATA_FILTER ${row.original.reportId}`}
          />
        )
      },
      meta: { minWidth: 240 },
    },
  ], [draft, dirty])

  if (!open) return null

  const headerTitle = title ?? (mode === 'user' ? `用户报表权限：${targetId.trim()}` : `用户组报表权限：${targetId.trim()}`)

  const matrixBody = (
    <>
      <div className="alert alert-info py-1 px-2 small mb-2 d-flex align-items-center justify-content-between">
        <span>
          报表权限已改为「默认开放 + 例外收紧」：模块拥有报表权限 (REPORT_TAG) 的用户默认可预览/列印/导出该模块全部报表。
          以下仅列出已逐报表收紧的例外行。清空某行勾选并保存 = 删除该例外，恢复默认开放。
          {mode === 'user' ? ' 个人例外覆盖组例外。' : ''}
        </span>
        <label className="form-check form-check-inline mb-0 text-nowrap">
          <input type="checkbox" className="form-check-input" checked={showOnlyExceptions}
            onChange={(event) => setShowOnlyExceptions(event.target.checked)} />
          <span className="form-check-label">仅显示例外</span>
        </label>
      </div>
      {matrix.isPending ? <LoadingState label="正在加载报表权限…" /> : matrix.isError ? (
        <div className="alert alert-danger d-flex align-items-center justify-content-between">
          <span>{describeApiError(matrix.error, '报表权限加载失败。')}</span>
          <Button variant="danger" size="sm" onClick={() => void matrix.refetch()}>重试</Button>
        </div>
      ) : rows.length === 0 ? (
        <EmptyState title="当前可见模块下没有报表定义" description="切换其它用户/组后重试。" />
      ) : filteredRows.length === 0 ? (
        <EmptyState title="没有匹配的报表" description="请调整搜索关键字后重试。" />
      ) : (
        <ErpTable
          columns={columns}
          data={filteredRows}
          getRowId={(row) => reportKey(row)}
          resizable
          storageKey={`report-rights-${targetId.trim()}`}
          clientSideSorting
          empty={<EmptyState title="当前可见模块下没有报表定义" />}
        />
      )}
      {error && <div className="alert alert-danger py-2 mt-2 mb-0" role="alert">{error}</div>}
      {notice && <div className="alert alert-info py-2 mt-2 mb-0" role="status">{notice}</div>}
    </>
  )

  const matrixFooter = (
    <div className="d-flex gap-2 ms-auto">
      <Button onClick={onClose}>{variant === 'page' ? '返回' : '取消'}</Button>
      <Button variant="primary" onClick={() => void save()} loading={saving}>保存</Button>
    </div>
  )

  if (variant === 'page') {
    return (
      <div className="erp-full-list-page">
        <section className="card erp-list-card">
          <section className="erp-list-command-bar" aria-label="报表权限工具栏">
            <div className="erp-nav-search erp-menu-search">
              <IconSearch size={16} aria-hidden="true" />
              <input
                type="search"
                value={search}
                onChange={(event) => setSearch(event.target.value)}
                placeholder="搜索模块/报表名称或编号"
                aria-label="搜索报表"
              />
              {search && (
                <button type="button" className="erp-nav-search-clear" aria-label="清除搜索" onClick={() => setSearch('')}>×</button>
              )}
            </div>
            <div className="erp-list-actions d-flex gap-2 align-items-center">
              <Button size="sm" variant="ghost" icon={<IconArrowLeft size={16} />} onClick={onClose}>
                {mode === 'user' ? '返回用户列表' : '返回用户组'}
              </Button>
              <Button size="sm" variant="primary" onClick={() => void save()} loading={saving}>保存</Button>
            </div>
          </section>
          <div className="erp-report-matrix-body p-2">{matrixBody}</div>
        </section>
      </div>
    )
  }

  return (
    <Modal title={headerTitle} onClose={onClose} size="lg" footer={matrixFooter}>
      {matrixBody}
    </Modal>
  )
}
