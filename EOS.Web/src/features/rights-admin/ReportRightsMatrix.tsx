import { useQuery } from '@tanstack/react-query'
import { useEffect, useMemo, useState } from 'react'
import { LoadingState } from '../../components/common/AsyncState'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'
import type { ReportRightsInput, ReportRightsRow } from './types'

interface ReportRightsMatrixProps {
  open: boolean
  mode: 'user' | 'group'
  targetId: string
  title?: string
  onClose: () => void
  onSaved?: () => void
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

export function ReportRightsMatrix({ open, mode, targetId, title, onClose, onSaved }: ReportRightsMatrixProps) {
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

  const [draft, setDraft] = useState<Record<string, ReportDraft>>({})
  const [dirty, setDirty] = useState<Set<string>>(new Set())
  const [expanded, setExpanded] = useState<Set<string>>(new Set())
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
    setExpanded(new Set())
    setError(null)
    setNotice(null)
  }, [open, matrix.data])

  const grouped = useMemo(() => {
    const map = new Map<number, ReportRightsRow[]>()
    for (const row of rows) {
      map.set(row.moduleId, [...(map.get(row.moduleId) ?? []), row])
    }
    return [...map.entries()].sort((a, b) => a[0] - b[0])
  }, [rows])

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
      setError(reason instanceof ApiError ? reason.body.message : '保存失败，请稍后重试。')
    } finally {
      setSaving(false)
    }
  }

  if (!open) return null

  return (
    <div className="modal modal-blur show d-block" role="dialog" aria-modal="true">
      <div className="modal-dialog modal-dialog-centered modal-lg">
        <div className="modal-content">
          <div className="modal-header">
            <h2 className="modal-title">{title ?? (mode === 'user' ? `用户报表权限：${targetId.trim()}` : `用户组报表权限：${targetId.trim()}`)}</h2>
            <button className="btn-close" aria-label="关闭" onClick={onClose} />
          </div>
          <div className="modal-body">
            <div className="alert alert-info py-1 px-2 small mb-2">个人报表权限存在时完全采用；组报表权限按组 OR 聚合。清空勾选并保存 = 删除该行，回退组权限。</div>
            {matrix.isPending ? <LoadingState label="正在加载报表权限…" /> : matrix.isError ? (
              <div className="alert alert-danger d-flex align-items-center justify-content-between">
                <span>{matrix.error instanceof ApiError ? matrix.error.body.message : '报表权限加载失败。'}</span>
                <Button variant="danger" size="sm" onClick={() => void matrix.refetch()}>重试</Button>
              </div>
            ) : rows.length === 0 ? (
              <div className="text-secondary py-4 text-center">当前可见模块下没有报表定义。</div>
            ) : (
              <div className="table-responsive rights-report-table">
                <table className="table table-sm table-hover align-middle">
                  <thead>
                    <tr>
                      <th>模块</th>
                      <th>报表</th>
                      <th className="text-center">预览</th>
                      <th className="text-center">列印</th>
                      <th className="text-center">导出</th>
                      <th>来源 / 生效</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {grouped.map(([moduleId, moduleRows]) => (
                      <>
                        {moduleRows.map((row) => {
                          const key = reportKey(row)
                          const value = draft[key]
                          if (!value) return null
                          const isExpanded = expanded.has(key)
                          const effective = row.effective
                          return (
                            <ReportRowFragment
                              key={key}
                              row={row}
                              value={value}
                              isExpanded={isExpanded}
                              dirty={dirty.has(key)}
                              moduleId={moduleId}
                              onToggleExpanded={() => {
                                setExpanded((current) => {
                                  const next = new Set(current)
                                  if (next.has(key)) next.delete(key)
                                  else next.add(key)
                                  return next
                                })
                              }}
                              onValue={(patch) => setValue(key, patch)}
                              effectiveSource={effective.source}
                              effectiveText={
                                effective.source === 'none'
                                  ? '无权限'
                                  : `${effective.source === 'personal' ? '个人' : '组'}：预览${effective.preview ? '✓' : '✗'} 列印${effective.print ? '✓' : '✗'} 导出${effective.export ? '✓' : '✗'}`
                              }
                            />
                          )
                        })}
                      </>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            {error && <div className="alert alert-danger py-2 mt-2 mb-0" role="alert">{error}</div>}
            {notice && <div className="alert alert-info py-2 mt-2 mb-0" role="status">{notice}</div>}
          </div>
          <div className="modal-footer">
            <div className="d-flex gap-2 ms-auto">
              <Button onClick={onClose}>取消</Button>
              <Button variant="primary" onClick={() => void save()} loading={saving}>保存</Button>
            </div>
          </div>
        </div>
      </div>
    </div>
  )
}

interface ReportRowFragmentProps {
  row: ReportRightsRow
  value: ReportDraft
  isExpanded: boolean
  dirty: boolean
  moduleId: number
  effectiveSource: string
  effectiveText: string
  onToggleExpanded: () => void
  onValue: (patch: Partial<ReportDraft>) => void
}

function ReportRowFragment({ row, value, isExpanded, dirty, moduleId, effectiveSource, effectiveText, onToggleExpanded, onValue }: ReportRowFragmentProps) {
  return (
    <>
      <tr>
        <td className="text-nowrap small">{moduleId}<span className="text-secondary ms-1">{row.moduleTitle}</span></td>
        <td>
          <div className="font-monospace small">{row.reportId}</div>
          <div className="small text-secondary">{row.reportName}</div>
        </td>
        <td className="text-center">
          <input type="checkbox" className="form-check-input m-0" checked={value.preview} onChange={(event) => onValue({ preview: event.target.checked })} aria-label={`预览 ${row.reportId}`} />
        </td>
        <td className="text-center">
          <input type="checkbox" className="form-check-input m-0" checked={value.print} onChange={(event) => onValue({ print: event.target.checked })} aria-label={`列印 ${row.reportId}`} />
        </td>
        <td className="text-center">
          <input type="checkbox" className="form-check-input m-0" checked={value.export} onChange={(event) => onValue({ export: event.target.checked })} aria-label={`导出 ${row.reportId}`} />
        </td>
        <td className="small">
          <span className={`badge ${effectiveSource === 'personal' ? 'bg-primary-subtle text-primary' : effectiveSource === 'group' ? 'bg-secondary-subtle text-secondary' : 'bg-light text-secondary'} me-1`}>
            {effectiveSource === 'personal' ? '个人' : effectiveSource === 'group' ? '组' : '无'}
          </span>
          <span className="text-secondary">{effectiveText}</span>
          {dirty && <span className="badge bg-warning-subtle text-warning ms-1">已修改</span>}
        </td>
        <td className="text-end">
          <button type="button" className="btn btn-sm btn-outline-secondary" onClick={onToggleExpanded}>
            {isExpanded ? '收起过滤' : 'DATA_FILTER'}
          </button>
        </td>
      </tr>
      {isExpanded && (
        <tr>
          <td colSpan={7} className="bg-light">
            <div className="d-flex align-items-center gap-2">
              <label className="form-label mb-0 text-nowrap small">DATA_FILTER</label>
              <input
                className="form-control form-control-sm font-monospace"
                value={value.dataFilter}
                onChange={(event) => onValue({ dataFilter: event.target.value })}
                placeholder="受控过滤表达式（非法保存时拒绝 400）"
              />
            </div>
          </td>
        </tr>
      )}
    </>
  )
}
