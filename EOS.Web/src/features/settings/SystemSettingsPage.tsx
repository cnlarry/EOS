import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useParams } from 'react-router-dom'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { apiClient } from '../../services/api'
import { SystemSettingsTabs } from './SystemSettingsTabs'
import {
  initialSettingsForm,
  normalizeScope,
  type SettingsForm,
  type SystemParameterList,
} from './settingsTypes'

/**
 * 系统参数设置页 / 考勤数据设置页：路由段决定设置范围
 * （system 110111 / hr-setup 180213 / hrm-setup 180662，菜单 URL 不变），
 * 参数按分组以选项卡呈现，控件由参数类型决定。
 */
export function SystemSettingsPage() {
  const { table } = useParams<{ table: string }>()
  const scope = normalizeScope(table)
  const [form, setForm] = useState<SettingsForm>({})
  const [activeGroupCode, setActiveGroupCode] = useState('')
  const [saving, setSaving] = useState(false)
  const [saved, setSaved] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)

  const settings = useQuery({
    queryKey: ['settings', scope],
    queryFn: async () => {
      const data = await apiClient.get<SystemParameterList>(`/settings/${scope}`)
      setForm(initialSettingsForm(data.groups))
      return data
    },
  })

  const save = async () => {
    if (!window.confirm('你确定要保存吗？')) return
    setSaving(true)
    setSaved(false)
    setSaveError(null)
    try {
      await apiClient.put(`/settings/${scope}`, form)
      setSaved(true)
    } catch (error) {
      const message = error instanceof Error ? error.message : ''
      setSaveError(`设置失败！${message ? `（${message}）` : ''}`)
    } finally {
      setSaving(false)
    }
  }

  const change = (key: string, value: string) => setForm((current) => ({ ...current, [key]: value }))

  if (settings.isPending) return <LoadingState label="正在加载系统参数…" />
  if (settings.isError) return <ErrorState message="系统参数加载失败。" onRetry={() => void settings.refetch()} />

  const groups = settings.data?.groups ?? []
  return (
    <SystemSettingsTabs
      groups={groups}
      form={form}
      activeGroupCode={activeGroupCode || groups[0]?.groupCode || ''}
      onSelectGroup={setActiveGroupCode}
      onChange={change}
      onSave={() => void save()}
      saving={saving}
      saved={saved}
      saveError={saveError}
    />
  )
}
