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

const isAmountColumn = (key: string): boolean =>
  /(QTY|AMOUNT|PRICE|SUM)/i.test(key)

const totalOf = (rows: Record<string, unknown>[], field: PrintField): number =>
  rows.reduce((sum, row) => {
    const value = Number(row[field.key])
    return sum + (Number.isFinite(value) ? value : 0)
  }, 0)

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

  if (print.moduleId === 1416 || print.moduleId === 1604) {
    return (
      <div className="erp-print-sheet p-4">
        <div className="text-center mb-3">
          {print.headerCompany && <div className="fs-4 fw-bold">{print.headerCompany}</div>}
          <div className="fs-5 fw-semibold mt-1">{print.title}</div>
          <div className="small text-secondary mt-1">编号：{formatValue(print.master.QUOTE_NO || print.master.QUOTE_TYPE)}　日期：{formatValue(print.master.QUOTE_DATE)}</div>
        </div>
        <div className="row mb-3">
          <div className="col-6">
            <div className="small">客户/厂商：<span className="fw-semibold">{formatValue(print.master.CLIENT_ID || print.master.SUPPLIER_ID)}</span>　{formatValue(print.master.CLIENT_NAME || print.master.SUPPLIER_NAME)}</div>
            <div className="small mt-1">币别：{formatValue(print.master.CURR_ID)}　汇率：{formatValue(print.master.CURR_RATE)}</div>
          </div>
          <div className="col-6 text-end">
            <div className="small">业务：{formatValue(print.master.SALES_ID)}</div>
            <div className="small mt-1">生效日期：{formatValue(print.master.IN_EFFECT_DATE)}</div>
          </div>
        </div>
        <table className="table table-sm table-bordered">
          <thead><tr>
            <th className="small w-10">#</th>
            <th className="small">料号</th>
            <th className="small">品名/规格</th>
            <th className="small text-end">数量</th>
            <th className="small">单位</th>
            <th className="small text-end">单价</th>
            <th className="small text-end">折扣</th>
            <th className="small text-end">金额</th>
          </tr></thead>
          <tbody>
            {print.details.map((row, index) => (
              <tr key={index}>
                <td className="small text-center">{index + 1}</td>
                <td className="small font-monospace">{formatValue(row.PRO_NO)}</td>
                <td className="small">{formatValue(row.PRO_NAME)} {formatValue(row.PRO_SPEC)}</td>
                <td className="small text-end">{formatValue(row.QTY)}</td>
                <td className="small">{formatValue(row.UNIT_ID)}</td>
                <td className="small text-end">{formatValue(row.PRICE)}</td>
                <td className="small text-end">{formatValue(row.REBATE)}%</td>
                <td className="small text-end">{formatValue(row.AMOUNT_TAX)}</td>
              </tr>
            ))}
          </tbody>
          <tfoot>
            <tr className="fw-semibold">
              <td colSpan={7} className="text-end small">价税合计</td>
              <td className="text-end small">{formatValue(print.master.AMOUNT_TAX)}</td>
            </tr>
          </tfoot>
        </table>
        <div className="d-flex justify-content-between mt-4">
          <div className="small">制表：{formatValue(print.master.CREATE_PERSON)}</div>
          <div className="small">审核：{formatValue(print.master.CONFIRM_PERSON)}</div>
          <div className="small">客户/厂商确认：</div>
        </div>
        {print.footerText && <div className="small mt-3 text-secondary">{print.footerText}</div>}
      </div>
    )
  }

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
            {print.details.length > 0 && (
              <tr className="fw-semibold">
                {print.detailFields.map((field) => (
                  <td key={field.key} className="small">{isAmountColumn(field.key) ? totalOf(print.details, field).toFixed(2) : ''}</td>
                ))}
              </tr>
            )}
          </tbody>
        </table>
      )}
      {print.footerText && <div className="small mt-3 text-secondary">{print.footerText}</div>}
    </div>
  )
}
