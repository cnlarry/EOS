import { IconArrowLeft, IconSearch } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import type { ColumnDef } from '../../lib/tanstackTable'
import { useEffect, useMemo, useState } from 'react'
import { EmptyState, LoadingState } from '../../components/common/AsyncState'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'

/**
 * 自定义按钮授权矩阵（用户 / 用户组）。
 *
 * 授权是 **fail-closed 名单**：配好并发布的按钮默认全场无人可点，显式授权给谁、谁才能点；
 * 这里与模块/报表权限同一处维护，但缺省相反——所以本页默认"只显示已授权"，
 * 而不是像报表那样"只显示例外"。
 */
export interface ButtonRightsRow {
  moduleId: number
  moduleTitle: string
  key: string
  label: string
  granted: boolean
  hasOverrideRow: boolean
  groupGranted: boolean
}

export interface ButtonRightsInput {
  moduleId: number
  key: string
  granted: boolean
}

interface ButtonRightsMatrixProps {
  open: boolean
  mode: 'user' | 'group'
  targetId: string
  title?: string
  onClose: () => void
  onSaved?: () => void
  /** 弹窗（默认）或完整页面（供用户/组管理页子页）。 */
  variant?: 'modal' | 'page'
}

const rowKey = (row: Pick<ButtonRightsRow, 'moduleId' | 'key'>) => `${row.moduleId}:${row.key}`

export function ButtonRightsMatrix({ open, mode, targetId, title, onClose, onSaved, variant = 'modal' }: ButtonRightsMatrixProps) {
  const url = mode === 'user'
    ? `/admin/users/${encodeURIComponent(targetId.trim())}/button-rights`
    : `/admin/groups/${encodeURIComponent(targetId.trim())}/button-rights`
  const matrix = useQuery({
    queryKey: ['rights-admin', mode, targetId, 'button-matrix'],
    queryFn: () => apiClient.get<ButtonRightsRow[]>(url),
    enabled: open,
    staleTime: Number.POSITIVE_INFINITY,
    refetchOnWindowFocus: false,
  })
  const rows = useMemo(() => matrix.data ?? [], [matrix.data])
  const [showOnlyGranted, setShowOnlyGranted] = useState(true)
  const [search, setSearch] = useState('')

  const filteredRows = useMemo(() => {
    const text = search.trim().toLowerCase()
    const scoped = showOnlyGranted ? rows.filter((row) => row.granted) : rows
    if (!text) return scoped
    return scoped.filter((row) =>
      String(row.moduleId).includes(text)
      || row.moduleTitle.toLowerCase().includes(text)
      || row.key.toLowerCase().includes(text)
      || row.label.toLowerCase().includes(text))
  }, [rows, search, showOnlyGranted])

  const [granted, setGranted] = useState<Record<string, boolean>>({})
  const [dirty, setDirty] = useState<Set<string>>(new Set())
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  useEffect(() => {
    if (!open || !matrix.data) return
    const next: Record<string, boolean> = {}
    matrix.data.forEach((row) => { next[rowKey(row)] = row.granted })
    setGranted(next)
    setDirty(new Set())
    setError(null)
    setNotice(null)
  }, [open, matrix.data])

  const toggle = (key: string, value: boolean) => {
    setGranted((current) => ({ ...current, [key]: value }))
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
      const items: ButtonRightsInput[] = [...dirty].map((key) => {
        const row = rows.find((item) => rowKey(item) === key)!
        return { moduleId: row.moduleId, key: row.key, granted: granted[key] === true }
      })
      await apiClient.put(url, { items })
      setNotice(`已保存 ${items.length} 个按钮的授权。`)
      onSaved?.()
      await matrix.refetch()
    } catch (reason) {
      setError(describeApiError(reason, '保存失败，请稍后重试。'))
    } finally {
      setSaving(false)
    }
  }

  const columns = useMemo<ColumnDef<ButtonRightsRow, unknown>[]>(() => [
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
      accessorKey: 'label',
      header: '按钮',
      cell: ({ row }) => (
        <span className="text-nowrap">
          {row.original.label}
          <span className="text-secondary ms-1 small">{row.original.key}</span>
        </span>
      ),
    },
    {
      id: 'granted',
      header: '允许点击',
      meta: { minWidth: 96 },
      cell: ({ row }) => (
        <input
          type="checkbox"
          className="form-check-input"
          aria-label={`允许点击 ${row.original.label}`}
          checked={granted[rowKey(row.original)] === true}
          onChange={(event) => toggle(rowKey(row.original), event.target.checked)}
        />
      ),
    },
    {
      id: 'channel',
      header: '通道',
      meta: { minWidth: 170 },
      cell: ({ row }) => (
        <span className="small text-secondary">
          {mode === 'user'
            ? row.original.hasOverrideRow
              ? '个人名单已生效（忽略组授权）'
              : row.original.groupGranted ? '仅组授权已开通' : '无（个人与组都没开）'
            : row.original.hasOverrideRow ? '已配组名单' : '未配'}
        </span>
      ),
    },
  ], [granted, mode])

  if (!open) return null

  const headerTitle = title ?? (mode === 'user' ? `用户按钮权限：${targetId.trim()}` : `用户组按钮权限：${targetId.trim()}`)

  const matrixBody = (
    <>
      <div className="alert alert-warning py-1 px-2 small mb-2 d-flex align-items-center justify-content-between">
        <span>
          自定义按钮是<strong>默认拒绝</strong>的名单：不在这里授权，按钮既不可点，也不会出现在单据上。
          个人名单一旦存在就接管组授权（个人 ≠ 组的并集）；取消勾选并保存 = 删除该行 = 回到不可点。
        </span>
        <label className="form-check form-check-inline mb-0 text-nowrap">
          <input type="checkbox" className="form-check-input" checked={showOnlyGranted}
            onChange={(event) => setShowOnlyGranted(event.target.checked)} />
          <span className="form-check-label">仅显示已授权</span>
        </label>
      </div>
      {matrix.isPending ? <LoadingState label="正在加载按钮授权…" /> : matrix.isError ? (
        <div className="alert alert-danger d-flex align-items-center justify-content-between">
          <span>{describeApiError(matrix.error, '按钮授权加载失败。')}</span>
          <Button variant="danger" size="sm" onClick={() => void matrix.refetch()}>重试</Button>
        </div>
      ) : rows.length === 0 ? (
        <EmptyState title="当前可见模块下没有配置自定义按钮" description="先在模块的行为动作配置里配好按钮并发布。" />
      ) : filteredRows.length === 0 ? (
        <EmptyState title="没有匹配的按钮" description="取消「仅显示已授权」或调整搜索关键字后重试。" />
      ) : (
        <ErpTable
          columns={columns}
          data={filteredRows}
          getRowId={(row) => rowKey(row)}
          resizable
          storageKey={`button-rights-${mode}-${targetId.trim()}`}
          clientSideSorting
          empty={<EmptyState title="当前可见模块下没有配置自定义按钮" />}
        />
      )}
      {error && <div className="alert alert-danger py-2 mt-2 mb-0" role="alert">{error}</div>}
      {notice && <div className="alert alert-info py-2 mt-2 mb-0" role="status">{notice}</div>}
    </>
  )

  if (variant === 'page') {
    return (
      <div className="erp-full-list-page">
        <section className="card erp-list-card">
          <section className="erp-list-command-bar" aria-label="按钮授权工具栏">
            <div className="erp-nav-search erp-menu-search">
              <IconSearch size={16} aria-hidden="true" />
              <input
                type="search"
                value={search}
                onChange={(event) => setSearch(event.target.value)}
                placeholder="搜索模块/按钮名称或键"
                aria-label="搜索按钮"
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
    <Modal title={headerTitle} onClose={onClose} size="lg" footer={
      <div className="d-flex gap-2 ms-auto">
        <Button onClick={onClose}>取消</Button>
        <Button variant="primary" onClick={() => void save()} loading={saving}>保存</Button>
      </div>
    }>
      {matrixBody}
    </Modal>
  )
}
