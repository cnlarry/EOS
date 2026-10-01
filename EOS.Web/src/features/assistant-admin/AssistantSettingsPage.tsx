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
import { AssistantScopeOverrides } from './AssistantScopeOverrides'

/**
 * 工作助手管理 → **助手设置**（菜单组 31 / 模块 3105，路由 `/admin/assistant/settings`）。
 *
 * <para>
 * 这一页是**助手参数目录的视图**（ADR-030 §5）：参数的声明在服务端代码
 * （`AssistantParameterCatalog`），取值在 `dbo.SYSSS` 的 `OWNER_MODULE = 3105`。页面按**域**分组
 * （提示词 / 成本与熔断 / 记忆 / …），每一节由服务端返回的分组顺序决定，前端不写死分组清单。
 * </para>
 *
 * <para>
 * **目录与库按批同步生长**：分组会随批次变多，所以这里不做"分组数量"假设，空分组不渲染。
 * </para>
 *
 * <para>
 * **默认值与取值范围都来自服务端**（同一份声明），前端只负责显示：这样不会出现"界面提示 5 元、
 * 代码其实是 8 元"。每一项还显示它的**读取方**——一个没有读取方的参数仍然可改，但它此刻不起作用，
 * 这一点必须看得见，而不是让管理员以为改了就生效。
 * </para>
 *
 * <para>
 * **保存是整页一个动作**（右上角，与"刷新"并排）：这一页是一张表单，改几项再一起存更合直觉。
 * 于是编辑中的内容先落在**本地草稿**里，保存按钮上的数字就是"待保存几项"；每一行另有"未保存"徽标。
 * 逐项提交（而不是一次提交整页）是刻意的：某一项被服务端拒了要能**指名道姓**说清是哪一项。
 * </para>
 *
 * <para>
 * **"恢复默认"仍即时生效**：它做的是让服务端**清空取值**，不是"把默认值存成一个覆盖值"，
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

  /** 按服务端给出的分组顺序切片；空分组不渲染（目录按批生长，先占号的域此刻还没有参数行）。 */
  const sections = useMemo(() => {
    const groups = settings.data?.groups ?? []
    return groups
      .map(group => ({
        ...group,
        items: items
          .filter(item => item.groupCode === group.code)
          .sort((left, right) => left.seqNo - right.seqNo),
      }))
      .filter(section => section.items.length > 0)
  }, [settings.data, items])

  const overridden = items.filter(item => item.isOverridden).length
  const orphaned = items.filter(item => item.consumers.length === 0).length

  const refresh = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ['assistant-admin-settings'] })
  }, [queryClient])

  /** 服务端此刻的有效值（取值列为空时就是默认值）。 */
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
      // 逐项写、逐项记账：某一项被服务端拒了（比如"必须大于 0"），要能指名道姓说清是哪一项，
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
            共 {items.length} 项，其中 <strong>{overridden}</strong> 项被改过；其余用默认值
          </span>
          {orphaned > 0 && (
            <span className="badge bg-warning-lt" title="这些参数还没有读取方，此刻改了不起作用">
              {orphaned} 项目前无读取方
            </span>
          )}
          <span className="ms-auto text-secondary small">保存后立即生效，不需要重启服务</span>
        </div>}
      >
        {settings.isPending ? <LoadingState label="正在加载设置…" /> : settings.isError ? (
          <ErrorState message={describeApiError(settings.error, '加载助手设置失败。')} onRetry={refresh} />
        ) : (
          <>
            {/* 库里的值解析不了、或目录有而库里没有时，要**当场**说出来：
                否则界面显示着一个其实没生效的值 */}
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
            <div className="erp-assistant-settings-body p-3 d-flex flex-column gap-4">
              {sections.map(section => (
                <section key={section.code}>
                  <div className="d-flex align-items-center gap-2 mb-2">
                    <h3 className="h5 mb-0">{section.label}</h3>
                    <span className="text-secondary small font-monospace">{section.code}</span>
                    <span className="text-secondary small">{section.items.length} 项</span>
                  </div>
                  <div className="d-flex flex-column gap-3">
                    {section.items.map(item => (
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
                </section>
              ))}

              {/* 作用域覆盖：全局值之上还有"按用户 / 按模块"两层（ADR-030 §6.2）。
                  单独成块而不是塞进各参数下面，是因为它维护的是**对象维度**（谁 / 哪个模块），
                  与"每一项参数一个值"不是同一种编辑。 */}
              <AssistantScopeOverrides />
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
  const isBool = item.valueType === 'bit'
  const boolValue = draft === '1' || draft === 'true'
  const numeric = item.valueType === 'int' || item.valueType === 'decimal'

  return (
    <section className="border rounded p-3">
      <div className="d-flex align-items-center gap-2 flex-wrap">
        <h4 className="h5 mb-0">{item.displayName}</h4>
        {item.isOverridden
          ? <span className="badge bg-blue-lt">已改过</span>
          : <span className="badge bg-secondary-lt">默认值</span>}
        {unsaved && <span className="badge bg-warning-lt">未保存</span>}
        {item.unit && <span className="text-secondary small">单位：{item.unit}</span>}
        {item.rangeHint && <span className="text-secondary small">（{item.rangeHint}）</span>}
        <span className="ms-auto text-secondary small font-monospace">{item.key}</span>
      </div>

      {isBool ? (
        <label className="form-check mt-2">
          <input type="checkbox" className="form-check-input" checked={boolValue}
            aria-label={item.displayName}
            onChange={(event) => onDraftChange(event.target.checked ? '1' : '0')} />
          <span className="form-check-label">当前：{boolValue ? '开启' : '关闭'}</span>
        </label>
      ) : (
        <div className="d-flex align-items-start gap-2 mt-2">
          {item.valueType === 'string'
            ? <textarea className="form-control font-monospace" rows={item.key === 'SYSTEM_PROMPT' ? 6 : 2}
                value={draft} aria-label={item.displayName}
                onChange={(event) => onDraftChange(event.target.value)} />
            : <input type={numeric ? 'number' : 'text'} className="form-control" style={{ maxWidth: 240 }}
                value={draft} aria-label={item.displayName}
                onChange={(event) => onDraftChange(event.target.value)} />}
          {/* 清空输入 = 恢复默认，但仍给一个明确的入口：不必让人猜"清空算不算" */}
          <Button size="sm" icon={<IconRotate size={14} />} loading={resetPending}
            disabled={!item.isOverridden} title="清空取值，回到默认值"
            onClick={onReset}>恢复默认</Button>
        </div>
      )}

      <div className="form-hint mt-2">{item.description}</div>
      {item.consumers.length > 0 ? (
        <div className="form-hint">读取方：<span className="font-monospace">{item.consumers.join('、')}</span></div>
      ) : (
        <div className="form-hint text-warning">暂无读取方——改它此刻不会影响助手行为。</div>
      )}
      <div className="form-hint">
        默认值 <code>{item.defaultValue.length > 60 ? `${item.defaultValue.slice(0, 60)}…` : item.defaultValue}</code>
      </div>
      {item.isOverridden && item.updatedBy && (
        <div className="form-hint">
          上次由 {item.updatedBy} 于 {item.updatedAt ? new Date(item.updatedAt).toLocaleString('zh-CN') : '未知时间'} 修改
        </div>
      )}
    </section>
  )
}
