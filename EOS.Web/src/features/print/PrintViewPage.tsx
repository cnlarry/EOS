import { useQuery } from '@tanstack/react-query'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useParams, useSearchParams } from 'react-router-dom'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'

interface ReportPrintOption { reportId: string; reportName: string; headerId: string | null; tailId: string | null; isDefault: boolean }
interface ReportHeaderOption { headerId: string; headerName: string; companyName: string; headerText: string | null; logoUrl: string | null }
interface ReportTailOption { tailId: string; tailName: string }
interface ReportPrintSettingsData {
  moduleId: number
  reports: ReportPrintOption[]
  headers: ReportHeaderOption[]
  tails: ReportTailOption[]
  userSettings: { reportId: string | null; headerId: string | null; tailId: string | null } | null
}

export function PrintViewPage() {
  const { moduleId = '' } = useParams()
  const [searchParams] = useSearchParams()
  const key = useMemo(() => {
    try { return JSON.parse(searchParams.get('key') ?? '[]') as string[] } catch { return [] }
  }, [searchParams])
  const [reportId, setReportId] = useState('')
  const [headerId, setHeaderId] = useState('')
  const [tailId, setTailId] = useState('')
  const [showRemark, setShowRemark] = useState(true)
  const [pdfUrl, setPdfUrl] = useState<string | null>(null)
  const [pdfError, setPdfError] = useState<string | null>(null)
  const iframeRef = useRef<HTMLIFrameElement>(null)
  const pdfUrlRef = useRef<string | null>(null)

  const settings = useQuery({
    queryKey: ['print', moduleId, 'settings'],
    queryFn: () => apiClient.get<ReportPrintSettingsData>(`/reports/${moduleId}/print-settings`),
  })

  const title = useMemo(() => {
    const report = settings.data?.reports.find((item) => item.reportId === reportId)
      ?? settings.data?.reports.find((item) => item.isDefault)
      ?? settings.data?.reports[0]
    return report?.reportName ?? '单据打印'
  }, [settings.data, reportId])

  useEffect(() => {
    if (!settings.data) return
    const user = settings.data.userSettings
    const report = settings.data.reports.find((item) => item.isDefault) ?? settings.data.reports[0]
    setReportId((current) => current || user?.reportId || report?.reportId || '')
    setHeaderId((current) => current || user?.headerId || report?.headerId || '')
    setTailId((current) => current || user?.tailId || report?.tailId || '')
  }, [settings.data])

  const loadPdf = useCallback(async () => {
    if (!settings.isSuccess) return
    setPdfError(null)
    try {
      const blob = await apiClient.postFile(`/print/${moduleId}/pdf`, {
        key,
        reportId: reportId || null,
        headerId: headerId || null,
        tailId: tailId || null,
        showRemark,
      })
      const next = URL.createObjectURL(blob)
      if (pdfUrlRef.current) URL.revokeObjectURL(pdfUrlRef.current)
      pdfUrlRef.current = next
      setPdfUrl(next)
    } catch (error) {
      setPdfError(error instanceof ApiError ? error.body.message : 'PDF 生成失败，请重试。')
    }
  }, [settings.isSuccess, moduleId, key, reportId, headerId, tailId, showRemark])

  useEffect(() => { void loadPdf() }, [loadPdf])

  const handlePrint = async () => {
    // 保存最近一次打印设置（SYSQR IS_LAST=1，对齐旧 RptParent），保存失败不阻断打印
    try {
      await apiClient.post<void>(`/reports/${moduleId}/print-settings`, {
        reportId: reportId || null,
        headerId: headerId || null,
        tailId: tailId || null,
        sortSerialNo: null,
        sortAsc: true,
        showGroup: true,
        showDetail: true,
      })
    } catch { /* 忽略保存失败 */ }
    iframeRef.current?.contentWindow?.print()
  }

  if (settings.isPending) return <LoadingState label="正在加载打印设置…" />
  if (settings.isError) return <ErrorState message={settings.error instanceof ApiError ? settings.error.body.message : '打印设置加载失败。'} onRetry={() => void settings.refetch()} />
  if (settings.data.reports.length === 0) return <ErrorState message="没有可打印的报表，或您没有该报表的预览/打印权限。" onRetry={() => void settings.refetch()} />

  return (
    <div className="erp-print-page d-flex flex-column vh-100">
      <div className="border-bottom bg-white px-3 py-2 d-flex align-items-center gap-3 flex-wrap">
        <div className="fw-semibold">{title}</div>
        {settings.data.reports.length > 1 && (
          <label className="small mb-0">报表
            <select className="form-select form-select-sm ms-1" value={reportId} onChange={(event) => setReportId(event.target.value)}>
              {settings.data.reports.map((report) => <option key={report.reportId} value={report.reportId}>{report.reportName}</option>)}
            </select>
          </label>
        )}
        <label className="small mb-0">页头
          <select className="form-select form-select-sm ms-1" value={headerId} onChange={(event) => setHeaderId(event.target.value)}>
            <option value="">（报表默认）</option>
            {settings.data.headers.map((header) => <option key={header.headerId} value={header.headerId}>{header.headerName}</option>)}
          </select>
        </label>
        <label className="small mb-0">表尾
          <select className="form-select form-select-sm ms-1" value={tailId} onChange={(event) => setTailId(event.target.value)}>
            <option value="">（无）</option>
            {settings.data.tails.map((tail) => <option key={tail.tailId} value={tail.tailId}>{tail.tailName}</option>)}
          </select>
        </label>
        <label className="form-check small mb-0">
          <input className="form-check-input" type="checkbox" checked={showRemark} onChange={(event) => setShowRemark(event.target.checked)} />
          <span className="form-check-label">打印备注</span>
        </label>
        <div className="ms-auto d-flex gap-2">
          <Button size="sm" onClick={() => void handlePrint()}>打印</Button>
          <Button size="sm" variant="secondary" onClick={() => pdfUrl && window.open(pdfUrl, '_blank')}>新标签打开</Button>
        </div>
      </div>
      {pdfError && <div className="px-3 py-2 text-danger small">{pdfError}</div>}
      <div className="flex-grow-1">
        {pdfUrl ? (
          <iframe ref={iframeRef} title={`${title} PDF 预览`} src={pdfUrl} className="erp-pdf-frame w-100 h-100 border-0" />
        ) : (
          <div className="p-4"><LoadingState label="正在生成 PDF…" /></div>
        )}
      </div>
    </div>
  )
}
