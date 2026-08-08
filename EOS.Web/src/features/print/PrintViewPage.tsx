import { useQuery } from '@tanstack/react-query'
import { useEffect } from 'react'
import { useParams, useSearchParams } from 'react-router-dom'
import { LoadingState } from '../../components/common/AsyncState'
import { apiClient } from '../../services/api'

interface PrintField { key: string; label: string }
interface PrintData {
  moduleId: number
  title: string
  headerCompany: string | null
  headerText: string | null
  footerText: string | null
  masterFields: PrintField[]
  detailFields: PrintField[]
  master: Record<string, unknown>
  details: Record<string, unknown>[]
}

const formatValue = (value: unknown): string => {
  if (value === null || value === undefined) return ''
  if (value instanceof Date || typeof value === 'string') return String(value).trim()
  if (typeof value === 'boolean') return value ? '是' : '否'
  return String(value)
}

export function PrintViewPage() {
  const { moduleId = '' } = useParams()
  const [searchParams] = useSearchParams()
  const keyParam = searchParams.get('key') ?? '[]'
  const data = useQuery({
    queryKey: ['print', moduleId, keyParam],
    queryFn: () => apiClient.post<PrintData>(`/print/${moduleId}`, { key: JSON.parse(keyParam) as string[] }),
  })

  useEffect(() => {
    if (data.isSuccess) {
      const timer = setTimeout(() => window.print(), 300)
      return () => clearTimeout(timer)
    }
  }, [data.isSuccess])

  if (data.isPending) return <LoadingState label="正在生成打印数据…" />
  if (data.isError) return <div className="p-4 text-danger">打印数据加载失败，请关闭本窗口重试。</div>
  const print = data.data!

  return (
    <div className="erp-print-sheet p-4">
      <div className="text-center mb-3">
        {print.headerCompany && <div className="fs-4 fw-bold">{print.headerCompany}</div>}
        <div className="fs-5 fw-semibold mt-1">{print.title}</div>
      </div>
      {print.headerText && <div className="small mb-2">{print.headerText}</div>}
      <table className="table table-sm table-bordered mb-4">
        <tbody>
          {print.masterFields.map((field) => (
            <tr key={field.key}>
              <th className="w-25 text-end pe-2 small">{field.label}</th>
              <td>{formatValue(print.master[field.key])}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {print.detailFields.length > 0 && (
        <table className="table table-sm table-bordered">
          <thead><tr>{print.detailFields.map((field) => <th key={field.key} className="small">{field.label}</th>)}</tr></thead>
          <tbody>
            {print.details.map((row, index) => (
              <tr key={index}>{print.detailFields.map((field) => <td key={field.key} className="small">{formatValue(row[field.key])}</td>)}</tr>
            ))}
          </tbody>
        </table>
      )}
      {print.footerText && <div className="small mt-3 text-secondary">{print.footerText}</div>}
    </div>
  )
}
