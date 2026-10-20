import {
  IconAdjustmentsHorizontal,
  IconArrowsLeftRight,
  IconEdit,
  IconFolder,
  IconFolderOpen,
  IconSitemap,
  IconStack2,
} from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { useCallback, useMemo, useState } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpCommandBar, type ErpCommandItem } from '../../components/common/ErpCommandBar'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpListGrid } from '../../components/common/ErpListGrid'
import { ErpTree, type ErpTreeNode } from '../../components/common/ErpTree'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import type { ColumnDef, SortingState } from '../../lib/tanstackTable'
import { describeApiError } from '../../lib/errors'
import { apiClient } from '../../services/api'
import { FieldBrowseLink } from '../document-workbench/FieldBrowseLink'
import { formatFieldValue } from '../document-workbench/fieldFormat'
import type { FormDefinition } from '../document-workbench/formDefinition'
import type { RecordBundle } from '../document-workbench/formEditorUtils'
import {
  expandBom,
  grossRequirement,
  type BomGrossRow,
  type BomTreeRow,
  EDITION_PATTERN,
} from './bomApi'

const MODULE_ID = 1204
/** 料件基本资料的模块号：右栏的料号链到它。 */
const PRODUCT_MODULE_ID = 1201
/** 合成根的节点 id：它不对应任何明细行，用它区分"根被选中"与"没选中"。 */
const ROOT_ID = 'root'

/**
 * 产品性质 / 主要来源的取值标签，与 `FIELDS.FORM_OPTIONS` 同口径
 * （`1=1.成品;2=2.半成品;3=3.材料;4=4.辅料` / `1=1.采购;2=2.自制;3=3.外包`）。
 * 放前端常量而不是再查一次元数据：树是只读视图，这两组值稳定，多一次往返不划算。
 */
const PRO_TYPE_LABEL: Record<string, string> = { '1': '成品', '2': '半成品', '3': '材料', '4': '辅料' }
const MAIN_SOURCE_LABEL: Record<string, string> = { '1': '采购', '2': '自制', '3': '外包' }

/**
 * 产品 BOM 的结构视图（模块 1204）。
 *
 * **它是 BOM 的浏览态**：读的还是 `BOM_STRU_M/D` 同一批数据，只是把"一层"摊成"整棵树"。
 * 交互按 230101（菜单管理）的骨架——左树逐层展开，点一个节点在右侧看**这一层的配方**
 * （该父件的子件、用量、底数、损耗率、备注）。
 *
 * 树的范围是**单一产品**：根节点是这条 BOM 的产品本身（A），二级是它的直接子件。
 * 要浏览别的产品，回列表重新进——页内不提供切换，避免"树是 A 的、右栏点了 B"的错位。
 *
 * 整棵树一次取回（单次递归 CTE），折叠只是渲染层过滤：逐层请求会让层与层之间看到
 * 不同时刻的数据，而这类不一致用户无从判断。
 */
export function BomTreePage() {
  const params = useParams<{ proNo?: string; edition?: string }>()
  const navigate = useNavigate()
  // 料号与版次由**路由**决定，页内不可改：换产品回列表重新进。
  const proNo = params.proNo ?? ''
  const edition = params.edition && EDITION_PATTERN.test(params.edition) ? params.edition : ''
  const [depth, setDepth] = useState(10)
  const [mode, setMode] = useState<'tree' | 'gross'>('tree')
  const [qty, setQty] = useState(1)
  const [paramsOpen, setParamsOpen] = useState(false)
  const [compareOpen, setCompareOpen] = useState(false)
  const [compareEdition, setCompareEdition] = useState('')
  // 毛需求表的交互状态：排序、选中、活动行（与列表页同口径）
  const [grossSort, setGrossSort] = useState<SortingState>([])
  const [grossSelection, setGrossSelection] = useState<Record<string, boolean>>({})
  const [grossActive, setGrossActive] = useState<string | null>(null)
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set())
  /**
   * 选中节点写进 URL（`?node=/A1/`）：跨模块浏览点出去后，「返回」是切回本页签的
   * 那个地址——**状态在 URL 里，位置才精确**。根节点是默认值，不占参数。
   * 进页面即选中根节点：右侧立刻是这条 BOM 的资料，而不是一句"请点左侧"。
   */
  const [searchParams, setSearchParams] = useSearchParams()
  const selectedId = searchParams.get('node') ?? ROOT_ID
  const setSelectedId = useCallback((id: string) => {
    setSearchParams(current => {
      const next = new URLSearchParams(current)
      if (id === ROOT_ID) next.delete('node')
      else next.set('node', id)
      return next
    }, { replace: true })
  }, [setSearchParams])

  const tree = useQuery({
    queryKey: ['bom', 'tree', proNo, edition, depth],
    queryFn: ({ signal }) => expandBom(proNo, edition || undefined, depth, signal),
    enabled: proNo.length > 0,
    retry: false,
  })

  const gross = useQuery({
    queryKey: ['bom', 'gross', proNo, edition, depth, qty],
    queryFn: () => grossRequirement(proNo, edition || undefined, qty, depth),
    enabled: proNo.length > 0 && mode === 'gross',
    retry: false,
  })

  const toggle = useCallback((id: string) => {
    setCollapsed(current => {
      const next = new Set(current)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }, [])

  /**
   * 树的根是**产品本身**（A），不是它的第一个子件。
   *
   * `expand` 端点返回的是"根 BOM 的子件"作为第一层（LVL=1），因为毛需求与反查都不需要
   * 根这一行。树要画成自洽的层级：A → A1/A2/A3 → A1-1/A1-2，所以这里补一个合成根，
   * 它的配方就是根 BOM 的那一层明细。
   */
  const rootRow = useMemo<BomTreeRow | null>(() => {
    if (!tree.data) return null
    return {
      level: 0,
      proNo: tree.data.proNo,
      proName: tree.data.proName,
      proSpec: '',
      unitId: '',
      path: '',
      qtyPer: 1,
      qtyCum: 1,
      lostRate: 0,
      lostFactor: 1,
      // 根不是任何父件的子件，三项在语义上不存在；右栏对根走记录详情，不读这三项
      serialNo: 0,
      baseQty: 1,
      remark: '',
      // 根是产品本身，不走"子件档案"那套字段；右栏对根走记录详情
      proType: '',
      mainSource: '',
      stockQty: 0,
      stopTag: false,
      // 根是产品本身：它的"父件版次"与"自己的 BOM 版次"是同一个
      edition: tree.data.edition,
      ownEdition: tree.data.edition,
      isFallback: false,
      hasChildren: tree.data.rows.length > 0,
    }
  }, [tree.data])

  /**
   * 扁平行转嵌套。按 `path` 的父子关系挂——不靠返回顺序：
   * `path` 形如 `/A1/A1-1/`，去掉最后一段就是父节点，同一料号出现在两条路径下也不会串。
   */
  const branches = useMemo<ErpTreeNode[]>(() => {
    const rows = tree.data?.rows ?? []
    if (rows.length === 0) return []
    const byPath = new Map<string, ErpTreeNode>()
    for (const row of rows) {
      byPath.set(row.path, {
        id: row.path,
        // 料号 / 品名：料号是主标识，品名灰色弱化。用量不在树上重复——
        // 选中节点右侧的属性卡里有组成量与累计用量，树这一列留给"结构"本身
        label: nodeLabel(row.proNo, row.proName, row.ownEdition),
        title: row.proName ? `${row.proNo} ${row.proName}` : row.proNo,
        children: [],
      })
    }
    const roots: ErpTreeNode[] = []
    for (const row of rows) {
      const node = byPath.get(row.path)!
      if (row.level === 1) {
        roots.push(node)
        continue
      }
      const parentPath = row.path.replace(/[^/]+\/$/, '')
      const parent = byPath.get(parentPath)
      // 父节点缺失只可能是数据异常（父件行没被返回）；挂到根上比丢掉整枝好排查
      if (parent) parent.children!.push(node)
      else roots.push(node)
    }
    return roots
  }, [tree.data])

  const treeNodes = useMemo<ErpTreeNode[]>(() => {
    if (!rootRow) return []
    return [{
      id: ROOT_ID,
      label: nodeLabel(rootRow.proNo, rootRow.proName, rootRow.ownEdition),
      title: rootRow.proName ? `${rootRow.proNo} ${rootRow.proName}` : rootRow.proNo,
      children: branches,
    }]
  }, [branches, rootRow])

  /** 展开集合 = 所有有子件的节点 - 被折叠的（默认全展开，与 230101 一致）。 */
  const expandedIds = useMemo(() => {
    const all = new Set<string>()
    if (rootRow?.hasChildren) all.add(ROOT_ID)
    for (const row of tree.data?.rows ?? []) if (row.hasChildren) all.add(row.path)
    for (const id of collapsed) all.delete(id)
    return all
  }, [collapsed, rootRow, tree.data])

  // 根用合成 id，其余用 path；右栏据此找回那一行的料号与版次。
  // `node` 参数可能指向已不存在的节点（换了版次、料被删），此时按未选中处理。
  const selected = useMemo(() => {
    if (selectedId === ROOT_ID) return rootRow
    return (tree.data?.rows ?? []).find(row => row.path === selectedId) ?? null
  }, [rootRow, selectedId, tree.data])

  /**
   * 右栏显示的是**选中元件自己的配方属性**（它在父件 BOM 里那一行：组成量、损耗率…），
   * 不是"把它的子件再列一遍"——层级已经在左树里了，右栏再列一次只是重复。
   * 所以这里不需要再查记录：`expand` 返回的行已经带了这些值。
   */
  /**
   * 结构统计：行数 / 叶子数 / 半成品数。
   *
   * 叶子与半成品回答的是不同问题——叶子多说明**采购件多**（备料面广），
   * 半成品多说明**要做多道制令**（生产面深）。只看"多少行"看不出这两件事。
   * 半成品按料号去重：同一个半成品出现在多条路径下，那是同一份制令。
   */
  const stats = useMemo(() => {
    const rows = tree.data?.rows ?? []
    return {
      rows: rows.length,
      leaves: rows.filter(row => !row.hasChildren).length,
      semis: new Set(rows.filter(row => row.proType === '2').map(row => row.proNo)).size,
    }
  }, [tree.data])

  const childCount = useMemo(() => {
    if (!selected) return 0
    const rows = tree.data?.rows ?? []
    if (selected.path === '') return rows.filter(row => row.level === 1).length
    return rows.filter(row => row.path.replace(/[^/]+\/$/, '') === selected.path).length
  }, [selected, tree.data])

  /**
   * 根节点 == 这条 BOM 的**记录本身**，所以右栏直接读工作台的记录与表单定义，
   * 按浏览态的字段顺序与格式显示——"看这个产品的资料"和"在维护页看主表"是同一件事，
   * 不该有两套字段清单。子件不查库：它在配方里的位置（项次/底数/损耗率/备注）随树一起返回了。
   */
  /**
   * 「本件 BOM 资料」的查询键：根用它自己的版次；子件用 `ownEdition`（有下阶 BOM 才有）。
   *
   * **不能用 `selected.edition`** —— 那是父件那条 BOM 的版次，拿它查会把父件的记录当成本件的。
   * 这是树里最容易混淆的一处：同一行上有两个版次，语义完全不同。
   */
  const nodeRecordKey = useMemo<[string, string] | null>(() => {
    if (!selected) return null
    if (selected.path === '') return [selected.proNo, selected.edition]
    return selected.ownEdition ? [selected.proNo, selected.ownEdition] : null
  }, [selected])

  const formQuery = useQuery({
    queryKey: ['workbench', MODULE_ID, 'form-definition', 'view'],
    queryFn: () => apiClient.get<FormDefinition>(`/document-workbench/${MODULE_ID}/form-definition?mode=view`),
    enabled: Boolean(nodeRecordKey),
    retry: false,
  })
  const nodeRecord = useQuery({
    queryKey: ['bom', 'node-record', nodeRecordKey?.[0], nodeRecordKey?.[1]],
    queryFn: () => apiClient.get<RecordBundle>(`/document-workbench/${MODULE_ID}/record`, {
      query: { key: JSON.stringify(nodeRecordKey) },
    }),
    enabled: Boolean(nodeRecordKey),
    retry: false,
  })

  const commands = useMemo<ErpCommandItem[]>(() => [
    {
      action: 'bom-edit',
      icon: <IconEdit size={16} />,
      label: '编辑',
      variant: 'primary',
      onClick: () => navigate(`/bom/product/${encodeURIComponent(proNo)}/${encodeURIComponent(edition)}/edit`),
    },
    { action: 'bom-compare', icon: <IconArrowsLeftRight size={16} />, label: '对比', title: '与另一个版次对比', onClick: () => { setCompareEdition(''); setCompareOpen(true) } },
    { action: 'bom-params', icon: <IconAdjustmentsHorizontal size={16} />, label: '参数', title: '展开深度 / 制令数量', onClick: () => setParamsOpen(true) },
    { action: 'refresh', label: '刷新', onClick: () => { void (mode === 'tree' ? tree : gross).refetch() } },
    { action: 'back', label: '返回列表', onClick: () => navigate('/bom/product') },
  ], [edition, gross, mode, navigate, proNo, tree])

  const active = mode === 'tree' ? tree : gross
  const hasRows = (tree.data?.rows ?? []).length > 0

  return (
    <div className="erp-full-list-page erp-bom-tree-page">
      <ErpListCard
        // 页签放左上角（`search` 槽在命令栏左侧），右侧仍是命令栏——与统一工作台的
        // "左筛选、右动作"同一行布局。用页签而不是按钮：两者是同一份数据的两个视图，
        // 不是两个动作。
        search={
          <ul className="nav nav-tabs erp-bom-mode-tabs" role="tablist" aria-label="视图切换">
            {([
              { key: 'tree' as const, label: '结构树', icon: <IconSitemap size={14} /> },
              { key: 'gross' as const, label: '毛需求', icon: <IconStack2 size={14} /> },
            ]).map(tab => (
              <li key={tab.key} className="nav-item" role="presentation">
                <button
                  type="button"
                  role="tab"
                  className={`nav-link${mode === tab.key ? ' active' : ''}`}
                  aria-selected={mode === tab.key}
                  onClick={() => setMode(tab.key)}
                >
                  <span className="d-inline-flex align-items-center gap-1">{tab.icon}{tab.label}</span>
                </button>
              </li>
            ))}
          </ul>
        }
        actions={<ErpCommandBar items={commands} ariaLabel="结构视图操作" />}
      >
        {!proNo ? (
          <EmptyState description="地址缺少料号，请从列表进入。" />
        ) : active.isPending ? (
          <LoadingState />
        ) : active.isError ? (
          <ErrorState message={describeApiError(active.error, '操作失败。')} onRetry={() => { void active.refetch() }} />
        ) : mode === 'gross' ? (
          (gross.data?.rows ?? []).length === 0 ? (
            <EmptyState description="该料号在这一版下没有子件（或该版次不存在）。" />
          ) : (
            <ErpListGrid
              plain
              listId="bom-gross"
              columns={grossColumns(qty)}
              rows={gross.data?.rows}
              // 毛需求的行身份是料号（服务端已按料号合并，不会重复）
              rowKey={row => row.proNo}
              // 服务端不排序，客户端排
              sorting={{ mode: 'client', value: grossSort, onChange: setGrossSort }}
              selection={{ value: grossSelection, onChange: setGrossSelection }}
              activeRowId={grossActive ?? undefined}
              onRowClick={row => { setGrossSelection({ [row.proNo]: true }); setGrossActive(row.proNo) }}
            />
          )
        ) : !hasRows ? (
          <EmptyState description="该料号在这一版下没有子件（或该版次不存在）。" />
        ) : (
          <div className="erp-bom-tree-layout">
            <div className="erp-bom-tree">
              <div className="erp-bom-tree-head">
                {/* 版次就地切换：它是同一条 BOM 的切面，不是"另一个产品"——换产品回列表。
                    切版次时**不保留 `?node=`**：新版未必有同一路径，带着只会落空。 */}
                <select
                  className="form-select erp-bom-edition-select"
                  aria-label="切换版次"
                  value={edition || tree.data?.edition || ''}
                  onChange={event => navigate(
                    `/bom/product/${encodeURIComponent(proNo)}/${encodeURIComponent(event.target.value)}/tree`,
                  )}
                >
                  {(tree.data?.editions ?? []).map(item => (
                    <option key={item} value={item}>
                      {item}
                      {item === tree.data?.latestEdition ? '（最新）' : ''}
                    </option>
                  ))}
                </select>
                <span>· 深度 {depth}</span>
                <span>· {stats.rows} 行</span>
                <span>· {stats.leaves} 叶子</span>
                {stats.semis > 0 ? <span>· {stats.semis} 半成品</span> : null}
              </div>
              <div className="erp-bom-tree-scroll">
                <ErpTree
                  nodes={treeNodes}
                  expandedIds={expandedIds}
                  selectedId={selectedId}
                  // 只切换、不取消：树永远有一个选中项，右侧不会因为"又点了一下"
                  // 退回空态——那是把用户已经找到的位置弄丢
                  onSelect={setSelectedId}
                  onToggle={toggle}
                  rootIcon={expanded => (expanded
                    ? <IconFolderOpen size={18} stroke={1.7} />
                    : <IconFolder size={18} stroke={1.7} />)}
                  ariaLabel="BOM 结构树"
                />
              </div>
            </div>

            <div className="erp-bom-tree-detail">
              {!selected ? (
                <EmptyState description="点左侧任一料号，在这里看它的配方属性（组成量、损耗率…）。" />
              ) : (
                <>
                  <div className="erp-bom-tree-detail-head">
                    <strong>{selected.proNo}</strong>
                    <span>{selected.proName || String(nodeRecord.data?.master?.PRO_NAME ?? '')}</span>
                    <span>· 版次 {selected.edition}{selected.isFallback ? '（回退）' : ''}</span>
                    {selected.hasChildren ? <span>· {childCount} 个子件</span> : <span>· 无下阶 BOM</span>}
                  </div>
                  <div className="erp-bom-tree-detail-body">
                    {/* 根 = 这条 BOM 的记录本身：按浏览态的字段顺序与格式显示主表全部可见字段。
                        子件 = 它在配方里的那一行（项次/底数/损耗率/备注），不查库——随树一起返回。 */}
                    {/* ① 本件 BOM 资料：根是这条 BOM 本身，子件是它作为父件的那条记录。
                        两者共用同一份表单定义与渲染，字段顺序与浏览态一致——
                        "组件本身也是 BOM"，所以它该看到的东西和根没有区别。 */}
                    {nodeRecordKey ? (
                      <section className="erp-bom-section">
                        <h3 className="erp-bom-section-title">
                          本件 BOM 资料
                          {selected.path === '' ? null : `（版次 ${selected.ownEdition}）`}
                        </h3>
                        {formQuery.isPending || nodeRecord.isPending ? (
                          <LoadingState />
                        ) : nodeRecord.isError ? (
                          <ErrorState message={describeApiError(nodeRecord.error, '操作失败。')} onRetry={() => { void nodeRecord.refetch() }} />
                        ) : (
                          <dl className="erp-bom-recipe">
                            {(formQuery.data?.masterFields ?? [])
                              .filter(field => field.isVisible)
                              .map(field => (
                                <div key={field.key}>
                                  <dt>{field.label}</dt>
                                  <dd>
                                    {formatFieldValue(
                                      nodeRecord.data?.master[field.key],
                                      field.dataType,
                                      field.displayFormat,
                                    ) || '—'}
                                  </dd>
                                </div>
                              ))}
                            <div>
                              <dt>下阶</dt>
                              <dd>{selected.hasChildren ? `${childCount} 个子件` : '无（叶子）'}</dd>
                            </div>
                          </dl>
                        )}
                      </section>
                    ) : null}

                    {/* ② 在上级配方中的位置：这一行属于**父件**的 BOM，不是本件自己的记录。
                        来源版次说的是"我在哪一版父件里"，与 ① 的 ownEdition 是两回事。 */}
                    {selected.path === '' ? null : (
                      <section className="erp-bom-section">
                        <h3 className="erp-bom-section-title">在上级配方中的位置</h3>
                        <dl className="erp-bom-recipe">
                          <div>
                            <dt>项次</dt>
                            <dd>{selected.serialNo}</dd>
                          </div>
                          <div>
                            <dt>组成量</dt>
                            <dd>{formatQty(selected.qtyPer)}</dd>
                          </div>
                          <div>
                            <dt>累计用量</dt>
                            <dd>{formatQty(selected.qtyCum)}</dd>
                          </div>
                          <div>
                            <dt>损耗率</dt>
                            <dd>{formatQty(selected.lostRate)}%</dd>
                          </div>
                          <div>
                            <dt>底数</dt>
                            <dd>{formatQty(selected.baseQty)}</dd>
                          </div>
                          <div>
                            <dt>子件版次</dt>
                            <dd>{selected.ownEdition || '—（材料）'}</dd>
                          </div>
                          <div>
                            {/* 来源版次 = 父件那条 BOM 的版次，与上面的子件版次是两个字段 */}
                            <dt>来源版次</dt>
                            <dd>{selected.edition}</dd>
                          </div>
                          <div>
                            <dt>备注</dt>
                            <dd>{selected.remark || '—'}</dd>
                          </div>
                        </dl>
                      </section>
                    )}

                    {/* ③ 料件属性：来自产品档案，与 BOM 结构无关 */}
                    {selected.path === '' ? null : (
                      <section className="erp-bom-section">
                        <h3 className="erp-bom-section-title">料件属性</h3>
                        <dl className="erp-bom-recipe">
                          <div>
                            <dt>料号</dt>
                            <dd>
                              {/* 跨模块浏览：新标签打开料件基本资料，来源模块带上 1204 */}
                              <FieldBrowseLink
                                value={selected.proNo}
                                browseModuleId={PRODUCT_MODULE_ID}
                                browseKeyFields={['PRO_NO']}
                                row={{ PRO_NO: selected.proNo }}
                                fromModuleId={MODULE_ID}
                              />
                            </dd>
                          </div>
                          <div>
                            <dt>品名</dt>
                            <dd>{selected.proName || '—'}</dd>
                          </div>
                          <div>
                            <dt>规格</dt>
                            <dd>{selected.proSpec || '—'}</dd>
                          </div>
                          <div>
                            <dt>单位</dt>
                            <dd>{selected.unitId || '—'}</dd>
                          </div>
                          <div>
                            <dt>产品性质</dt>
                            <dd>{PRO_TYPE_LABEL[selected.proType] ?? '—'}</dd>
                          </div>
                          <div>
                            <dt>主要来源</dt>
                            <dd>{MAIN_SOURCE_LABEL[selected.mainSource] ?? '—'}</dd>
                          </div>
                          <div>
                            <dt>现有库存</dt>
                            <dd>{formatQty(selected.stockQty)}</dd>
                          </div>
                          <div>
                            <dt>状态</dt>
                            <dd className={selected.stopTag ? 'text-danger' : 'text-success'}>
                              {selected.stopTag ? '已停止使用' : '正常'}
                            </dd>
                          </div>
                        </dl>
                      </section>
                    )}
                  </div>
                </>
              )}
            </div>
          </div>
        )}
      </ErpListCard>

      {compareOpen ? (
        <Modal
          title="与另一个版次对比"
          size="sm"
          onClose={() => setCompareOpen(false)}
          footer={
            <>
              <Button onClick={() => setCompareOpen(false)}>取消</Button>
              <Button
                variant="primary"
                disabled={!EDITION_PATTERN.test(compareEdition)}
                onClick={() => {
                  const target = compareEdition.trim()
                  setCompareOpen(false)
                  const current = edition || tree.data?.edition || ''
                  navigate(`/bom/product/${encodeURIComponent(proNo)}/${encodeURIComponent(current)}/compare/${encodeURIComponent(target)}`)
                }}
              >
                对比
              </Button>
            </>
          }
        >
          <label className="form-label" htmlFor="bom-tree-compare-edition">目标版次</label>
          <input
            id="bom-tree-compare-edition"
            className="form-control"
            value={compareEdition}
            maxLength={2}
            placeholder="如 02"
            onChange={event => setCompareEdition(event.target.value.trim())}
          />
          <div className="form-text">按子件对齐，比用量 / 底数 / 损耗率 / 备注的原值。</div>
        </Modal>
      ) : null}

      {paramsOpen ? (
        <Modal
          title="展开参数"
          size="sm"
          onClose={() => setParamsOpen(false)}
          footer={<Button variant="primary" onClick={() => setParamsOpen(false)}>确定</Button>}
        >
          <div className="mb-3">
            <label className="form-label" htmlFor="bom-tree-depth">展开深度</label>
            <input
              id="bom-tree-depth"
              className="form-control"
              type="number"
              min={1}
              max={100}
              value={depth}
              onChange={event => setDepth(Math.min(100, Math.max(1, Number(event.target.value) || 1)))}
            />
          </div>
          <div>
            <label className="form-label" htmlFor="bom-tree-qty">制令数量（毛需求用）</label>
            <input
              id="bom-tree-qty"
              className="form-control"
              type="number"
              min={1}
              value={qty}
              onChange={event => setQty(Math.max(1, Number(event.target.value) || 1))}
            />
          </div>
        </Modal>
      ) : null}
    </div>
  )
}

/** 毛需求列：不进两栏布局（它是"算完给下游"的结果表，不是层级钻取）。 */
function grossColumns(qty: number): ColumnDef<BomGrossRow, unknown>[] {
  return [
    {
      // 与列表页同一套：选择列由调用方提供，冻结左侧、不可排序/隐藏/拖宽
      id: 'select',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'erp-select-column', frozenLeft: true, resizable: false, truncate: false },
      header: ({ table }) => (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label="选择当前页"
          checked={table.getIsAllPageRowsSelected()}
          ref={input => { if (input) input.indeterminate = table.getIsSomePageRowsSelected() }}
          onChange={table.getToggleAllPageRowsSelectedHandler()}
        />
      ),
      cell: ({ row }) => (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label={`选择 ${row.original.proNo}`}
          checked={row.getIsSelected()}
          onChange={row.getToggleSelectedHandler()}
          onClick={event => event.stopPropagation()}
        />
      ),
    },
    { id: 'proNo', header: '料号', accessorFn: row => row.proNo, meta: { minWidth: 180 } },
    { id: 'proName', header: '品名', accessorFn: row => row.proName, meta: { minWidth: 140 } },
    { id: 'proSpec', header: '规格', accessorFn: row => row.proSpec, meta: { minWidth: 140 } },
    { id: 'unitId', header: '单位', accessorFn: row => row.unitId, meta: { minWidth: 70 } },
    {
      id: 'qtyPerUnit',
      header: '单件用量',
      accessorFn: row => row.qtyPerUnit,
      meta: { className: 'text-end', minWidth: 110 },
      cell: ({ row }) => formatQty(row.original.qtyPerUnit),
    },
    {
      id: 'qtyPerUnitWithLoss',
      header: '含损耗',
      accessorFn: row => row.qtyPerUnitWithLoss,
      meta: { className: 'text-end', minWidth: 110 },
      cell: ({ row }) => formatQty(row.original.qtyPerUnitWithLoss),
    },
    {
      id: 'lostQtyPerUnit',
      header: '其中损耗',
      accessorFn: row => row.lostQtyPerUnit,
      meta: { className: 'text-end', minWidth: 110 },
      cell: ({ row }) => formatQty(row.original.lostQtyPerUnit),
    },
    {
      id: 'totalQty',
      header: `${qty} 件需求`,
      accessorFn: row => row.qtyPerUnitWithLoss * qty,
      meta: { className: 'text-end', minWidth: 120 },
      cell: ({ row }) => formatQty(row.original.qtyPerUnitWithLoss * qty),
    },
  ]
}

/**
 * 树上的节点名：`料号 / 品名 / 版次`。
 *
 * 三段各自弱化一级：料号正常色（主标识）→ 品名灰色 → 版次**再小一号**。
 * 扫视时先认料号，需要确认时再读品名与版次；整串同色会让三者互相抢注意力。
 *
 * 版次传的是**这个料号自己的 BOM 版次**（`ownEdition`），不是父件那条 BOM 的版次；
 * 叶子（材料）没有自己的 BOM，第三段不出现。
 */
function nodeLabel(proNo: string, proName: string, edition: string) {
  // 三段放在一个 inline-flex 里按**基线**对齐：字号不同（12 / 12 / 11）时，
  // 行内默认的居中会让版次浮在文字中间，看着像错位
  return (
    <span className="erp-tree-label">
      <span>{proNo}</span>
      {proName ? <span className="erp-tree-sub">/ {proName}</span> : null}
      {edition ? <span className="erp-tree-edition">/ {edition}</span> : null}
    </span>
  )
}

/** 用量最多 6 位小数，去掉无意义的尾零（0.500000 → 0.5）。 */
function formatQty(value: number): string {
  if (!Number.isFinite(value)) return ''
  return String(Number(value.toFixed(6)))
}
