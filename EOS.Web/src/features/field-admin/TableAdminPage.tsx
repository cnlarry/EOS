import { IconRefresh, IconSearch } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { ErrorState, EmptyState, LoadingState } from '../../components/common/AsyncState'
import { ErpDataTable } from '../../components/common/ErpDataTable'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'

export interface FieldAdminTable {
  tableId: string
  description: string
  kind: string | null
  type: string | null
}

const pageSize = 16

export function TableAdminPage() {
  const navigate = useNavigate()
  const [keyword, setKeyword] = useState('')
  const [page, setPage] = useState(1)
  const tables = useQuery({ queryKey: ['field-admin', 'tables'], queryFn: () => apiClient.get<FieldAdminTable[]>('/admin/tables') })

  const filtered = (tables.data ?? []).filter((item) =>
    !keyword || item.tableId.toLowerCase().includes(keyword.toLowerCase()) || item.description.toLowerCase().includes(keyword.toLowerCase()))
  const totalPages = Math.max(1, Math.ceil(filtered.length / pageSize))
  const rows = filtered.slice((page - 1) * pageSize, page * pageSize)

  const errorMessage = tables.error instanceof ApiError ? tables.error.body.message : '发生未知错误，请稍后重试。'

  return (
    <div className="d-grid gap-2">
      <section className="card erp-list-card">
        <section className="erp-list-command-bar" aria-label="数据表维护查询">
          <div className="erp-search erp-list-global-search">
            <IconSearch size={18} />
            <input aria-label="搜索数据表" placeholder="搜索表名或描述" type="search" value={keyword} onChange={(event) => { setKeyword(event.target.value); setPage(1) }} />
            <kbd>Ctrl K</kbd>
          </div>
          <div className="erp-list-actions">
            <Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void tables.refetch()}>刷新</Button>
          </div>
        </section>
        {tables.isPending ? <LoadingState label="正在加载数据表…" /> : tables.isError ? <ErrorState message={errorMessage} onRetry={() => void tables.refetch()} /> : rows.length === 0 ? (
          <EmptyState title="没有找到数据表" description="请调整搜索条件后重试。" />
        ) : (
          <ErpDataTable resizable storageKey="field-admin-tables">
            <thead>
              <tr>
                <th>表名</th>
                <th>描述</th>
                <th>类型</th>
                <th>性质</th>
                <th className="text-end">操作</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((item) => (
                <tr key={item.tableId}>
                  <td className="font-monospace fw-semibold">{item.tableId}</td>
                  <td>{item.description}</td>
                  <td className="text-secondary">{item.type ?? '—'}</td>
                  <td className="text-secondary">{item.kind ?? '—'}</td>
                  <td className="text-end">
                    <Button size="sm" className="erp-table-action" onClick={() => navigate(`/admin/tables/${encodeURIComponent(item.tableId)}/fields`)}>管理字段</Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </ErpDataTable>
        )}
        {!tables.isPending && !tables.isError && (
          <div className="card-footer erp-pagination-footer">
            <div className="text-secondary small">共 {filtered.length} 条，第 {page}/{totalPages} 页</div>
            <div className="btn-group">
              <Button size="sm" disabled={page <= 1} onClick={() => setPage(1)}>首页</Button>
              <Button size="sm" disabled={page <= 1} onClick={() => setPage((value) => value - 1)}>上一页</Button>
              <Button size="sm" disabled={page >= totalPages} onClick={() => setPage((value) => value + 1)}>下一页</Button>
              <Button size="sm" disabled={page >= totalPages} onClick={() => setPage(totalPages)}>尾页</Button>
            </div>
          </div>
        )}
      </section>
    </div>
  )
}
