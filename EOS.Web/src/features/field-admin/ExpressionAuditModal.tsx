import { useQuery } from '@tanstack/react-query'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'

export interface ExpressionStaleEntry {
  kind: string
  table: string
  field: string
  expression: string
  errors: string[]
}

export interface ExpressionOverview {
  whiteListVersion: number
  totalVisible: number
  hidden: number
  virtualExp: number
  convertFunction: number
  dataSourceSql: number
  stale: number
  staleItems: ExpressionStaleEntry[]
}

/** 受控表达式审计面板（2302）：白名单版本 + 分类/可见性计数 + 重校验残留清单。 */
export function ExpressionAuditModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const query = useQuery({
    queryKey: ['field-admin', 'expressions', 'overview'],
    queryFn: () => apiClient.get<ExpressionOverview>('/admin/fields/expressions/overview'),
    enabled: open,
  })
  if (!open) return null
  const data = query.data
  return (
    <Modal title="受控表达式审计" onClose={onClose} size="lg">
      {query.isLoading ? (
              <p className="text-secondary">正在校验全部表达式…</p>
            ) : query.isError ? (
              <div className="alert alert-danger">表达式审计加载失败，请确认有字段设置权限。</div>
            ) : data && (
              <>
                <div className="d-flex flex-wrap gap-3 mb-2 small">
                  <span>白名单版本 <strong>v{data.whiteListVersion}</strong></span>
                  <span>可见表达式 {data.totalVisible}</span>
                  <span>隐藏幽灵 {data.hidden}</span>
                  <span>VIRTUAL_EXP {data.virtualExp}</span>
                  <span>CONVERT_FUNCTION {data.convertFunction}</span>
                  <span>DATASOURCE_SQL {data.dataSourceSql}</span>
                  <span className={data.stale > 0 ? 'text-danger fw-semibold' : 'text-success'}>待复核 {data.stale}</span>
                </div>
                <div className="d-flex justify-content-end mb-2">
                  <Button size="sm" onClick={() => void query.refetch()} loading={query.isFetching}>重新校验</Button>
                </div>
                {data.staleItems.length > 0 ? (
                  <table className="table table-sm table-bordered mb-0">
                    <thead>
                      <tr><th>表.字段</th><th>表达式</th><th>原因</th></tr>
                    </thead>
                    <tbody>
                      {data.staleItems.map((item, index) => (
                        <tr key={index}>
                          <td className="font-monospace">{item.table}.{item.field}</td>
                          <td className="font-monospace">{item.expression.length > 50 ? `${item.expression.slice(0, 50)}…` : item.expression}</td>
                          <td>{item.errors.join('；')}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                ) : (
                  <div className="alert alert-success mb-0">全部可见表达式均通过当前白名单校验。</div>
                )}
              </>
      )}
    </Modal>
  )
}
