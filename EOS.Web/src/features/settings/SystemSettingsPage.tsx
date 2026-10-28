import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useParams } from 'react-router-dom'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { apiClient } from '../../services/api'
import { SystemSettingsPanel } from './SystemSettingsPanel'
import {
  initialSettingsForm,
  normalizeScope,
  type SettingsForm,
  type SystemParameterList,
} from './settingsTypes'

/**
 * 系统参数设置页 / 考勤数据设置页 / 薪资考勤设置页：路由段决定设置范围
 * （system 110111 / hr-setup 180213 / hrm-setup 180662，菜单 URL 不变）。
 *
 * <p>
 * 页面根只负责"整页占满 + 命令栏 + 两栏内容"的骨架，两栏本体在
 * `SystemSettingsPanel`；保存是**整页一个动作**（提交 form 里全部参数），
 * 所以编辑态只在这里持有。
 * </p>
 */
export function SystemSettingsPage() {
  const { table } = useParams<{ table: string }>()
  const scope = normalizeScope(table)
  const [form, setForm] = useState<SettingsForm>({})
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
    <div className="erp-base-page erp-page-fill">
      {/* 保存结果固定在两栏之上，不随内容滚走：改完滚到最底下才看见"到底存没存进去"是最糟的顺序 */}
      {saved && <div role="alert" className="alert alert-success py-2 mb-0">设置成功！</div>}
      {saveError && <div role="alert" className="alert alert-danger py-2 mb-0">{saveError}</div>}
      <SystemSettingsPanel
        groups={groups}
        form={form}
        onChange={change}
        onSave={() => void save()}
        saving={saving}
      />
    </div>
  )
}
