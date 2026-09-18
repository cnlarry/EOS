import { describe, expect, it } from 'vitest'
import {
  HINT_TAB_AT_LIMIT,
  HINT_TAB_LIMIT,
  MAX_TABS,
  createWorkspaceState,
  fromModuleIdOf,
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

  it('撞顶只提示不新建（上限 12）', () => {
    const full = openAll(MAX_TABS)
    expect(full.tabs).toHaveLength(MAX_TABS)
    expect(full.hint).toBe(HINT_TAB_AT_LIMIT)
    const rejected = workspaceReducer(full, { type: 'open', id: 't99', url: '/workbench/9999', label: '超限' })
    expect(rejected.tabs).toHaveLength(MAX_TABS)
    expect(rejected.activeId).toBe(full.activeId)
    expect(rejected.hint).toBe(HINT_TAB_LIMIT)
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

  it('隐藏标签的导航只改自身地址', () => {
    const state: WorkspaceState = { tabs: [
      { id: 't1', url: '/dashboard', label: '首页', mounted: true },
      { id: 't2', url: '/workbench/1606', label: '采购订单', mounted: true },
    ], activeId: 't1', hint: null }
    const next = workspaceReducer(state, { type: 'tabUrl', id: 't2', url: '/workbench/1606/view/A' })
    expect(next.activeId).toBe('t1')
    expect(next.tabs[1].url).toBe('/workbench/1606/view/A')
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

describe('workspaceTabs 持久化', () => {
  it('序列化丢掉组件状态，只留地址与标题', () => {
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
