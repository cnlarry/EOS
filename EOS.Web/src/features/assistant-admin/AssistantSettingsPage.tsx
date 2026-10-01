import { IconAlertTriangle, IconRefresh, IconRotate } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useMemo, useState } from 'react'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { Button } from '../../components/ui/Button'
import { useToast } from '../../components/ui/toastContext'
import { describeApiError } from '../../lib/errors'
import { listSettings, resetSetting, updateSetting } from './api'
import type { AssistantSettingItem } from './api'

/**
 * 工作助手管理 → **助手设置**（菜单组 31 / 模块 3105，路由 `/admin/assistant/settings`）。
 *
 * <para>
 * 这里放的是助手的**全局策略**：系统提示词、日上限与单价兜底、熔断阈值与冷却、自动提炼开关
 * （ADR-030 §8）。它们此前在 `appsettings` 里，改一次要改文件加重启；现在改完**立即生效**。
 * </para>
 *
 * <para>
 * **缺行 = 代码默认值**：所以"恢复默认"就是删掉覆盖行，界面上每项都显示后端给出的默认值。
 * 这样升级调整默认值时，没被改过的参数会自动跟着走——改过的那些必须留住。
 * </para>
 */
export function AssistantSettingsPage() {
  const queryClient = useQueryClient()
  const settings = useQuery({ queryKey: ['assistant-admin-settings'], queryFn: listSettings })

  const refresh = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ['assistant-admin-settings'] })
  }, [queryClient])

  const items = useMemo(() => settings.data?.items ?? [], [settings.data])
  const overridden = items.filter(item => item.isOverridden).length

  return (
    <div className="erp-full-list-page d-flex flex-column gap-3">
      <ErpListCard
        ariaLabel="助手设置"
        actions={<Button size="sm" icon={<IconRefresh size={16} />} onClick={refresh}>刷新</Button>}
        header={<div className="card-header py-2 d-flex align-items-center gap-2 flex-wrap">
          <h2 className="card-title mb-0">助手设置</h2>
          <span className="text-secondary small">
            共 {items.length} 项，其中 <strong>{overridden}</strong> 项被改过；其余用代码默认值
          </span>
          <span className="ms-auto text-secondary small">保存后立即生效，不需要重启服务</span>
        </div>}
      >
        {settings.isPending ? <LoadingState label="正在加载设置…" /> : settings.isError ? (
          <ErrorState message={describeApiError(settings.error, '加载助手设置失败。')} onRetry={refresh} />
        ) : (
          <>
            {/* 库里的值解析不了时要**当场**说出来：否则界面显示着一个其实没生效的值 */}
            {(settings.data?.problems.length ?? 0) > 0 && (
              <div className="alert alert-warning mb-0 rounded-0 border-0 border-bottom py-2" role="alert">
                {/* 这里不能写 Markdown 记号：JSX 文本不会渲染 **，只会把星号原样显示出来 */}
                <div className="fw-semibold d-flex align-items-center gap-1">
                  <IconAlertTriangle size={16} />
                  <span>有 {settings.data!.problems.length} 项设置没有生效</span>
                </div>
                <ul className="mb-0 small">
                  {settings.data!.problems.map(problem => <li key={problem}>{problem}</li>)}
                </ul>
              </div>
            )}
            <div className="p-3 d-flex flex-column gap-3">
              {items.map(item => (
                <SettingRow key={item.key} item={item} onChanged={refresh} />
              ))}
            </div>
          </>
        )}
      </ErpListCard>
    </div>
  )
}

/** 单项设置：文本/数字输入 + 保存 + 恢复默认；布尔项即时保存。 */
function SettingRow({ item, onChanged }: { item: AssistantSettingItem; onChanged: () => void }) {
  const toast = useToast()
  // 草稿只在本地：没点保存就离开不会写库（"改了但要能反悔"）
  const [draft, setDraft] = useState(item.value ?? item.defaultValue)
  const isBool = item.valueType === 'bool'
  const effective = item.value ?? item.defaultValue
  const boolValue = (item.value ?? item.defaultValue) === 'true'

  const onSuccess = useCallback((message: string) => {
    toast.notify({ message, variant: 'success' })
    onChanged()
  }, [onChanged, toast])

  const save = useMutation({
    mutationFn: (value: string) => updateSetting(item.key, value),
    onSuccess: () => onSuccess(`已保存「${item.displayName}」，立即生效。`),
    onError: (error) => toast.notify({ message: describeApiError(error, '保存失败。'), variant: 'danger' }),
  })

  const reset = useMutation({
    mutationFn: () => resetSetting(item.key),
    onSuccess: () => onSuccess(`「${item.displayName}」已恢复默认值。`),
    onError: (error) => toast.notify({ message: describeApiError(error, '恢复默认失败。'), variant: 'danger' }),
  })

  const dirty = draft.trim() !== effective.trim()
  const numeric = item.valueType === 'int' || item.valueType === 'decimal' || item.valueType === 'long'

  return (
    <section className="border rounded p-3">
      <div className="d-flex align-items-center gap-2 flex-wrap">
        <h3 className="h4 mb-0">{item.displayName}</h3>
        {item.isOverridden
          ? <span className="badge bg-blue-lt">已改过</span>
          : <span className="badge bg-secondary-lt">默认值</span>}
        {item.unit && <span className="text-secondary small">单位：{item.unit}</span>}
        <span className="ms-auto text-secondary small">
          默认值 <code>{item.defaultValue.length > 60 ? `${item.defaultValue.slice(0, 60)}…` : item.defaultValue}</code>
        </span>
      </div>

      {isBool ? (
        <label className="form-check mt-2">
          <input type="checkbox" className="form-check-input" checked={boolValue} disabled={save.isPending}
            aria-label={item.displayName}
            onChange={(event) => {
              // 开关没有"草稿"的概念：点了就是改了，直接写库
              const next = event.target.checked ? 'true' : 'false'
              setDraft(next)
              save.mutate(next)
            }} />
          <span className="form-check-label">当前：{boolValue ? '开启' : '关闭'}</span>
        </label>
      ) : (
        <div className="d-flex align-items-start gap-2 mt-2">
          {item.valueType === 'string'
            ? <textarea className="form-control font-monospace" rows={item.key === 'SystemPrompt' ? 6 : 2}
                value={draft} aria-label={item.displayName}
                onChange={(event) => setDraft(event.target.value)} />
            : <input type={numeric ? 'number' : 'text'} className="form-control" style={{ maxWidth: 240 }}
                value={draft} aria-label={item.displayName}
                onChange={(event) => setDraft(event.target.value)} />}
          <Button size="sm" variant="primary" loading={save.isPending} disabled={!dirty}
            onClick={() => save.mutate(draft.trim())}>保存</Button>
          {/* 清空输入 = 恢复默认，但仍给一个明确的入口：不必让人猜"清空算不算" */}
          <Button size="sm" icon={<IconRotate size={14} />} loading={reset.isPending}
            disabled={!item.isOverridden} title="删掉覆盖值，回到代码默认值"
            onClick={() => reset.mutate()}>恢复默认</Button>
        </div>
      )}

      <div className="form-hint mt-2">{item.description}</div>
      {item.isOverridden && item.updatedBy && (
        <div className="form-hint">
          上次由 {item.updatedBy} 于 {item.updatedAt ? new Date(item.updatedAt).toLocaleString('zh-CN') : '未知时间'} 修改
        </div>
      )}
    </section>
  )
}
