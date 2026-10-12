import { useMemo, useState, type ReactNode } from 'react'
import { IconRefresh } from '@tabler/icons-react'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { Button } from '../../components/ui/Button'

/**
 * 两个配置面（230102 模块分组 / 230104 行为动作、校验规则）**共用的两栏外壳**。
 *
 * 为什么是两栏而不是"选择模块"弹窗：这两个面配的都是"**别的**模块"，模块清单本该像侧栏一样常驻。
 * 弹窗选对象既多一次导航，又把"系统里配了什么"藏起来——打开页面的第一眼必须能回答这个问题。
 * （230103 自定义按钮配的也是别的模块，但它走的是跨模块的扁平列表 + 弹窗，不用这具外壳。）
 *
 * 口径：
 * - 左栏默认**只列已配过的模块**（计数 > 0），其余折叠在「显示全部模块」后面——先看到事实，再扩到全集；
 * - **单击即切换**（没有"编辑态"，也不需要双击）；未保存的模块带「未保存」徽标；
 * - 右栏顶部恒显"模块 + 主/副表 + 我能不能改"，编辑由各面自己的右栏内容承担。
 */

/** 左栏一行：可选目标模块 + 它当前配了多少条（0 = 还没配）。 */
export type WorkbenchModule = {
  moduleId: number
  moduleDesc: string
  masterTable: string
  masterTableDesc: string
  detailTable: string
  detailTableDesc: string
  configured: number
}

export function ConfigWorkbench({
  title,
  modules,
  loading,
  error,
  onReload,
  selectedId,
  onSelect,
  dirtyId = null,
  toolbar,
  right,
  footer,
  configuredUnit = '条',
}: {
  title: string
  modules: WorkbenchModule[]
  loading: boolean
  error: unknown
  onReload: () => void
  selectedId: number | null
  onSelect: (moduleId: number) => void
  /** 有未保存改动的模块（左栏标出来，切模块时页面据此拦截）。 */
  dirtyId?: number | null
  /** 顶部工具条里属于该面的按钮（新增 / 保存并生效 等）。 */
  toolbar?: ReactNode
  /** 右栏：选中模块的配置内容。 */
  right: ReactNode
  /** 底部（一般放保存条）。 */
  footer?: ReactNode
  configuredUnit?: string
}) {
  const [keyword, setKeyword] = useState('')
  const [showAll, setShowAll] = useState(false)

  const filtered = useMemo(() => {
    const needle = keyword.trim().toLowerCase()
    const rows = needle === ''
      ? modules
      : modules.filter((item) =>
        `${item.moduleId} ${item.moduleDesc} ${item.masterTable}`.toLowerCase().includes(needle))
    // 已配过的在前，同组内按模块号：先看到"有内容的"，全量在需要时展开
    return [...rows].sort((left, rightItem) =>
      (rightItem.configured - left.configured) || (left.moduleId - rightItem.moduleId))
  }, [keyword, modules])

  const configured = filtered.filter((item) => item.configured > 0)
  const listed = showAll ? filtered : configured
  const allFilteredConfigured = filtered.length > 0 && configured.length === filtered.length

  // 整页在白色圆角卡片内、顶部走标准命令栏（ErpListCard）：
  // 与全站列表页同一套外壳，模块清单与配置区两栏走 card-body
  return (
    <div className="erp-config-workbench">
      <ErpListCard
        ariaLabel="配置面工具栏"
        search={(
          <>
            <div className="fw-semibold me-auto">{title}</div>
            <input
              className="form-control form-control-sm"
              style={{ maxWidth: '16rem' }}
              aria-label="搜索模块"
              placeholder="模块号 / 模块名 / 主表"
              value={keyword}
              onChange={(event) => setKeyword(event.target.value)}
            />
            <div className="form-check mb-0">
              <input
                className="form-check-input"
                type="checkbox"
                id="workbench-show-all"
                aria-label="显示全部模块"
                checked={showAll}
                onChange={(event) => setShowAll(event.target.checked)}
              />
              <label className="form-check-label small" htmlFor="workbench-show-all">
                显示全部模块（{filtered.length}）
              </label>
            </div>
          </>
        )}
        actions={(
          <>
            <Button size="sm" variant="ghost" icon={<IconRefresh size={16} />} onClick={onReload}>刷新</Button>
            {toolbar}
          </>
        )}
      >
        <div className="card-body">
        <div className="d-flex gap-3 align-items-start">
          <div className="border rounded flex-shrink-0" style={{ width: '20rem', maxHeight: '72vh', overflowY: 'auto' }}>
            {loading ? (
              <LoadingState label="正在加载模块清单…" />
            ) : error ? (
              <div className="p-2"><ErrorState message="加载模块清单失败。" onRetry={onReload} /></div>
            ) : listed.length === 0 ? (
              <div className="p-3 text-secondary small">
                {configured.length === 0 && !showAll
                  ? `系统里还没有配过${configuredUnit}：勾上「显示全部模块」挑一个开始。`
                  : '没有匹配的模块。'}
              </div>
            ) : (
              <ul className="list-group list-group-flush">
                {listed.map((item) => (
                  <li
                    key={item.moduleId}
                    role="button"
                    tabIndex={0}
                    className={`list-group-item list-group-item-action d-flex align-items-center gap-2 ${
                      item.moduleId === selectedId ? 'active' : ''}`}
                    onClick={() => onSelect(item.moduleId)}
                    onKeyDown={(event) => {
                      if (event.key === 'Enter' || event.key === ' ') {
                        event.preventDefault()
                        onSelect(item.moduleId)
                      }
                    }}
                  >
                    <span
                      className="flex-fill text-truncate small"
                      title={`${item.moduleDesc}（${item.moduleId}）· 主表 ${item.masterTable}`}
                    >
                      {item.moduleDesc}
                      <span className="text-secondary ms-1">{item.moduleId}</span>
                    </span>
                    {dirtyId === item.moduleId ? <span className="badge text-bg-warning">未保存</span> : null}
                    {item.configured > 0 ? (
                      <span className={`badge ${item.moduleId === selectedId ? 'text-bg-light' : 'text-bg-secondary'}`}>
                        {item.configured}
                      </span>
                    ) : null}
                  </li>
                ))}
              </ul>
            )}
            {!showAll && !allFilteredConfigured && !loading && !error ? (
              <div className="border-top px-3 py-2 text-secondary small">
                只列了配过的模块；要配别的模块，勾上「显示全部模块」。
              </div>
            ) : null}
          </div>

          <div className="flex-fill" style={{ minWidth: 0 }}>{right}</div>
        </div>
        </div>

        {footer}
      </ErpListCard>
    </div>
  )
}

/**
 * 右栏顶部状态条：**这是哪个模块、它的主/副表是什么、我能不能改**。
 * 三者都不该需要点开才知道——尤其是"能不能改"，只读时要说清原因，而不是把控件灰掉。
 */
export function WorkbenchPanelHeader({
  module,
  canWrite,
  extra,
}: {
  module: WorkbenchModule
  canWrite: boolean
  extra?: ReactNode
}) {
  return (
    <div className="d-flex flex-wrap align-items-center gap-2 small px-1">
      <span className="fw-semibold">{module.moduleDesc}</span>
      <span className="text-secondary font-monospace">{module.moduleId}</span>
      <span className="text-secondary">
        主表 {module.masterTableDesc || module.masterTable}
        {module.detailTable ? ` · 副表 ${module.detailTableDesc || module.detailTable}` : ''}
      </span>
      <span className={canWrite ? 'badge text-bg-success' : 'badge text-bg-secondary'}>
        {canWrite ? '可改' : '只读'}
      </span>
      {canWrite ? null : (
        <span className="text-secondary">（本页只给了查看权，要改请让管理员放开本配置面的 SETUP）</span>
      )}
      {extra}
    </div>
  )
}
