import { useQuery } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { useParams } from 'react-router-dom'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { SysSettingsLayout } from './SysSettingsLayout'
import {
  initialSettingsForm,
  type SettingsForm,
  type SysSettingsField,
  type SysSettingsPayload,
} from './settingsTypes'

function normalizeTable(table: string): string {
  const upper = table.toUpperCase()
  if (upper === 'HR-SETUP') return 'HR_SETUP'
  if (upper === 'HRM-SETUP') return 'HRM_SETUP'
  return 'SYSSS'
}

/**
 * 单行参数表设置页。
 * - SYSSS（110111 系统参数设置）：完整复刻旧 MagSysSet.aspx 布局（七分区、标签、控件类型、保存副作用）；
 * - HR_SETUP / HRM_SETUP（180213 / 180662）：保留通用参数网格。
 */
export function SystemSettingsPage() {
  const { table = 'SYSSS' } = useParams<{ table: string }>()
  const apiTable = normalizeTable(table)
  const [form, setForm] = useState<SettingsForm>({})
  const [saving, setSaving] = useState(false)
  const [saved, setSaved] = useState(false)

  const settings = useQuery({
    queryKey: ['settings', apiTable],
    queryFn: async () => {
      const data = await apiClient.get<SysSettingsPayload>(`/settings/${apiTable}`)
      setForm(initialSettingsForm(data.fields, data.values))
      return data
    },
  })

  const fields = useMemo(() => {
    const map: Record<string, SysSettingsField> = {}
    for (const field of settings.data?.fields ?? []) map[field.key] = field
    return map
  }, [settings.data])

  const save = async () => {
    if (!window.confirm('你确定要保存吗？')) return
    setSaving(true)
    setSaved(false)
    try {
      await apiClient.put(`/settings/${apiTable}`, form)
      setSaved(true)
    } catch (error) {
      const message = error instanceof Error ? error.message : ''
      window.alert(`未知错误，设置失败！${message ? `（${message}）` : ''}`)
    } finally {
      setSaving(false)
    }
  }

  const change = (key: string, value: string) => setForm((current) => ({ ...current, [key]: value }))

  if (settings.isPending) return <LoadingState label="正在加载系统参数…" />
  if (settings.isError) return <ErrorState message="系统参数加载失败。" onRetry={() => void settings.refetch()} />

  if (apiTable === 'SYSSS') {
    return (
      <SysSettingsLayout
        fields={fields}
        form={form}
        onChange={change}
        onSave={() => void save()}
        saving={saving}
        saved={saved}
      />
    )
  }

  return (
    <div className="d-grid gap-2">
      <ErpListCard
        ariaLabel="系统参数设置"
        search={null}
        actions={(
          <Button size="sm" onClick={() => void save()} loading={saving}>保存</Button>
        )}
      >
        {saved && <div role="alert" className="alert alert-success py-2 mb-2">设置成功！</div>}
        <div className="table-responsive">
          <table className="table table-sm table-vcenter card-table">
            <thead>
              <tr><th className="w-50">参数</th><th>值</th></tr>
            </thead>
            <tbody>
              {Object.entries(form).map(([key, value]) => (
                <tr key={key}>
                  <td>{fields[key]?.label ?? key}</td>
                  <td>
                    <input
                      aria-label={fields[key]?.label ?? key}
                      className="form-control form-control-sm"
                      value={value}
                      onChange={(event) => change(key, event.target.value)}
                    />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </ErpListCard>
    </div>
  )
}
