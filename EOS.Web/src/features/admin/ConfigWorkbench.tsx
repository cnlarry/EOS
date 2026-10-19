import { useMemo, useState, type ReactNode } from 'react'
import { IconRefresh } from '@tabler/icons-react'
import { ErrorState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpSelectionList } from '../../components/common/ErpSelectionList'
import { Button } from '../../components/ui/Button'

/**
 * 模块 230104「行为动作」的两栏外壳（左栏模块清单 + 右栏配置）。
 *
 * 为什么是两栏而不是"选择模块"弹窗：这个面配的是"**别的**模块"，模块清单本该像侧栏一样常驻。
 * 弹窗选对象既多一次导航，又把"系统里配了什么"藏起来——打开页面的第一眼必须能回答这个问题。
 *
 * 口径：
 * - 左栏用**一个下拉**筛"配了什么"（有配置 / 有动作 / 有规则 / 全部），默认「全部」：
 *   四个并排按钮占掉一行宽度、把清单往下挤，下拉只占一行中的一格；
 * - 每行**分列两个计数**（动作 / 规则）：一个总数看不出"这个模块是配了动作还是只配了规则"；
 * - 排序恒按模块号：按"已配条数"排会让刚保存的模块跳到列表顶部，鼠标下的行换人；
 * - **单击即切换**（没有"编辑态"，也不需要双击）；未保存的模块带一个点；
 * - 右栏顶部恒显"模块 + 主/副表 + 我能不能改"，编辑由各面自己的右栏内容承担。
 */

/** 左栏一行：可选目标模块 + 它当前的动作 / 规则条数（0 = 还没配）。 */
export type WorkbenchModule = {
  moduleId: number
  moduleDesc: string
  masterTable: string
  masterTableDesc: string
  detailTable: string
  detailTableDesc: string
  actionCount: number
  ruleCount: number
}

/** 左栏筛选片：按"配了什么"筛，而不是按"有没有配过"一刀切。 */
type RailFilter = 'configured' | 'actions' | 'rules' | 'all'

const FILTERS: { key: RailFilter; label: string; hint: string }[] = [
  { key: 'configured', label: '有配置', hint: '配过动作或规则的模块' },
  { key: 'actions', label: '有动作', hint: '配过行为动作的模块' },
  { key: 'rules', label: '有规则', hint: '配过校验规则的模块' },
  { key: 'all', label: '全部', hint: '全部可配置的模块（含未配过的）' },
]

function matchesFilter(module: WorkbenchModule, filter: RailFilter): boolean {
  switch (filter) {
    case 'actions': return module.actionCount > 0
    case 'rules': return module.ruleCount > 0
    case 'all': return true
    default: return module.actionCount + module.ruleCount > 0
  }
}

export function ConfigWorkbench({
  modules,
  loading,
  error,
  onReload,
  selectedId,
  onSelect,
  dirtyId = null,
  toolbar,
  right,
  keyword,
  onKeywordChange,
}: {
  modules: WorkbenchModule[]
  loading: boolean
  error: unknown
  onReload: () => void
  selectedId: number | null
  onSelect: (moduleId: number) => void
  /** 有未保存改动的模块（左栏标出来，切模块时页面据此拦一道）。 */
  dirtyId?: number | null
  /** 顶部工具条里属于该面的按钮（保存等）。 */
  toolbar?: ReactNode
  /** 右栏：选中模块的配置内容，或未选中时的全库总览。 */
  right: ReactNode
  /** 搜索关键字（提升到页面：左栏与总览共用同一份过滤条件）。 */
  keyword: string
  onKeywordChange: (value: string) => void
}) {
  const [filter, setFilter] = useState<RailFilter>('all')

  const counts = useMemo(() => ({
    configured: modules.filter((item) => item.actionCount + item.ruleCount > 0).length,
    actions: modules.filter((item) => item.actionCount > 0).length,
    rules: modules.filter((item) => item.ruleCount > 0).length,
    all: modules.length,
  }), [modules])

  const listed = useMemo(() => {
    const needle = keyword.trim().toLowerCase()
    return modules
      .filter((item) => matchesFilter(item, filter))
      .filter((item) => needle === ''
        || `${item.moduleId} ${item.moduleDesc} ${item.masterTable} ${item.masterTableDesc}`
          .toLowerCase().includes(needle))
      .sort((left, rightItem) => left.moduleId - rightItem.moduleId)
  }, [modules, filter, keyword])

  // 整页在白色圆角卡片内、顶部走标准命令栏（ErpListCard）：
  // 与全站列表页同一套外壳，模块清单与配置区两栏走 card-body（几何在 app.css 一处定义）
  return (
    <div className="erp-config-workbench">
      <ErpListCard
        ariaLabel="配置面工具栏"
        search={(
          <ErpSearchBox
            value={keyword}
            onChange={onKeywordChange}
            placeholder="模块号 / 模块名 / 主表"
            ariaLabel="搜索模块"
          />
        )}
        actions={(
          <>
            <Button size="sm" variant="ghost" icon={<IconRefresh size={16} />} onClick={onReload}>刷新</Button>
            {toolbar}
          </>
        )}
      >
        <div className="card-body erp-config-workbench-body">
          <ErpSelectionList
            ariaLabel="模块清单"
            className="erp-config-rail"
            header={(
              <label className="erp-config-rail-filter">
                <span className="visually-hidden">模块筛选</span>
                <select
                  className="form-select form-select-sm"
                  value={filter}
                  title="按配置情况筛选模块"
                  onChange={(event) => setFilter(event.target.value as RailFilter)}
                >
                  {FILTERS.map((item) => (
                    <option key={item.key} value={item.key} title={item.hint}>
                      {item.label}（{counts[item.key]}）
                    </option>
                  ))}
                </select>
              </label>
            )}
            loading={loading}
            loadingLabel="正在加载模块清单…"
            error={error ? <div className="p-2"><ErrorState message="加载模块清单失败。" onRetry={onReload} /></div> : null}
            empty={(
              <div className="erp-selection-list-empty">
                {keyword.trim() !== '' ? (
                  <span>没有匹配的模块。</span>
                ) : filter === 'configured' ? (
                  <>
                    <span>系统里还没有配过行为动作或校验规则。</span>
                    <Button size="sm" variant="secondary" onClick={() => setFilter('all')}>看全部模块</Button>
                  </>
                ) : (
                  <>
                    <span>
                      {filter === 'actions' ? '没有配过行为动作的模块。' : '没有配过校验规则的模块。'}
                    </span>
                    <Button size="sm" variant="secondary" onClick={() => setFilter('all')}>看全部模块</Button>
                  </>
                )}
              </div>
            )}
            items={listed.map((item) => ({
              id: String(item.moduleId),
              label: item.moduleDesc,
              description: item.moduleId,
              tooltip: `${item.moduleDesc}（${item.moduleId}）· 主表 ${item.masterTable || '—'}`,
              meta: (
                <>
                  <span className="erp-config-rail-counts">
                    <span className={`erp-config-count${item.actionCount === 0 ? ' is-zero' : ''}`}>
                      动作 {item.actionCount}
                    </span>
                    <span className={`erp-config-count${item.ruleCount === 0 ? ' is-zero' : ''}`}>
                      规则 {item.ruleCount}
                    </span>
                  </span>
                  {dirtyId === item.moduleId ? (
                    <span className="erp-config-dirty" role="img" aria-label="有未保存的改动" />
                  ) : null}
                </>
              ),
            }))}
            activeId={selectedId == null ? null : String(selectedId)}
            onSelect={(id) => onSelect(Number(id))}
          />

          <section className="erp-config-detail" aria-label="模块配置">
            {right}
          </section>
        </div>
      </ErpListCard>
    </div>
  )
}

/**
 * 右栏顶部状态条：**这是哪个模块、它的主/副表是什么、我能不能改、现在有多少条**。
 * 四者都不该需要点开才知道——尤其是"能不能改"，只读时要说清原因，而不是把控件灰掉。
 */
export function WorkbenchPanelHeader({
  module,
  canWrite,
  counts,
  extra,
}: {
  module: WorkbenchModule
  canWrite: boolean
  /** 当前草稿条数（编辑中优先用草稿，未装载时用服务端计数）。 */
  counts?: { actions: number; rules: number }
  extra?: ReactNode
}) {
  const actions = counts?.actions ?? module.actionCount
  const rules = counts?.rules ?? module.ruleCount
  return (
    <div className="erp-config-detail-head">
      <div className="erp-config-detail-title-line">
        <span className="erp-config-detail-title">{module.moduleDesc}</span>
        <span className="erp-config-detail-id">{module.moduleId}</span>
        <span className={canWrite ? 'badge text-bg-success' : 'badge text-bg-secondary'}>
          {canWrite ? '可改' : '只读'}
        </span>
        {extra}
      </div>
      <div className="erp-config-detail-meta">
        <span>主表 {module.masterTableDesc || module.masterTable || '—'}</span>
        {module.detailTable ? <span>明细 {module.detailTableDesc || module.detailTable}</span> : <span>无明细表</span>}
        <span className="erp-config-meta-sep">·</span>
        <span>{actions} 条动作 / {rules} 条规则</span>
        {canWrite ? null : (
          <span className="text-secondary">
            （本页只给了查看权，要改请让管理员放开本配置面的 SETUP）
          </span>
        )}
      </div>
    </div>
  )
}
