import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'

interface ImportTable { tableId: string; tableDesc: string; primaryKeys: string[] }
interface ImportField { key: string; label: string; dataType: string; isRequired: boolean; isPrimaryKey: boolean }
interface ImportDefinition { table: string; fields: ImportField[]; primaryKeys: string[] }
interface ImportPreview { columns: string[]; sampleRows: string[][]; totalRows: number }
interface ImportRowError { rowNumber: number; message: string }
interface ImportResult { inserted: number; failed: number; errors: ImportRowError[] }

export function ImportPage() {
  const [table, setTable] = useState('')
  const [csvText, setCsvText] = useState('')
  const [preview, setPreview] = useState<ImportPreview | null>(null)
  const [mapping, setMapping] = useState<Record<number, string>>({})
  const [result, setResult] = useState<ImportResult | null>(null)
  const [busy, setBusy] = useState(false)

  const tables = useQuery({ queryKey: ['import', 'tables'], queryFn: () => apiClient.get<ImportTable[]>('/import/tables') })
  const definition = useQuery({
    queryKey: ['import', table, 'definition'],
    queryFn: () => apiClient.get<ImportDefinition>(`/import/${table}/definition`),
    enabled: table !== '',
  })

  const parsePreview = async () => {
    if (!csvText.trim()) return
    setBusy(true)
    try {
      const parsed = await apiClient.post<ImportPreview>('/import/preview', csvText)
      setPreview(parsed)
      const auto: Record<number, string> = {}
      parsed.columns.forEach((column, index) => {
        const match = definition.data?.fields.find((field) => field.label === column.trim() || field.key === column.trim())
        if (match) auto[index] = match.key
      })
      setMapping(auto)
      setResult(null)
    } catch (error) {
      window.alert(error instanceof Error ? `解析失败：${error.message}` : '解析失败。')
    } finally {
      setBusy(false)
    }
  }

  const executeImport = async () => {
    if (!preview) return
    setBusy(true)
    try {
      const executed = await apiClient.post<ImportResult>('/import/execute', {
        table,
        mapping: preview.columns.map((_, index) => ({ name: preview.columns[index], mappedField: mapping[index] ?? null })),
        rows: preview.sampleRows,
      })
      setResult(executed)
    } catch (error) {
      window.alert(error instanceof Error ? `导入失败：${error.message}` : '导入失败。')
    } finally {
      setBusy(false)
    }
  }

  if (tables.isPending) return <LoadingState label="正在加载可导入表…" />
  if (tables.isError) return <ErrorState message="导入功能加载失败。" onRetry={() => void tables.refetch()} />

  return (
    <div className="d-grid erp-import-page">
      <ErpListCard
        ariaLabel="基本资料导入"
        search={null}
        actions={<>
          <Button size="sm" onClick={() => void parsePreview()} disabled={!csvText.trim() || busy}>解析预览</Button>
          <Button size="sm" onClick={() => void executeImport()} disabled={!preview || busy}>导入</Button>
        </>}
      >
        <div className="card mb-2">
          <div className="card-body py-2">
            <div className="row g-2">
              <div className="col-md-4">
                <label className="form-label mb-1 small">导入表</label>
                <select className="form-select form-select-sm" value={table} onChange={(event) => { setTable(event.target.value); setPreview(null); setResult(null) }}>
                  <option value="">请选择数据表</option>
                  {tables.data?.map((item) => <option key={item.tableId} value={item.tableId}>{item.tableDesc}（{item.tableId}）</option>)}
                </select>
              </div>
              <div className="col-md-8">
                <label className="form-label mb-1 small">CSV 内容（首行为列名，UTF-8）</label>
                <textarea className="form-control form-control-sm font-monospace" rows={4} value={csvText} onChange={(event) => setCsvText(event.target.value)} placeholder={'PRO_NO,PRO_NAME,UNIT_ID\nIMP001,导入测试产品,KG'} />
              </div>
            </div>
          </div>
        </div>
        {preview && (
          <div className="card mb-2">
            <div className="card-body py-2">
              <h2 className="card-title fs-6">预览（共 {preview.totalRows} 行，显示前 {preview.sampleRows.length} 行）</h2>
              <div className="table-responsive">
                <table className="table table-sm table-vcenter card-table">
                  <thead><tr>{preview.columns.map((column, index) => (
                    <th key={index}>
                      <div className="small text-secondary">{column}</div>
                      <select className="form-select form-select-sm mt-1" value={mapping[index] ?? ''} onChange={(event) => setMapping((current) => ({ ...current, [index]: event.target.value }))}>
                        <option value="">不导入</option>
                        {definition.data?.fields.map((field) => <option key={field.key} value={field.key}>{field.label}{field.isPrimaryKey ? '（主键）' : ''}</option>)}
                      </select>
                    </th>
                  ))}</tr></thead>
                  <tbody>{preview.sampleRows.map((row, rowIndex) => (
                    <tr key={rowIndex}>{row.map((cell, cellIndex) => <td key={cellIndex}>{cell}</td>)}</tr>
                  ))}</tbody>
                </table>
              </div>
            </div>
          </div>
        )}
        {result && (
          <div className={result.failed > 0 ? 'alert alert-warning' : 'alert alert-success'}>
            成功 {result.inserted} 行，失败 {result.failed} 行
            {result.errors.length > 0 && <ul className="mb-0 mt-1 small">{result.errors.slice(0, 20).map((error) => <li key={error.rowNumber}>第 {error.rowNumber} 行：{error.message}</li>)}</ul>}
          </div>
        )}
      </ErpListCard>
    </div>
  )
}
