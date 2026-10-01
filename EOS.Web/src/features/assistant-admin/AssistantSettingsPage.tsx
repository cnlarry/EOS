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
 *
 * <para>
 * **保存是整页一个动作**（右上角，与"刷新"并排），不再是每个设置项各配一个"保存"：这一页是一张
 * 表单，改几项再一起存更合直觉，也少了一排重复按钮。于是编辑中的内容先落在**本地草稿**里，
 * 保存按钮上的数字就是"待保存几项"；每一行另有"未保存"徽标，免得一排输入框里看不出动过哪些。
 * </para>
 *
 * <para>
 * **"恢复默认"仍即时生效**：它做的是让服务端**删掉覆盖行**，不是"把默认值存成一个覆盖值"，
 * 所以不排队等"保存"。
 * </para>
 */
export function AssistantSettingsPage() {
  const queryClient = useQueryClient()
  const toast = useToast()
  const settings = useQuery({ queryKey: ['assistant-admin-settings'], queryFn: listSettings })

  // 草稿只记**被动过的**键：没动过的项直接读服务端的有效值。
  // 这样后台重新拉取（切窗口回来、保存后失效重取）不会把用户正在编辑的内容冲掉。
  const [drafts, setDrafts] = useState<Record<string, string>>({})

  const items = useMemo(() => settings.data?.items ?? [], [settings.data])
  const overridden = items.filter(item => item.isOverridden).length

  const refresh = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ['assistant-admin-settings'] })
  }, [queryClient])

  /** 服务端此刻的有效值（没有覆盖行时就是代码默认值）。 */
  const baselineOf = useCallback((item: AssistantSettingItem) => item.value ?? item.defaultValue, [])

  /** 界面上该显示的值：本地草稿优先。 */
  const draftOf = useCallback(
    (item: AssistantSettingItem) => drafts[item.key] ?? baselineOf(item),
    [drafts, baselineOf])

  /** 待保存的项：去掉首尾空白再比——只多打了几个空格不算改动。 */
  const pending = useMemo(
    () => items
      .filter(item => draftOf(item).trim() !== baselineOf(item).trim())
      .map(item => ({ key: item.key, displayName: item.displayName, value: draftOf(item).trim() })),
    [items, draftOf, baselineOf])

  const save = useMutation({
    mutationFn: async () => {
      // 逐项写、逐项记账：某一项被服务端拒了（比如"日上限必须大于 0"），要能指名道姓说清是哪一项，
      // 而不是笼统报一句"保存失败"，让用户自己去猜是哪一格。
      const savedKeys: string[] = []
      const failed: string[] = []
      for (const item of pending) {
        try {
          await updateSetting(item.key, item.value)
          savedKeys.push(item.key)
        } catch (error) {
          failed.push(`${item.displayName}：${describeApiError(error, '写入失败')}`)
        }
      }
      // 保存完必须重新拉一次：否则界面上还显示着旧值，看不出到底存进去没有
      await queryClient.invalidateQueries({ queryKey: ['assistant-admin-settings'] })
      return { savedKeys, failed }
    },
    onSuccess: ({ savedKeys, failed }) => {
      // 存进去的那些把草稿清掉，让界面回到服务端事实；**没存进去的留着草稿**——
      // 否则失败会被当成成功一起清掉，用户以为存上了、界面也不再提示。
      setDrafts(previous => {
        const next = { ...previous }
        for (const key of savedKeys) delete next[key]
        return next
      })
      if (failed.length === 0) {
        toast.notify({ message: `已保存 ${savedKeys.length} 项，立即生效。`, variant: 'success' })
        return
      }
      toast.notify({ message: `有 ${failed.length} 项没存进去：${failed.join('；')}`, variant: 'danger' })
    },
    onError: (error) => toast.notify({ message: describeApiError(error, '保存失败。'), variant: 'danger' }),
  })

  const reset = useMutation({
    mutationFn: (item: AssistantSettingItem) => resetSetting(item.key),
    onSuccess: (_result, item) => {
      // 顺带清掉这一行的草稿：否则草稿会把刚回到默认的值又顶成一个"待保存"的改动
      setDrafts(previous => {
        const next = { ...previous }
        delete next[item.key]
        return next
      })
      toast.notify({ message: `「${item.displayName}」已恢复默认值。`, variant: 'success' })
      refresh()
    },
    onError: (error) => toast.notify({ message: describeApiError(error, '恢复默认失败。'), variant: 'danger' }),
  })

  return (
    <div className="erp-full-list-page">
      <ErpListCard
        ariaLabel="助手设置"
        actions={<>
          <Button size="sm" variant="primary" loading={save.isPending} disabled={pending.length === 0}
            title={pending.length === 0 ? '没有待保存的改动' : `保存 ${pending.length} 项改动`}
            onClick={() => save.mutate()}>
            {pending.length > 0 ? `保存（${pending.length}）` : '保存'}
          </Button>
          <Button size="sm" icon={<IconRefresh size={16} />} onClick={refresh}>刷新</Button>
        </>}
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
            {/* 设置项比一屏高，所以给它们**自己的滚动位**：命令栏（保存/刷新）与页头因此固定不动，
                不会出现"往下滚就找不着保存按钮"。列表页的滚动交给表格内部，这一页没有表格。 */}
            <div className="erp-assistant-settings-body p-3 d-flex flex-column gap-3">
              {items.map(item => (
                <SettingRow
                  key={item.key}
                  item={item}
                  draft={draftOf(item)}
                  unsaved={draftOf(item).trim() !== baselineOf(item).trim()}
                  onDraftChange={(value) => setDrafts(previous => ({ ...previous, [item.key]: value }))}
                  onReset={() => reset.mutate(item)}
                  resetPending={reset.isPending && reset.variables?.key === item.key}
                />
              ))}
            </div>
          </>
        )}
      </ErpListCard>
    </div>
  )
}

/**
 * 单项设置：输入框 + 恢复默认。
 *
 * <para>
 * **没有行内"保存"**：保存是整页一个动作（右上角）。所以这里只负责把改动报上去，
 * 并用"未保存"徽标标明这一行动过——否则一排输入框里哪些改了就看不出。
 * </para>
 */
function SettingRow({ item, draft, unsaved, onDraftChange, onReset, resetPending }: {
  item: AssistantSettingItem
  draft: string
  unsaved: boolean
  onDraftChange: (value: string) => void
  onReset: () => void
  resetPending: boolean
}) {
  const isBool = item.valueType === 'bool'
  const boolValue = draft === 'true'
  const numeric = item.valueType === 'int' || item.valueType === 'decimal' || item.valueType === 'long'

  return (
    <section className="border rounded p-3">
      <div className="d-flex align-items-center gap-2 flex-wrap">
        <h3 className="h4 mb-0">{item.displayName}</h3>
        {item.isOverridden
          ? <span className="badge bg-blue-lt">已改过</span>
          : <span className="badge bg-secondary-lt">默认值</span>}
        {unsaved && <span className="badge bg-warning-lt">未保存</span>}
        {item.unit && <span className="text-secondary small">单位：{item.unit}</span>}
        <span className="ms-auto text-secondary small">
          默认值 <code>{item.defaultValue.length > 60 ? `${item.defaultValue.slice(0, 60)}…` : item.defaultValue}</code>
        </span>
      </div>

      {isBool ? (
        <label className="form-check mt-2">
          <input type="checkbox" className="form-check-input" checked={boolValue}
            aria-label={item.displayName}
            onChange={(event) => onDraftChange(event.target.checked ? 'true' : 'false')} />
          <span className="form-check-label">当前：{boolValue ? '开启' : '关闭'}</span>
        </label>
      ) : (
        <div className="d-flex align-items-start gap-2 mt-2">
          {item.valueType === 'string'
            ? <textarea className="form-control font-monospace" rows={item.key === 'SystemPrompt' ? 6 : 2}
                value={draft} aria-label={item.displayName}
                onChange={(event) => onDraftChange(event.target.value)} />
            : <input type={numeric ? 'number' : 'text'} className="form-control" style={{ maxWidth: 240 }}
                value={draft} aria-label={item.displayName}
                onChange={(event) => onDraftChange(event.target.value)} />}
          {/* 清空输入 = 恢复默认，但仍给一个明确的入口：不必让人猜"清空算不算" */}
          <Button size="sm" icon={<IconRotate size={14} />} loading={resetPending}
            disabled={!item.isOverridden} title="删掉覆盖值，回到代码默认值"
            onClick={onReset}>恢复默认</Button>
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
