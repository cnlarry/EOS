import { describe, expect, it } from 'vitest'
import {
  HINT_TAB_LIMIT,
  HINT_TAB_NEAR_LIMIT,
  MAX_TABS,
  canonicalTabUrl,
  createWorkspaceState,
  fromModuleIdOf,
  isHomeUrl,
  moduleIdOfUrl,
  neighborAfterClose,
  parsePersistedTabs,
  restoreWorkspaceState,
  serializeTabs,
  tabUrlOf,
  workspaceReducer,
  type WorkspaceState,
} from './workspaceTabs'

function openAll(count: number): WorkspaceState {
  let state = createWorkspaceState('/dashboard', 't1', '首页')
  for (let i = 2; i <= count; i += 1) {
    state = workspaceReducer(state, { type: 'open', id: `t${i}`, url: `/workbench/${1600 + i}`, label: `模块${i}` })
  }
  return state
}

describe('workspaceTabs 工具', () => {
  it('标签地址是路径 + 查询串 + hash', () => {
    expect(tabUrlOf({ pathname: '/workbench/1606', search: '?page=2', hash: '#x' })).toBe('/workbench/1606?page=2#x')
  })

  it('解析地址中的工作台模块与来源模块', () => {
    expect(moduleIdOfUrl('/workbench/1606?from=1615')).toBe('1606')
    expect(moduleIdOfUrl('/dashboard')).toBeNull()
    expect(fromModuleIdOf('/workbench/1606/view/A?from=1615', '1606')).toBe('1615')
    // 来源等于目标模块本身时不生效
    expect(fromModuleIdOf('/workbench/1606/view/A?from=1606', '1606')).toBeNull()
    expect(fromModuleIdOf('/workbench/1606/view/A', '1606')).toBeNull()
  })

  it('接管标签优先右邻，其次左邻', () => {
    const state = openAll(3)
    expect(neighborAfterClose(state.tabs, 't2')?.id).toBe('t3')
    expect(neighborAfterClose(state.tabs, 't3')?.id).toBe('t2')
  })
})

describe('根路径归一为首页', () => {
  it('地址归一：根路径（含查询串/hash）归一到首页地址，其余地址原样保留', () => {
    expect(canonicalTabUrl('/')).toBe('/dashboard')
    expect(canonicalTabUrl('/?x=1')).toBe('/dashboard')
    expect(canonicalTabUrl('/#top')).toBe('/dashboard')
    expect(canonicalTabUrl('/counter')).toBe('/counter')
    expect(canonicalTabUrl('/workbench/1606?from=1615')).toBe('/workbench/1606?from=1615')
    expect(isHomeUrl('/')).toBe(true)
    expect(isHomeUrl('/dashboard')).toBe(true)
    expect(isHomeUrl('/counter')).toBe(false)
  })

  it('入口落在根路径时只开首页一个标签，不留重定向标签', () => {
    const state = createWorkspaceState('/', 't1', '', true)
    expect(state.tabs).toHaveLength(1)
    expect(state.tabs[0].url).toBe('/dashboard')
    expect(state.tabs[0].label).toBe('首页')
    expect(state.activeId).toBe('t1')
  })

  it('打开根路径时聚焦首页，而不是新建同址标签', () => {
    const state = workspaceReducer(createWorkspaceState('/counter', 't1', '计数器', true), { type: 'open', id: 't9', url: '/', label: '' })
    expect(state.tabs.map((tab) => tab.url)).toEqual(['/dashboard', '/counter'])
    expect(state.activeId).toBe('home')
  })

  it('地址回到根路径时按首页匹配标签，不改写活动标签地址', () => {
    const state: WorkspaceState = { tabs: [
      { id: 'home', url: '/dashboard', label: '首页', mounted: true },
      { id: 't1', url: '/counter', label: '计数器', mounted: true },
    ], activeId: 't1', hint: null }
    const next = workspaceReducer(state, { type: 'sync', url: '/', label: '首页' })
    expect(next.activeId).toBe('home')
    expect(next.tabs.map((tab) => tab.url)).toEqual(['/dashboard', '/counter'])
  })

  it('恢复时把历史遗留的根路径标签并进首页，不生成第二个同址标签', () => {
    const state = restoreWorkspaceState([
      { id: 't1', url: '/', label: '' },
      { id: 't2', url: '/counter', label: '计数器' },
    ], '/', () => 't9', true)
    // 根路径条目按首页接管（id 沿用），不再单独成标签；当前地址同样归一到首页
    expect(state.tabs.map((tab) => `${tab.url}|${tab.label}`)).toEqual(['/dashboard|首页', '/counter|计数器'])
    expect(state.activeId).toBe('t1')
    expect(state.tabs[0].mounted).toBe(true)
  })

  it('解析持久化数据时归一地址并按地址去重（首页优先）', () => {
    expect(parsePersistedTabs(JSON.stringify([
      { id: 'home', url: '/dashboard', label: '首页' },
      { id: 't1', url: '/', label: '' },
    ]))).toEqual([{ id: 'home', url: '/dashboard', label: '首页', fromModuleId: undefined }])
    // 只有根路径条目时补成首页标签
    expect(parsePersistedTabs(JSON.stringify([{ id: 't1', url: '/', label: '' }]))).toEqual([
      { id: 't1', url: '/dashboard', label: '首页', fromModuleId: undefined },
    ])
  })
})

describe('workspaceReducer', () => {
  it('打开新地址新建标签并激活', () => {
    const state = workspaceReducer(createWorkspaceState('/dashboard', 't1'), { type: 'open', id: 't2', url: '/workbench/1606', label: '采购订单' })
    expect(state.tabs.map((tab) => tab.url)).toEqual(['/dashboard', '/workbench/1606'])
    expect(state.activeId).toBe('t2')
  })

  it('重复地址只聚焦，不新建', () => {
    const first = workspaceReducer(createWorkspaceState('/dashboard', 't1'), { type: 'open', id: 't2', url: '/workbench/1606', label: '采购订单' })
    const second = workspaceReducer(first, { type: 'open', id: 't3', url: '/dashboard', label: '首页' })
    expect(second.tabs).toHaveLength(2)
    expect(second.activeId).toBe('t1')
  })

  it('剩 1 个名额时提示接近上限；撞顶只提示不新建（上限 12）', () => {
    const nearLimit = openAll(MAX_TABS - 1)
    expect(nearLimit.tabs).toHaveLength(MAX_TABS - 1)
    expect(nearLimit.hint).toBe(HINT_TAB_NEAR_LIMIT)

    // 开满上限本身是成功动作，不再提示（提示在"剩 1 个"时已经给过）
    const full = openAll(MAX_TABS)
    expect(full.tabs).toHaveLength(MAX_TABS)
    expect(full.hint).toBeNull()

    const rejected = workspaceReducer(full, { type: 'open', id: 't99', url: '/workbench/9999', label: '超限' })
    expect(rejected.tabs).toHaveLength(MAX_TABS)
    expect(rejected.activeId).toBe(full.activeId)
    expect(rejected.hint).toBe(HINT_TAB_LIMIT)
  })

  it('接管活动位的标签即使从未挂载过也会挂载（否则工作区只剩空白）', () => {
    const home = { id: 'home', url: '/dashboard', label: '首页', mounted: false }
    const counter = { id: 't1', url: '/counter', label: '计数器', mounted: true }
    const closed = workspaceReducer({ tabs: [home, counter], activeId: 't1', hint: null }, { type: 'close', id: 't1' })
    expect(closed.activeId).toBe('home')
    expect(closed.tabs[0].mounted).toBe(true)

    const all = workspaceReducer({ tabs: [home, counter], activeId: 't1', hint: null }, { type: 'closeAll' })
    expect(all.activeId).toBe('home')
    expect(all.tabs[0].mounted).toBe(true)

    const others = workspaceReducer({ tabs: [counter, home], activeId: 't1', hint: null }, { type: 'closeOthers', keepIds: ['home'], activeId: 'home' })
    expect(others.activeId).toBe('home')
    expect(others.tabs[0].mounted).toBe(true)
  })

  it('关闭活动标签后由右邻接管；关闭非活动标签不动活动标签', () => {
    const state = openAll(3)
    const closedActive = workspaceReducer({ ...state, activeId: 't2' }, { type: 'close', id: 't2' })
    expect(closedActive.tabs.map((tab) => tab.id)).toEqual(['t1', 't3'])
    expect(closedActive.activeId).toBe('t3')

    const closedOther = workspaceReducer({ ...state, activeId: 't1' }, { type: 'close', id: 't2' })
    expect(closedOther.activeId).toBe('t1')
  })

  it('地址落在其它标签上时激活该标签（前进/后退），不改写当前标签地址', () => {
    const state: WorkspaceState = { tabs: [
      { id: 't1', url: '/dashboard', label: '首页', mounted: true },
      { id: 't2', url: '/workbench/1606', label: '采购订单', mounted: true },
    ], activeId: 't2', hint: null }
    const next = workspaceReducer(state, { type: 'sync', url: '/dashboard', label: '首页' })
    expect(next.activeId).toBe('t1')
    expect(next.tabs.find((tab) => tab.id === 't2')?.url).toBe('/workbench/1606')
  })

  it('地址无人认领时改写活动标签地址（标签内导航）', () => {
    const state: WorkspaceState = { tabs: [
      { id: 't1', url: '/workbench/1606', label: '采购订单', mounted: true },
      { id: 't2', url: '/dashboard', label: '首页', mounted: true },
    ], activeId: 't1', hint: null }
    const next = workspaceReducer(state, { type: 'sync', url: '/workbench/1606/view/A?from=1615', label: '查看采购订单：A ← 请购单', fromModuleId: '1615' })
    expect(next.activeId).toBe('t1')
    expect(next.tabs[0].url).toBe('/workbench/1606/view/A?from=1615')
    expect(next.tabs[0].fromModuleId).toBe('1615')
    expect(next.tabs[1].url).toBe('/dashboard')
  })

  it('标题不变时保持原状态对象引用', () => {
    const state = createWorkspaceState('/dashboard', 't1', '首页')
    expect(workspaceReducer(state, { type: 'sync', url: '/dashboard', label: '首页' })).toBe(state)
  })

  it('标题解析不出来时保持原标签文字，不被空标题覆盖', () => {
    const state: WorkspaceState = { tabs: [{ id: 't1', url: '/p1', label: '采购订单', mounted: true }], activeId: 't1', hint: null }
    const next = workspaceReducer(state, { type: 'sync', url: '/p1', label: '' })
    expect(next.tabs[0].label).toBe('采购订单')
  })

  it('模块域路由（报表/明细查询/打印等）也能解析出模块 ID', () => {
    expect(moduleIdOfUrl('/reports/1606')).toBe('1606')
    expect(moduleIdOfUrl('/detail-query/1606?x=1')).toBe('1606')
    expect(moduleIdOfUrl('/print/1606')).toBe('1606')
    expect(moduleIdOfUrl('/layout-designer/1606')).toBe('1606')
    expect(moduleIdOfUrl('/fallback/modules/1606')).toBe('1606')
    expect(moduleIdOfUrl('/dashboard')).toBeNull()
  })

  it('隐藏标签的导航只改自身地址', () => {
    const state: WorkspaceState = { tabs: [
      { id: 't1', url: '/dashboard', label: '首页', mounted: true },
      { id: 't2', url: '/workbench/1606', label: '采购订单', mounted: true },
    ], activeId: 't1', hint: null }
    const next = workspaceReducer(state, { type: 'tabUrl', id: 't2', url: '/workbench/1606/view/A' })
    expect(next.activeId).toBe('t1')
    expect(next.tabs[1].url).toBe('/workbench/1606/view/A')
  })

  it('隐藏标签的导航落到别的标签已开地址上时不写入（否则会出现两个同址标签）', () => {
    const state: WorkspaceState = { tabs: [
      { id: 'home', url: '/dashboard', label: '首页', mounted: true },
      { id: 't2', url: '/workbench/1605', label: '请购单', mounted: true },
    ], activeId: 'home', hint: null }
    expect(workspaceReducer(state, { type: 'tabUrl', id: 't2', url: '/dashboard' })).toBe(state)
    // 归一到首页地址同样受这条守卫约束（根路径不能把标签改成与首页同址）
    expect(workspaceReducer(state, { type: 'tabUrl', id: 't2', url: '/' })).toBe(state)
    // 未被占用的地址照常写入
    expect(workspaceReducer(state, { type: 'tabUrl', id: 't2', url: '/workbench/1606' })?.tabs[1].url).toBe('/workbench/1606')
  })

  it('激活标签时才标记已挂载（懒挂载）', () => {
    const state: WorkspaceState = { tabs: [
      { id: 't1', url: '/dashboard', label: '首页', mounted: true },
      { id: 't2', url: '/workbench/1606', label: '采购订单', mounted: false },
    ], activeId: 't1', hint: null }
    const activated = workspaceReducer(state, { type: 'activate', id: 't2' })
    expect(activated.activeId).toBe('t2')
    expect(activated.tabs[1].mounted).toBe(true)
    expect(activated.tabs[0].mounted).toBe(true)
  })
})

describe('首页标签常驻', () => {
  const home = { id: 'home', url: '/dashboard', label: '首页', mounted: true }
  const counter = { id: 't1', url: '/counter', label: '计数器', mounted: true }
  const profile = { id: 't2', url: '/settings/profile', label: '个人设置', mounted: true }

  it('初始状态把首页放在首位（当前地址不是首页时）', () => {
    const state = createWorkspaceState('/counter', 't1', '计数器', true)
    expect(state.tabs.map((tab) => tab.url)).toEqual(['/dashboard', '/counter'])
    expect(state.activeId).toBe('t1')
  })

  it('恢复列表里没有首页时补上并置首', () => {
    const state = restoreWorkspaceState([
      { id: 't1', url: '/counter', label: '计数器' },
      { id: 't2', url: '/settings/profile', label: '个人设置' },
    ], '/counter', () => 't3', true)
    expect(state.tabs[0].url).toBe('/dashboard')
    expect(state.tabs.map((tab) => tab.url)).toEqual(['/dashboard', '/counter', '/settings/profile'])
  })

  it('恢复列表里首页不在首位时移到首位', () => {
    const state = restoreWorkspaceState([
      { id: 't1', url: '/counter', label: '计数器' },
      { id: 'home', url: '/dashboard', label: '首页' },
    ], '/counter', () => 't3', true)
    expect(state.tabs.map((tab) => tab.url)).toEqual(['/dashboard', '/counter'])
  })

  it('关闭首页是空操作', () => {
    const state: WorkspaceState = { tabs: [home, counter], activeId: 'home', hint: null }
    expect(workspaceReducer(state, { type: 'close', id: 'home' })).toBe(state)
  })

  it('「关闭其它」始终保留首页', () => {
    const state: WorkspaceState = { tabs: [home, counter, profile], activeId: 't2', hint: null }
    const next = workspaceReducer(state, { type: 'closeOthers', keepIds: ['t2'], activeId: 't2' })
    expect(next.tabs.map((tab) => tab.url)).toEqual(['/dashboard', '/settings/profile'])
    expect(next.activeId).toBe('t2')
  })

  it('「关闭全部」只保留首页', () => {
    const state: WorkspaceState = { tabs: [home, counter, profile], activeId: 't2', hint: null }
    const next = workspaceReducer(state, { type: 'closeAll' })
    expect(next.tabs).toEqual([home])
    expect(next.activeId).toBe('home')
  })

  it('只剩首页时「关闭全部」保持原状态', () => {
    const state: WorkspaceState = { tabs: [home], activeId: 'home', hint: null }
    expect(workspaceReducer(state, { type: 'closeAll' })).toBe(state)
  })
})

describe('workspaceTabs 持久化', () => {  it('序列化丢掉组件状态，只留地址与标题', () => {
    const state = createWorkspaceState('/dashboard', 't1', '首页')
    expect(serializeTabs(state.tabs)).toEqual([{ id: 't1', url: '/dashboard', label: '首页', fromModuleId: undefined }])
  })

  it('解析容忍脏数据并按上限截断', () => {
    expect(parsePersistedTabs(null)).toEqual([])
    expect(parsePersistedTabs('{oops')).toEqual([])
    expect(parsePersistedTabs('{"a":1}')).toEqual([])
    expect(parsePersistedTabs(JSON.stringify([
      { id: 't1', url: '/dashboard', label: '首页' },
      { id: '', url: '/x' },
      { id: 't2', url: 'relative' },
      { id: 't3', url: '/dashboard', label: '重复地址' },
      { id: 't4', url: '/counter', label: 42 },
      null,
    ]))).toEqual([
      { id: 't1', url: '/dashboard', label: '首页', fromModuleId: undefined },
      { id: 't4', url: '/counter', label: '', fromModuleId: undefined },
    ])
  })

  it('恢复时只有当前地址所在标签挂载，其余保持未挂载', () => {
    const saved = [
      { id: 't1', url: '/dashboard', label: '首页' },
      { id: 't2', url: '/counter', label: '计数器' },
    ]
    const state = restoreWorkspaceState(saved, '/counter', () => 't9')
    expect(state.tabs.map((tab) => tab.mounted)).toEqual([false, true])
    expect(state.activeId).toBe('t2')
  })

  it('当前地址不在恢复列表时补一个标签并激活', () => {
    const saved = [{ id: 't1', url: '/dashboard', label: '首页' }]
    const state = restoreWorkspaceState(saved, '/workbench/1606', () => 't9')
    expect(state.tabs).toHaveLength(2)
    expect(state.activeId).toBe('t9')
    expect(state.tabs[1]).toMatchObject({ url: '/workbench/1606', mounted: true })
  })

  it('恢复列表已满时挤掉末位，保证当前地址一定有标签', () => {
    const saved = Array.from({ length: MAX_TABS }, (_, index) => ({ id: `t${index + 1}`, url: `/p${index + 1}`, label: `页${index + 1}` }))
    const state = restoreWorkspaceState(saved, '/current', () => 't99')
    expect(state.tabs).toHaveLength(MAX_TABS)
    expect(state.tabs.at(-1)).toMatchObject({ url: '/current', mounted: true })
    expect(state.tabs.some((tab) => tab.url === `/p${MAX_TABS}`)).toBe(false)
  })
})
