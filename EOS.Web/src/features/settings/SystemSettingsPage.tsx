import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { useParams } from 'react-router-dom'

export function SystemSettingsPage() {
  const { table = 'SYSSS' } = useParams<{ table: string }>()
  const apiTable = table.toUpperCase() === 'HR-SETUP' ? 'HR_SETUP' : table.toUpperCase() === 'HRM-SETUP' ? 'HRM_SETUP' : 'SYSSS'
  const [values, setValues] = useState<Record<string, string>>({})
  const [saving, setSaving] = useState(false)
  const [saved, setSaved] = useState(false)
  const settings = useQuery({
    queryKey: ['settings', apiTable],
    queryFn: async () => {
      const data = await apiClient.get<Record<string, unknown>>(`/settings/${apiTable}`)
      const initial: Record<string, string> = {}
      Object.entries(data).forEach(([key, value]) => { initial[key] = value === null ? '' : String(value) })
      setValues(initial)
      return data
    },
  })

  const save = async () => {
    setSaving(true)
    setSaved(false)
    try {
      await apiClient.put(`/settings/${apiTable}`, values)
      setSaved(true)
    } catch (error) {
      window.alert(error instanceof Error ? `保存失败：${error.message}` : '保存失败。')
    } finally {
      setSaving(false)
    }
  }

  if (settings.isPending) return <LoadingState label="正在加载系统参数…" />
  if (settings.isError) return <ErrorState message="系统参数加载失败。" onRetry={() => void settings.refetch()} />
  const entries = Object.entries(values)

  return (
    <div className="d-grid gap-2">
      <ErpListCard
        ariaLabel="系统参数设置"
        search={null}
        actions={<>
          <Button size="sm" onClick={() => void save()} loading={saving}>保存</Button>
        </>}
      >
        {saved && <div className="alert alert-success py-2 mb-2">保存成功。</div>}
        <div className="table-responsive">
          <table className="table table-sm table-vcenter card-table">
            <thead><tr><th className="w-50">参数</th><th>值</th></tr></thead>
            <tbody>
              {entries.map(([key, value]) => (
                <tr key={key}>
                  <td className="font-monospace small">{key}</td>
                  <td><input className="form-control form-control-sm" value={value} onChange={(event) => setValues((current) => ({ ...current, [key]: event.target.value }))} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </ErpListCard>
    </div>
  )
}
