import { IconAlertTriangle, IconDeviceFloppy, IconRefresh, IconRotate } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpSelectionList } from '../../components/common/ErpSelectionList'
import { useSectionScrollSpy } from '../../components/common/useSectionScrollSpy'
import { Button } from '../../components/ui/Button'
import { useToast } from '../../components/ui/toastContext'
import { describeApiError } from '../../lib/errors'
import { listPromptSegments, listSettings, resetSetting, updatePromptSegment, updateSetting } from './api'
import type { AssistantPromptSegment, AssistantSettingItem } from './api'
import { AssistantScopeOverrides, SCOPE_ANCHOR, SCOPE_CODE, SCOPE_LABEL } from './AssistantScopeOverrides'

/** 行为规则段的锚点：文本在库（ADR-046），与参数是两种编辑对象，所以单独一块。 */
const PROMPT_ANCHOR = '__prompt__'
const PROMPT_LABEL = '行为规则（规则段）'
/** 规则段的英文标识：左导航的第二行与这一块的标题栏都显示它，与"域"的 code 同一口径。 */
const PROMPT_CODE = 'PROMPT_SEGMENT'

/**
 * 工作助手管理 → **助手设置**（菜单组 31 / 模块 3105，路由 `/admin/assistant/settings`）。
 *
 * <para>
 * 这一页是**助手参数目录的视图**（ADR-030 §5）：参数的声明在服务端代码
 * （`AssistantParameterCatalog`），取值在 `dbo.SYS_PARAMETER` 的 `OWNER_MODULE = 3105`。
 * 布局是**左导航 + 右配置**：左侧按**域**列（提示词 / 成本与熔断 / 记忆 / …），右侧逐域展开；
 * 分组清单与组内顺序都由服务端给，前端不写死。两栏骨架与系统参数页共用
 * （`.erp-settings-split-*`），滚动联动同用 `useSectionScrollSpy`。
 * </para>
 *
 * <para>
 * **导航为什么只到"域"这一级**：目录有一百多项参数，做成一百多条导航等于让导航本身变成
 * 需要导航的东西。当前读到哪一项由右侧顶部那行 sticky（「当前 · 域 · 参数名」）承担——
 * 滚动时它跟着变，所以"我在哪一项旁边"始终看得见，而不必去左侧找。
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
 * **保存是整页一个动作**（工具栏，与"刷新"并排）：这一页是一张表单，改几项再一起存更合直觉。
 * 于是编辑中的内容先落在**本地草稿**里，保存按钮上的数字就是"待保存几项"；每一行另有"未保存"徽标，
 * 左侧导航上还会点一颗点——不然"哪个域里还有没存的改动"要滚遍全页才知道。
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
  // 行为规则段：与参数是两种编辑对象（逐段保存、红线只读），所以单独一个查询。
  const promptSegments = useQuery({ queryKey: ['assistant-prompt-segments'], queryFn: listPromptSegments })

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

  /** 每个域的"改过几项 / 待保存几项"，供左侧导航点出息。 */
  const groupMarks = useMemo(() => {
    const marks = new Map<string, { overridden: number; pending: number }>()
    for (const section of sections) {
      let overriddenCount = 0
      let pendingCount = 0
      for (const item of section.items) {
        if (item.isOverridden) overriddenCount += 1
        if (draftOf(item).trim() !== baselineOf(item).trim()) pendingCount += 1
      }
      marks.set(section.code, { overridden: overriddenCount, pending: pendingCount })
    }
    return marks
  }, [sections, draftOf, baselineOf])

  // 左侧域导航与右侧内容的滚动联动（实现见 components/common/useSectionScrollSpy）
  const { paneRef, activeGroupCode, activeItemLabel, onScroll, jumpTo } = useSectionScrollSpy(sections)

  const activeLabel = activeGroupCode === SCOPE_ANCHOR
    ? SCOPE_LABEL
    : (sections.find(section => section.code === activeGroupCode)?.label ?? '—')

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
    <div className="erp-base-page erp-page-fill">
      <ErpListCard
        ariaLabel="助手设置"
        // 标题与统计走**命令栏左侧**（`search` 槽）：它们与保存按钮说的是同一件事——
        // 这批设置一共几项、改了几项、还有几项没存。分成上下两行，就得在两处拼着看才知道要不要点保存。
        search={<div className="erp-settings-split-title">
          <h2 className="card-title mb-0">助手设置</h2>
          <span className="text-secondary small">
            共 {items.length} 项，其中 <strong>{overridden}</strong> 项被改过；其余用默认值
            （保存后立即生效，不需要重启服务）
          </span>
          {orphaned > 0 && (
            <span className="badge bg-warning-lt" title="这些参数还没有读取方，此刻改了不起作用">
              {orphaned} 项目前无读取方
            </span>
          )}
        </div>}
        actions={<>
          {/* 图标 + 文字 + `.erp-command-btn`：与管理面其余页（模型与用量、配置面）同一口径；
              主操作靠主色区分。图标尺寸 16px 是该类的约定，不要在这里另给 size。 */}
          <Button size="sm" variant="primary" className="erp-command-btn"
            icon={<IconDeviceFloppy size={16} />}
            loading={save.isPending} disabled={pending.length === 0}
            title={pending.length === 0 ? '没有待保存的改动' : `保存 ${pending.length} 项改动，保存后立即生效`}
            onClick={() => save.mutate()}>
            {pending.length > 0 ? `保存（${pending.length}）` : '保存'}
          </Button>
          <Button size="sm" className="erp-command-btn" icon={<IconRefresh size={16} />}
            title="重新从服务端读取参数（本地未保存的改动会保留）" onClick={refresh}>刷新</Button>
        </>}
      >
        {settings.isPending ? <LoadingState label="正在加载设置…" /> : settings.isError ? (
          <ErrorState message={describeApiError(settings.error, '加载助手设置失败。')} onRetry={refresh} />
        ) : (
          <>
            {/* 库里的值解析不了、或目录有而库里没有时，要**当场**说出来：
                否则界面显示着一个其实没生效的值。它固定在两栏之上，不随内容滚走。 */}
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

            <div className="card-body erp-settings-split">
              {/* 左：域导航。只到"域"这一级，理由见组件头注释；选中态由 ErpSelectionList 提供 */}
              <ErpSelectionList
                ariaLabel="设置分组"
                className="erp-settings-rail"
                items={[
                  ...sections.map(section => {
                    const mark = groupMarks.get(section.code)
                    const pendingCount = mark?.pending ?? 0
                    const changedCount = mark?.overridden ?? 0
                    return {
                      id: section.code,
                      label: section.label,
                      // 第二行是这一域的 code（与服务端的分组标识同一份），管理员对着日志/参数键说话时用得上
                      description: <span className="font-monospace">{section.code}</span>,
                      tooltip: pendingCount > 0
                        ? `${pendingCount} 项改动还没保存`
                        : (changedCount > 0 ? `${changedCount} 项已改过（已生效）` : undefined),
                      meta: (
                        <>
                          {pendingCount > 0
                            ? <span className="erp-settings-rail-dot" />
                            : (changedCount > 0 ? <span className="erp-settings-rail-dot is-saved" /> : null)}
                          <span>{section.items.length}</span>
                        </>
                      ),
                    }
                  }),
                  {
                    id: PROMPT_ANCHOR,
                    label: PROMPT_LABEL,
                    description: <span className="font-monospace">{PROMPT_CODE}</span>,
                    meta: promptSegments.data ? <span>{promptSegments.data.length}</span> : undefined,
                  },
                  {
                    id: SCOPE_ANCHOR,
                    label: SCOPE_LABEL,
                    description: <span className="font-monospace">{SCOPE_CODE}</span>,
                  },
                ]}
                activeId={activeGroupCode}
                onSelect={jumpTo}
              />

              {/* 右：配置内容。滚动发生在这里，页面级不出滚动条 */}
              <div className="erp-settings-split-pane" ref={paneRef} onScroll={onScroll}>
                <div className="erp-settings-split-now">
                  <span className="text-secondary">当前</span>
                  <span className="fw-semibold">{activeLabel}</span>
                  {activeItemLabel && (
                    <>
                      <span className="text-secondary">·</span>
                      <span>{activeItemLabel}</span>
                    </>
                  )}
                  {/* 右侧只在**有活没干完**时说话：空闲时不重复命令栏已经写着的"保存后立即生效" */}
                  {pending.length > 0 && (
                    <span className="ms-auto text-secondary small">{pending.length} 项待保存</span>
                  )}
                </div>

                {sections.map(section => (
                  <section key={section.code} className="erp-settings-split-section"
                    data-settings-anchor="group" data-group-code={section.code}
                    data-group-label={section.label}>
                    <div className="erp-settings-split-section-head">
                      <h3 className="h5 mb-0">{section.label}</h3>
                      <span className="text-secondary small font-monospace">{section.code}</span>
                      <span className="text-secondary small">{section.items.length} 项</span>
                    </div>
                    {section.items.map(item => (
                      <SettingRow
                        key={item.key}
                        item={item}
                        groupCode={section.code}
                        draft={draftOf(item)}
                        unsaved={draftOf(item).trim() !== baselineOf(item).trim()}
                        onDraftChange={(value) => setDrafts(previous => ({ ...previous, [item.key]: value }))}
                        onReset={() => reset.mutate(item)}
                        resetPending={reset.isPending && reset.variables?.key === item.key}
                      />
                    ))}
                  </section>
                ))}

                {/* 行为规则段：文本在库（ADR-046），改完下一轮对话生效。
                    与上面的参数不同，这里**逐段保存**——一段话是一个整体，没有"整页保存"的语义；
                    锁定段（红线）是只读的：批核不可代签、金额不得估算这类条款只能走迁移改。
                    骨架与参数域一致：一个分组标题 + 每段一张圆角卡片。 */}
                <section className="erp-settings-split-section" data-settings-anchor="group"
                  data-group-code={PROMPT_ANCHOR} data-group-label={PROMPT_LABEL}>
                  <div className="erp-settings-split-section-head">
                    <h3 className="h5 mb-0">{PROMPT_LABEL}</h3>
                    <span className="text-secondary small font-monospace">{PROMPT_CODE}</span>
                    <span className="text-secondary small">{promptSegments.data?.length ?? 0} 段</span>
                    <span className="text-secondary small">改完下一轮对话生效</span>
                  </div>
                  {promptSegments.isLoading ? (
                    <LoadingState label="正在读取行为规则…" />
                  ) : promptSegments.isError ? (
                    <ErrorState message={describeApiError(promptSegments.error, '读取行为规则失败。')}
                      onRetry={() => void promptSegments.refetch()} />
                  ) : (
                    (promptSegments.data ?? []).map(segment => (
                      <PromptSegmentRow key={segment.key} segment={segment} />
                    ))
                  )}
                </section>

                {/* 作用域覆盖：全局值之上还有"按用户 / 按模块"两层（ADR-030 §6.2）。
                    单独成块而不是塞进各参数下面，是因为它维护的是**对象维度**（谁 / 哪个模块），
                    与"每一项参数一个值"不是同一种编辑。它自己带分组标题与圆角卡片。 */}
                <AssistantScopeOverrides />
              </div>
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
 * **没有行内"保存"**：保存是整页一个动作（工具栏）。所以这里只负责把改动报上去，
 * 并用"未保存"徽标标明这一行动过——否则一排输入框里哪些改了就看不出。
 * </para>
 *
 * <para>
 * 根节点带 `data-settings-anchor="item"`：右侧顶部那行「当前」靠它知道读到哪一项，
 * 因此它是**滚动联动的锚点**，不是装饰性属性。
 * </para>
 */
function SettingRow({ item, groupCode, draft, unsaved, onDraftChange, onReset, resetPending }: {
  item: AssistantSettingItem
  groupCode: string
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
    <section className="erp-settings-split-item"
      data-settings-anchor="item" data-group-code={groupCode}
      data-item-label={item.displayName}>
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

/**
 * 一段行为规则：正文 + 逐段保存。
 *
 * <para>
 * **锁定段是只读的**（红线：指标口径不得省略过滤、删除与批量不代劳、confirm 型动作不得直接执行）。
 * 只读不是"界面上灰一下"就算：服务端对锁定段的写入直接 409，这里的禁用只是让管理员早点知道。
 * </para>
 *
 * <para>
 * **与上面参数区的保存方式不同**：参数是"整页一个保存"，规则段是"一段一个保存"——
 * 一段话是一个整体，把它拆进整页草稿只会让"我改的这句到底存了没有"变得难判断。
 * </para>
 */
function PromptSegmentRow({ segment }: { segment: AssistantPromptSegment }) {
  const queryClient = useQueryClient()
  const toast = useToast()
  const [draft, setDraft] = useState(segment.text)

  // 重新拉取后（别人改过、或自己保存成功）以服务端文本为准
  useEffect(() => setDraft(segment.text), [segment.text])

  const save = useMutation({
    mutationFn: () => updatePromptSegment(segment.key, draft),
    onSuccess: () => {
      toast.notify({ message: `「${segment.title}」已保存，下一轮对话生效`, variant: 'success' })
      void queryClient.invalidateQueries({ queryKey: ['assistant-prompt-segments'] })
    },
    onError: (error) => toast.notify({ message: describeApiError(error, '保存失败。'), variant: 'danger' }),
  })

  const dirty = draft.trim() !== segment.text.trim()

  return (
    <section className="erp-settings-split-item" data-settings-anchor="item"
      data-item-label={segment.title}>
      <div className="erp-settings-split-section-head">
        <h4 className="h5 mb-0">{segment.title}</h4>
        <span className="text-secondary small font-monospace">{segment.key}</span>
        {segment.isLocked && <span className="badge text-bg-warning">红线 · 只读</span>}
      </div>

      <textarea className="form-control font-monospace mt-2" rows={4}
        style={{ maxWidth: 720 }}
        value={draft} aria-label={segment.title}
        disabled={segment.isLocked}
        onChange={(event) => setDraft(event.target.value)} />

      <div className="d-flex align-items-center gap-2 mt-2">
        <Button size="sm" icon={<IconDeviceFloppy size={14} />}
          loading={save.isPending}
          disabled={segment.isLocked || !dirty}
          aria-label={`保存「${segment.title}」`}
          onClick={() => save.mutate()}>保存</Button>
        {dirty && <span className="text-secondary small">未保存</span>}
        {segment.isLocked && (
          <span className="form-hint mb-0">红线条款：只能通过迁移修改，改动需评审。</span>
        )}
      </div>

      {segment.requiredTools.length > 0 && (
        <div className="form-hint">
          依赖工具：<span className="font-monospace">{segment.requiredTools.join('、')}</span>
          ——这些工具被关掉或被页面裁掉时，本段不注入。
        </div>
      )}
    </section>
  )
}
