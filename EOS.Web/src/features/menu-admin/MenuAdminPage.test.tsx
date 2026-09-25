import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MenuAdminPage, type MenuAdminModule } from './MenuAdminPage'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const moduleNode = (id: number, desc: string, parent: number | null): MenuAdminModule => ({
  M_IDX: id, M_ALIAS: null, M_DESC: desc, M_URL: null, NEW_URL: null, MODI_URL: null, HELP_URL: null,
  DETAIL_NO_FIELDS: null, DETAIL_NO_SAVE: false, SEARCH_1: false, SEARCH_2: false, M_P_IDX: parent,
  SORT_IDX: 0, M_TAG: true, AUTO_APPROVE: false, IF_COPY: false, ERROR_NO_SAVE: false, SORT_FIELDS: null,
  MASTER_TABLE: null, FILTER: null, DETAIL_TABLE: null,
  NOT_BACK_FIELDS_M: null, NOT_BACK_FIELDS: null,
  GROUP1: false, GROUP_EXP1: null, GROUP_DESC1: null,
  GROUP2: false, GROUP_EXP2: null, GROUP_DESC2: null,
  GROUP3: false, GROUP_EXP3: null, GROUP_DESC3: null,
  GROUP4: false, GROUP_EXP4: null, GROUP_DESC4: null,
  GROUP5: false, GROUP_EXP5: null, GROUP_DESC5: null,
  FORM_TABS: null, FORM_COLUMNS: null, FORM_BUTTONS: null,
  LAST_UPDATE_BY: null, LAST_UPDATE_DATE: null,
  M_ICON: null,
  Icon: null,
  EFFECT_ENGINE_TAG: false,
})

const modules = [moduleNode(11, '基本参数', null), moduleNode(1101, '系统参数', 11), moduleNode(110101, '公司基本资料', 1101)]
const twoRoots = [moduleNode(11, '基本参数', null), moduleNode(12, '产品管理', null), moduleNode(1101, '系统参数', 11)]
const twoLeafRoots = [moduleNode(11, '基本参数', null), moduleNode(12, '产品管理', null)]
const nestedModules = [
  moduleNode(11, '基本参数', null),
  moduleNode(12, '产品管理', null),
  moduleNode(1101, '系统参数', 11),
  moduleNode(110101, '公司基本资料', 1101),
]
const threeLeaves = [
  moduleNode(11, '基本参数', null),
  moduleNode(1101, '系统参数', 11),
  moduleNode(1102, '产品参数', 11),
  moduleNode(1103, '报表查询', 11),
]
const withTables: MenuAdminModule = { ...moduleNode(110101, '公司基本资料', 1101), MASTER_TABLE: 'COMPANY', DETAIL_TABLE: 'COMPANY_D' }

/** 行为配置的接口数据：一条效果动作 + 目录 + Schema（页签级用例共用）。 */
const businessConfig = {
  moduleId: 110101,
  actions: [
    {
      seq: 1,
      eventCode: 'APPROVE_EFFECT',
      effectKey: 'field-accumulate',
      effectName: '收料量回写采购单',
      enabled: true,
      failMode: 'BLOCK',
      ops: [],
    },
  ],
  validationRules: [],
}

const businessCatalog = {
  events: ['SAVE', 'APPROVE_EFFECT', 'MANUAL'],
  failModes: ['BLOCK', 'WARN'],
  effectKeys: ['field-accumulate'],
  opCodes: ['ACCUM'],
  sourceScopes: ['MASTER', 'DETAIL'],
  sourceAggregates: ['SUM'],
  validationStages: ['SAVE'],
  validationKeys: ['qty-not-exceed'],
  labels: {
    events: { SAVE: '保存后', APPROVE_EFFECT: '批核生效', MANUAL: '用户点击（自定义按钮）' },
    failModes: { BLOCK: '失败整链回滚' },
    effectKeys: { 'field-accumulate': '量额/日期累加回写' },
    opCodes: { ACCUM: '累加' },
    sourceScopes: { MASTER: '本单主表', DETAIL: '本单明细' },
    validationStages: { SAVE: '保存前' },
    validationKeys: { 'qty-not-exceed': '不超量' },
  },
  documentActions: [{ key: 'recalc-account', label: '重算账面数量', placement: 'detail' }],
}

/** 含表模块 + 行为配置的统一桩：extra 可先截获特定路径，capabilities 可换成本用例需要的能力。 */
function mockPageWithBusinessConfig(
  extra?: (path: string, config?: unknown) => unknown,
  capabilities: unknown = PAGE_CAPABILITIES,
) {
  mockPageGet(async (path: string, config?: unknown) => {
    const handled = extra?.(path, config)
    if (handled !== undefined) return handled
    if (path === '/admin/module-business-config/meta') return businessCatalog
    if (path === '/admin/module-business-config/110101') return businessConfig
    if (path === '/admin/module-business-config/110101/field-labels') return {}
    if (path === '/admin/module-business-config/schemas') {
      return { effects: [], reverseKinds: ['no-reverse'], reverseKindLabels: {}, validationParams: [] }
    }
    if (path.endsWith('/relations')) return []
    if (path === '/settings/system') return { groups: [] }
    return { total: 3, modules: [moduleNode(11, '基本参数', null), moduleNode(1101, '系统参数', 11), withTables] }
  }, capabilities)
}

/** 渲染页面并进入含表模块（默认打开「行为动作」页签）。 */
async function openModuleTab(tab = '行为动作') {
  renderPage()
  await waitForMenuTree()
  fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
  fireEvent.click(screen.getByRole('button', { name: /系统参数/ }))
  fireEvent.click(screen.getByRole('button', { name: /公司基本资料/ }))
  fireEvent.click(screen.getByRole('tab', { name: tab }))
}

/**
 * 页面首屏会读一次 2301 能力（决定是否渲染配置页签）。
 * 统一包一层：能力查询固定返回全权，其余请求交给用例自己的实现，
 * 这样各用例不必为它单独分支。
 */
const PAGE_CAPABILITIES = { canBrowse: true, canSetup: true, canModuleConfig: true }

function mockPageGet(handler: (path: string, config?: unknown) => unknown, capabilities: unknown = PAGE_CAPABILITIES) {
  apiClientMock.get.mockImplementation(async (path: string, config?: unknown) =>
    path === '/admin/menus/capabilities' ? capabilities : handler(path, config))
}

/** 同上，但非能力请求一律返回同一个值。 */
function mockPageGetValue(value: unknown) {
  mockPageGet(async () => value)
}

const tableChooserData = {
  columns: [
    { key: 'T_ID', label: '表名', dataType: 'nvarchar', format: null },
    { key: 'T_DESC', label: '描述', dataType: 'nvarchar', format: null },
    { key: 'T_KIND', label: '类型', dataType: 'nvarchar', format: null },
    { key: 'T_TYPE', label: '种类', dataType: 'nvarchar', format: null },
  ],
  rows: [{ T_ID: 'COMPANY', T_DESC: '公司基本资料', T_KIND: '', T_TYPE: '' }],
  total: 1,
}

function fieldChooserData(fieldRows: { F_ID: string; F_DESC: string; F_TYPE: string }[]) {
  return {
    columns: [
      { key: 'F_ID', label: '字段名', dataType: 'nvarchar', format: null },
      { key: 'F_DESC', label: '描述', dataType: 'nvarchar', format: null },
      { key: 'F_TYPE', label: '类型', dataType: 'nvarchar', format: null },
    ],
    rows: fieldRows.map(({ F_ID, F_DESC, F_TYPE }) => ({ F_ID, F_DESC, F_TYPE })),
    total: fieldRows.length,
  }
}

function renderPage() {
  return renderWithProviders(
      <MenuAdminPage />
)
}

/** 等待菜单树初始渲染（全量并发测试下放宽超时，避免首帧超时抖动）。 */
async function waitForMenuTree() {
  await waitFor(() => expect(screen.getByRole('button', { name: /基本参数/ })).toBeInTheDocument(), { timeout: 5000 })
}

describe('MenuAdminPage', () => {
  beforeEach(() => {
    mockPageGetValue({ total: modules.length, modules })
    apiClientMock.post.mockResolvedValue({ id: 999 })
    apiClientMock.put.mockResolvedValue(undefined)
    apiClientMock.delete.mockResolvedValue(undefined)
    vi.spyOn(window, 'alert').mockImplementation(() => {})
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('渲染菜单树并选中节点填充表单', async () => {
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    await waitFor(() => expect(screen.getByLabelText('菜单名称')).toHaveValue('基本参数'))
    expect(screen.getByText('已选择：基本参数（ID：11）')).toBeInTheDocument()
  })

  it('保存已有节点调用 PUT', async () => {
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    await waitFor(() => expect(screen.getByLabelText('菜单名称')).toHaveValue('基本参数'))
    fireEvent.change(screen.getByLabelText('菜单名称'), { target: { value: '基本参数-改' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith('/admin/menus/11',
      expect.objectContaining({ module: expect.objectContaining({ M_DESC: '基本参数-改' }) })))
  })

  it('保存已有节点后仍停留在该节点，表单不被清空', async () => {
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    await waitFor(() => expect(screen.getByLabelText('菜单名称')).toHaveValue('基本参数'))
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    // onSuccess 以「保存成功」提示收尾，此时草稿回填已完成
    await waitFor(() => expect(window.alert).toHaveBeenCalledWith('菜单保存成功。'))
    expect(screen.getByText('已选择：基本参数（ID：11）')).toBeInTheDocument()
    expect(screen.getByLabelText('菜单名称')).toHaveValue('基本参数')
    expect(screen.queryByText(/请在左侧选择菜单节点/)).not.toBeInTheDocument()
  })

  it('按服务端状态显示模块发布徽标并在改动后转为未保存', async () => {
    mockPageGetValue({
      total: 1,
      modules: [{
        ...moduleNode(11, '基本参数', null),
        DIRTY_TAG: true,
        PUBLISH_VERSION: 3,
        PUBLISHED_AT: '2026-09-11T10:00:00',
      }],
    })
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    await waitFor(() => expect(screen.getByLabelText('菜单名称')).toHaveValue('基本参数'))
    expect(screen.getByText('已保存未发布')).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('菜单名称'), { target: { value: '基本参数-改' } })
    expect(screen.getByText('已修改未保存')).toBeInTheDocument()
  })

  it('未改动且未置脏时显示当前发布版本', async () => {
    mockPageGetValue({
      total: 1,
      modules: [{ ...moduleNode(11, '基本参数', null), DIRTY_TAG: false, PUBLISH_VERSION: 7 }],
    })
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    await waitFor(() => expect(screen.getByLabelText('菜单名称')).toHaveValue('基本参数'))
    expect(screen.getByText('已发布 module-11-v7')).toBeInTheDocument()
  })

  it('发布返回"未写入"但无失败项时提示快照已是最新', async () => {
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    await waitFor(() => expect(screen.getByLabelText('菜单名称')).toHaveValue('基本参数'))
    apiClientMock.put.mockResolvedValue(11)
    apiClientMock.post.mockResolvedValue([
      {
        moduleId: 11, title: '基本参数', published: false, version: 5,
        definitionVersion: 'module-11-v5', passed: true, checks: [], error: null,
      },
    ])
    fireEvent.click(screen.getByRole('button', { name: '发布' }))

    expect(await screen.findByText(/快照已是最新，无需重发布/)).toBeInTheDocument()
    expect(screen.queryByText(/发布未通过校验/)).not.toBeInTheDocument()
  })

  it('发布返回未通过项时逐条列出校验失败原因', async () => {
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    await waitFor(() => expect(screen.getByLabelText('菜单名称')).toHaveValue('基本参数'))
    apiClientMock.put.mockResolvedValue(11)
    apiClientMock.post.mockResolvedValue([
      {
        moduleId: 11, title: '基本参数', published: false, version: 4,
        definitionVersion: 'module-11-v4', passed: false,
        checks: [{ code: 'master_pk_exists', passed: false, message: '主表缺少主键。', severity: 'error' }],
        error: null,
      },
    ])
    fireEvent.click(screen.getByRole('button', { name: '发布' }))

    expect(await screen.findByText(/发布未通过校验/)).toBeInTheDocument()
    expect(screen.getByText('主表缺少主键。')).toBeInTheDocument()
  })

  it('版本历史弹窗展示该模块的历史发布版本', async () => {
    mockPageGet(async (path: string) => {
      if (path.endsWith('/versions')) {
        return [
          { version: 2, definitionVersion: 'module-11-v2', publishedBy: 'SYSTEM', publishedAt: '2026-09-11T09:00:00', validationStatus: 'PASS', isCurrent: true },
          { version: 1, definitionVersion: 'module-11-v1', publishedBy: 'SYSTEM', publishedAt: '2026-09-10T09:00:00', validationStatus: 'PASS', isCurrent: false },
        ]
      }
      return { total: 1, modules: [moduleNode(11, '基本参数', null)] }
    })
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    await waitFor(() => expect(screen.getByRole('button', { name: '版本历史' })).toBeEnabled())
    fireEvent.click(screen.getByRole('button', { name: '版本历史' }))

    expect(await screen.findByText('module-11-v2')).toBeInTheDocument()
    expect(screen.getByText('module-11-v1')).toBeInTheDocument()
    expect(screen.getByText('是')).toBeInTheDocument()
  })

  it('新增根节点调用 POST', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByRole('button', { name: '新增根节点' })).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '新增根节点' }))
    await waitFor(() => expect(screen.getByLabelText('菜单名称')).toHaveValue(''))
    fireEvent.change(screen.getByLabelText('菜单名称'), { target: { value: '新菜单' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith('/admin/menus',
      expect.objectContaining({ module: expect.objectContaining({ M_IDX: 0, M_DESC: '新菜单' }) })))
  })

  it('新增根节点保存后选中服务端返回的新编号', async () => {
    let posted = false
    mockPageGet(async () => {
      if (!posted) return { total: 1, modules: [moduleNode(11, '基本参数', null)] }
      return { total: 2, modules: [moduleNode(11, '基本参数', null), moduleNode(999, '新菜单', null)] }
    })
    apiClientMock.post.mockImplementation(async (path: string) => {
      if (path === '/admin/menus') {
        posted = true
        return { id: 999 }
      }
      throw new Error(`unexpected POST ${path}`)
    })
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: '新增根节点' }))
    await waitFor(() => expect(screen.getByLabelText('菜单名称')).toHaveValue(''))
    fireEvent.change(screen.getByLabelText('菜单名称'), { target: { value: '新菜单' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(screen.getByText('已选择：新菜单（ID：999）')).toBeInTheDocument())
  })

  it('默认查询列弹窗加载并保存主表默认列', async () => {
    mockPageGet(async (path: string) => {
      if (path.includes('/default-columns')) {
        return {
          table: 'COMPANY', tableKind: 'master',
          fields: [
            { key: 'C_ID', label: '公司编号', isSelected: true, order: 1 },
            { key: 'C_NAME', label: '公司名称', isSelected: false, order: 2 },
            { key: 'ADDR', label: '地址', isSelected: true, order: 3 },
          ],
        }
      }
      return { total: modules.length, modules: [moduleNode(11, '基本参数', null), moduleNode(1101, '系统参数', 11), withTables] }
    })
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    fireEvent.click(screen.getByRole('button', { name: /系统参数/ }))
    fireEvent.click(screen.getByRole('button', { name: /公司基本资料/ }))
    fireEvent.click(screen.getByRole('tab', { name: '主表' }))
    await waitFor(() => expect(screen.getByRole('button', { name: '默认列' })).toBeEnabled())
    fireEvent.click(screen.getByRole('button', { name: '默认列' }))
    await waitFor(() => expect(screen.getByText('主表默认查询列')).toBeInTheDocument())
    // 等弹窗把默认勾选字段同步进“已选字段”后再保存，避免时序抖动导致空选择提交
    await waitFor(() => {
      const selectedBox = screen.getByRole('listbox', { name: '主表字段已选字段' })
      expect(within(selectedBox).getAllByRole('option').map((option) => option.getAttribute('value')))
        .toEqual(expect.arrayContaining(['C_ID', 'ADDR']))
    })
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: '保存' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    // 默认查询列不再即时落库，随模块「保存」在同一事务内提交
    expect(apiClientMock.put).not.toHaveBeenCalledWith('/admin/menus/110101/default-columns', expect.anything())
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith(
      '/admin/menus/110101',
      expect.objectContaining({
        module: expect.objectContaining({ M_IDX: 110101 }),
        defaultColumns: [{ table: 'master', fieldIds: ['C_ID', 'ADDR'] }],
      }),
    ))
  })

  it('渲染同级排序按钮：最前/前一步/后一步/最后', async () => {
    mockPageGetValue({ total: twoRoots.length, modules: twoRoots })
    renderPage()
    await waitForMenuTree()
    for (const name of ['最前', '前一步', '后一步', '最后']) {
      expect(screen.getByRole('button', { name })).toBeInTheDocument()
    }
  })

  it('展开/折叠全部为单一切换按钮：随树状态切换文案', async () => {
    renderPage()
    await waitForMenuTree()
    // 初始全部折叠：子节点不可见，按钮显示展开全部
    expect(screen.queryByRole('button', { name: /系统参数/ })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '展开全部' }))
    await waitFor(() => expect(screen.getByRole('button', { name: /系统参数/ })).toBeInTheDocument())
    expect(screen.getByRole('button', { name: /公司基本资料/ })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '折叠全部' })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '折叠全部' }))
    await waitFor(() => expect(screen.queryByRole('button', { name: /系统参数/ })).not.toBeInTheDocument())
    expect(screen.getByRole('button', { name: '展开全部' })).toBeInTheDocument()
  })

  it('排序按钮调用 PUT /sort 并按动作移动同级节点', async () => {
    const state = twoRoots.map((module) => ({ ...module }))
    const parentKeyOf = (module: MenuAdminModule) => (module.M_P_IDX != null && module.M_P_IDX > 0 ? module.M_P_IDX : 0)
    const applySort = (id: number, action: string) => {
      const target = state.find((module) => module.M_IDX === id)!
      const siblings = state
        .filter((module) => parentKeyOf(module) === parentKeyOf(target))
        .sort((a, b) => a.SORT_IDX - b.SORT_IDX || a.M_IDX - b.M_IDX)
      const ids = siblings.map((module) => module.M_IDX)
      const index = ids.indexOf(id)
      ids.splice(index, 1)
      const targetIndex = action === 'top' ? 0 : action === 'up' ? index - 1 : action === 'down' ? index + 1 : ids.length
      ids.splice(targetIndex, 0, id)
      ids.forEach((siblingId, position) => {
        const module = state.find((item) => item.M_IDX === siblingId)!
        module.SORT_IDX = (position + 1) * 10
      })
    }
    apiClientMock.get.mockImplementation(async () => ({ total: state.length, modules: state.map((module) => ({ ...module })) }))
    apiClientMock.put.mockImplementation(async (path: string, body?: { action?: string }) => {
      const match = /\/admin\/menus\/(\d+)\/sort$/.exec(path)
      if (match && body?.action) applySort(Number(match[1]), body.action)
      return undefined
    })
    vi.spyOn(window, 'confirm').mockImplementation(() => true)
    renderPage()
    await waitForMenuTree()

    // 选中第二个根节点后向上移动一位
    fireEvent.click(screen.getByRole('button', { name: /产品管理/ }))
    await waitFor(() => expect(screen.getByLabelText('菜单名称')).toHaveValue('产品管理'))
    const upButton = screen.getByRole('button', { name: '前一步' }) as HTMLButtonElement
    fireEvent.click(upButton)
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith('/admin/menus/12/sort', { action: 'up' }))
    await waitFor(() => expect(screen.getByRole('button', { name: '最后' })).toBeEnabled())

    // 移到同级最后
    fireEvent.click(screen.getByRole('button', { name: '最后' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith('/admin/menus/12/sort', { action: 'bottom' }))
    await waitFor(() => expect(screen.getByRole('button', { name: '最前' })).toBeEnabled())

    // 移到同级最前
    fireEvent.click(screen.getByRole('button', { name: '最前' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith('/admin/menus/12/sort', { action: 'top' }))
  })

  it('排序按钮在边界自动禁用：首节点不可上移，末节点不可下移', async () => {
    mockPageGetValue({ total: twoRoots.length, modules: twoRoots })
    renderPage()
    await waitForMenuTree()

    // 未选中节点时全部禁用
    expect(screen.getByRole('button', { name: '最前' })).toBeDisabled()
    expect(screen.getByRole('button', { name: '前一步' })).toBeDisabled()
    expect(screen.getByRole('button', { name: '后一步' })).toBeDisabled()
    expect(screen.getByRole('button', { name: '最后' })).toBeDisabled()

    // 首节点：最前/前一步禁用，后一步/最后可用
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    await waitFor(() => expect(screen.getByRole('button', { name: '最前' })).toBeDisabled())
    expect(screen.getByRole('button', { name: '前一步' })).toBeDisabled()
    expect(screen.getByRole('button', { name: '后一步' })).toBeEnabled()
    expect(screen.getByRole('button', { name: '最后' })).toBeEnabled()

    // 末节点：后一步/最后禁用，最前/前一步可用
    fireEvent.click(screen.getByRole('button', { name: /产品管理/ }))
    await waitFor(() => expect(screen.getByRole('button', { name: '最前' })).toBeEnabled())
    expect(screen.getByRole('button', { name: '前一步' })).toBeEnabled()
    expect(screen.getByRole('button', { name: '后一步' })).toBeDisabled()
    expect(screen.getByRole('button', { name: '最后' })).toBeDisabled()
  })

  it('拖拽模块到同级节点前：调用 PUT /move 同级重排', async () => {
    mockPageGetValue({ total: twoRoots.length, modules: twoRoots.map((module) => ({ ...module })) })
    renderPage()
    await waitForMenuTree()

    const dragNode = screen.getByText('产品管理').closest('.erp-menu-node') as HTMLElement
    const targetNode = screen.getByText('基本参数').closest('.erp-menu-node') as HTMLElement
    vi.spyOn(targetNode, 'getBoundingClientRect').mockReturnValue(rect(100))
    const dataTransfer = { setData: vi.fn(), effectAllowed: '' }

    fireEvent.dragStart(dragNode, { dataTransfer })
    fireEvent(targetNode, new MouseEvent('dragover', { bubbles: true, cancelable: true, clientY: 10 }))
    fireEvent(targetNode, new MouseEvent('drop', { bubbles: true, cancelable: true, clientY: 10 }))

    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith('/admin/menus/12/move', { parentId: null, beforeId: 11 }))
  })

  it('拖拽模块放入其它节点内：调用 PUT /move 跨父级移动', async () => {
    mockPageGetValue({ total: nestedModules.length, modules: nestedModules.map((module) => ({ ...module })) })
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    await waitFor(() => expect(screen.getByRole('button', { name: /系统参数/ })).toBeInTheDocument())

    const dragNode = screen.getByText('产品管理').closest('.erp-menu-node') as HTMLElement
    const targetNode = screen.getByText('系统参数').closest('.erp-menu-node') as HTMLElement
    vi.spyOn(targetNode, 'getBoundingClientRect').mockReturnValue(rect(100))
    const dataTransfer = { setData: vi.fn(), effectAllowed: '' }

    fireEvent.dragStart(dragNode, { dataTransfer })
    fireEvent(targetNode, new MouseEvent('dragover', { bubbles: true, cancelable: true, clientY: 50 }))
    fireEvent(targetNode, new MouseEvent('drop', { bubbles: true, cancelable: true, clientY: 50 }))

    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith('/admin/menus/12/move', { parentId: 1101, beforeId: null }))
  })

  it('拖到叶子行中间=插入到该行之前（不会误拖入叶子）', async () => {
    mockPageGetValue({ total: twoLeafRoots.length, modules: twoLeafRoots.map((module) => ({ ...module })) })
    renderPage()
    await waitForMenuTree()

    // 基本参数（11）是叶子；把产品管理（12）拖到其中间 → 视为插到 11 之前（同级排序），而非拖入
    const dragNode = screen.getByText('产品管理').closest('.erp-menu-node') as HTMLElement
    const targetNode = screen.getByText('基本参数').closest('.erp-menu-node') as HTMLElement
    vi.spyOn(targetNode, 'getBoundingClientRect').mockReturnValue(rect(100))
    const dataTransfer = { setData: vi.fn(), effectAllowed: '' }

    fireEvent.dragStart(dragNode, { dataTransfer })
    fireEvent(targetNode, new MouseEvent('dragover', { bubbles: true, cancelable: true, clientY: 50 }))
    fireEvent(targetNode, new MouseEvent('drop', { bubbles: true, cancelable: true, clientY: 50 }))

    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith('/admin/menus/12/move', { parentId: null, beforeId: 11 }))
  })

  it('拖到上一行中间即可上移一位（回归：此前被解析成 after 自身而成为无操作）', async () => {
    mockPageGetValue({ total: threeLeaves.length, modules: threeLeaves.map((module) => ({ ...module })) })
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    await waitFor(() => expect(screen.getByRole('button', { name: /系统参数/ })).toBeInTheDocument())

    // 报表查询（1103）拖到产品参数（1102）中间 → 插到 1102 之前，即 1103 上移一位
    const dragNode = screen.getByText('报表查询').closest('.erp-menu-node') as HTMLElement
    const targetNode = screen.getByText('产品参数').closest('.erp-menu-node') as HTMLElement
    vi.spyOn(targetNode, 'getBoundingClientRect').mockReturnValue(rect(100))
    const dataTransfer = { setData: vi.fn(), effectAllowed: '' }

    fireEvent.dragStart(dragNode, { dataTransfer })
    fireEvent(targetNode, new MouseEvent('dragover', { bubbles: true, cancelable: true, clientY: 50 }))
    fireEvent(targetNode, new MouseEvent('drop', { bubbles: true, cancelable: true, clientY: 50 }))

    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith('/admin/menus/1103/move', { parentId: 11, beforeId: 1102 }))
  })

  it('拖到上一行下缘（after 自身）视为原位，不发起移动请求', async () => {
    mockPageGetValue({ total: threeLeaves.length, modules: threeLeaves.map((module) => ({ ...module })) })
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    await waitFor(() => expect(screen.getByRole('button', { name: /系统参数/ })).toBeInTheDocument())

    // 报表查询（1103）拖到产品参数（1102）下缘 → after 1102 的下一个是 1103 自身 → 无操作
    const dragNode = screen.getByText('报表查询').closest('.erp-menu-node') as HTMLElement
    const targetNode = screen.getByText('产品参数').closest('.erp-menu-node') as HTMLElement
    vi.spyOn(targetNode, 'getBoundingClientRect').mockReturnValue(rect(100))
    const dataTransfer = { setData: vi.fn(), effectAllowed: '' }

    fireEvent.dragStart(dragNode, { dataTransfer })
    fireEvent(targetNode, new MouseEvent('dragover', { bubbles: true, cancelable: true, clientY: 90 }))
    fireEvent(targetNode, new MouseEvent('drop', { bubbles: true, cancelable: true, clientY: 90 }))
    await new Promise((resolve) => setTimeout(resolve, 50))

    expect(apiClientMock.put).not.toHaveBeenCalledWith(expect.stringContaining('/move'))
  })

  it('不能拖拽到自身子孙节点（防止循环引用）', async () => {
    mockPageGetValue({ total: twoRoots.length, modules: twoRoots.map((module) => ({ ...module })) })
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    await waitFor(() => expect(screen.getByRole('button', { name: /系统参数/ })).toBeInTheDocument())

    const dragNode = screen.getByText('基本参数').closest('.erp-menu-node') as HTMLElement
    const targetNode = screen.getByText('系统参数').closest('.erp-menu-node') as HTMLElement
    vi.spyOn(targetNode, 'getBoundingClientRect').mockReturnValue(rect(100))
    const dataTransfer = { setData: vi.fn(), effectAllowed: '' }

    fireEvent.dragStart(dragNode, { dataTransfer })
    fireEvent(targetNode, new MouseEvent('dragover', { bubbles: true, cancelable: true, clientY: 50 }))
    fireEvent(targetNode, new MouseEvent('drop', { bubbles: true, cancelable: true, clientY: 50 }))
    await new Promise((resolve) => setTimeout(resolve, 50))

    expect(apiClientMock.put).not.toHaveBeenCalledWith(expect.stringContaining('/move'))
  })

  it('表单不再展示编号/排序号/上级菜单/启用与删除按钮', async () => {
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    await waitFor(() => expect(screen.getByLabelText('菜单名称')).toHaveValue('基本参数'))
    expect(screen.queryByLabelText('菜单编号')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('排序号')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('上级菜单')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('启用')).not.toBeInTheDocument()
    expect(screen.queryByText('菜单图标')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '删除' })).not.toBeInTheDocument()
  })

  it('一级菜单右键菜单提供重命名/启停/添加子节点/更换图标/删除', async () => {
    renderPage()
    await waitForMenuTree()
    const row = screen.getByText('基本参数').closest('.erp-menu-node') as HTMLElement
    fireEvent.contextMenu(row, { clientX: 120, clientY: 80 })
    expect(screen.getByRole('menuitem', { name: '重命名' })).toBeInTheDocument()
    expect(screen.getByRole('menuitem', { name: '停用' })).toBeInTheDocument()
    expect(screen.getByRole('menuitem', { name: '添加子节点' })).toBeInTheDocument()
    expect(screen.getByRole('menuitem', { name: '更换图标' })).toBeInTheDocument()
    expect(screen.getByRole('menuitem', { name: '删除' })).toBeInTheDocument()
  })

  it('非一级菜单右键菜单不显示更换图标', async () => {
    mockPageGetValue({ total: nestedModules.length, modules: nestedModules.map((module) => ({ ...module })) })
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    await waitFor(() => expect(screen.getByRole('button', { name: /系统参数/ })).toBeInTheDocument())
    const row = screen.getByText('系统参数').closest('.erp-menu-node') as HTMLElement
    fireEvent.contextMenu(row, { clientX: 120, clientY: 80 })
    expect(screen.queryByRole('menuitem', { name: '更换图标' })).not.toBeInTheDocument()
    expect(screen.getByRole('menuitem', { name: '重命名' })).toBeInTheDocument()
  })

  it('右键重命名以内联输入保存', async () => {
    const dispatchSpy = vi.spyOn(window, 'dispatchEvent')
    renderPage()
    await waitForMenuTree()
    const row = screen.getByText('基本参数').closest('.erp-menu-node') as HTMLElement
    fireEvent.contextMenu(row, { clientX: 120, clientY: 80 })
    fireEvent.click(screen.getByRole('menuitem', { name: '重命名' }))

    const renameInput = document.querySelector('.erp-menu-rename-input') as HTMLInputElement
    expect(renameInput).toBeInTheDocument()
    fireEvent.change(renameInput, { target: { value: '基本参数-新名' } })
    fireEvent.keyDown(renameInput, { key: 'Enter' })

    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith(
      '/admin/menus/11/rename',
      { description: '基本参数-新名' },
    ))
    expect(dispatchSpy.mock.calls.some(([event]) => (event as Event).type === 'eos:menu-changed')).toBe(true)
  })

  it('右键停用/启用切换 M_TAG', async () => {
    renderPage()
    await waitForMenuTree()
    const row = screen.getByText('基本参数').closest('.erp-menu-node') as HTMLElement
    fireEvent.contextMenu(row, { clientX: 120, clientY: 80 })
    fireEvent.click(screen.getByRole('menuitem', { name: '停用' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith(
      '/admin/menus/11/enabled',
      { enabled: false },
    ))
  })

  it('停用节点在左侧树上有停用标记', async () => {
    mockPageGetValue({
      total: 1,
      modules: [{ ...moduleNode(11, '基本参数', null), M_TAG: false }],
    })
    renderPage()
    await waitForMenuTree()
    expect(screen.getByText('停用')).toBeInTheDocument()
  })

  it('右键删除经确认后调用 DELETE', async () => {
    vi.spyOn(window, 'confirm').mockImplementation(() => true)
    renderPage()
    await waitForMenuTree()
    const row = screen.getByText('基本参数').closest('.erp-menu-node') as HTMLElement
    fireEvent.contextMenu(row, { clientX: 120, clientY: 80 })
    fireEvent.click(screen.getByRole('menuitem', { name: '删除' }))
    await waitFor(() => expect(apiClientMock.delete).toHaveBeenCalledWith('/admin/menus/11'))
  })

  it('右键「更换图标」选择后保存到一级菜单', async () => {
    renderPage()
    await waitForMenuTree()
    const row = screen.getByText('基本参数').closest('.erp-menu-node') as HTMLElement
    fireEvent.contextMenu(row, { clientX: 120, clientY: 80 })
    fireEvent.click(screen.getByRole('menuitem', { name: '更换图标' }))
    await waitFor(() => expect(screen.getByTitle('product')).toBeInTheDocument())
    fireEvent.click(screen.getByTitle('product'))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith(
      '/admin/menus/11',
      expect.objectContaining({ module: expect.objectContaining({ M_ICON: 'product' }) }),
    ))
  })

  it('表选择器选择操作主表并回填', async () => {
    mockPageGetValue({ total: modules.length, modules })
    apiClientMock.post.mockImplementation(async (path: string) => {
      if (path === '/chooser/query') return tableChooserData
      throw new Error(`unexpected POST ${path}`)
    })
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    fireEvent.click(screen.getByRole('tab', { name: '主表' }))
    await waitFor(() => expect(screen.getAllByRole('button', { name: '选择…' }).length).toBeGreaterThan(0))
    fireEvent.click(screen.getAllByRole('button', { name: '选择…' })[0])
    await waitFor(() => expect(screen.getByText('公司基本资料')).toBeInTheDocument())
    fireEvent.click(screen.getByText('公司基本资料'))
    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    await waitFor(() => expect(screen.getByLabelText('操作主表名')).toHaveValue('COMPANY'))
  })

  it('排序字段选择器：多选并设置升降序后回填', async () => {
    const fieldRows = [
      { F_ID: 'C_ID', F_DESC: '公司编号', F_TYPE: 'nvarchar', IS_VISIBLE: true, IS_VIRTUAL: false, IS_QUERY: true },
      { F_ID: 'C_NAME', F_DESC: '公司名称', F_TYPE: 'nvarchar', IS_VISIBLE: true, IS_VIRTUAL: false, IS_QUERY: true },
    ]
    mockPageGet(async (path: string) => {
      if (path.includes('/admin/menus/fields')) return fieldRows
      return { total: 3, modules: [moduleNode(11, '基本参数', null), moduleNode(1101, '系统参数', 11), withTables] }
    })
    apiClientMock.post.mockImplementation(async (path: string) => {
      if (path === '/chooser/query') return fieldChooserData(fieldRows)
      throw new Error(`unexpected POST ${path}`)
    })
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    fireEvent.click(screen.getByRole('button', { name: /系统参数/ }))
    fireEvent.click(screen.getByRole('button', { name: /公司基本资料/ }))
    fireEvent.click(screen.getByRole('tab', { name: '主表' }))
    await waitFor(() => expect(screen.getByRole('button', { name: '默认列' })).toBeEnabled())

    fireEvent.click(screen.getByTitle('选择排序字段'))
    await waitFor(() => expect(screen.getByLabelText('选择字段 C_ID')).toBeInTheDocument())
    fireEvent.click(screen.getByLabelText('选择字段 C_ID'))
    fireEvent.click(screen.getAllByRole('button', { name: '升序' })[0])
    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    await waitFor(() => expect(screen.getByLabelText('排序字段')).toHaveValue('C_ID DESC'))
  })

  it('新增明细必需字段选择器：多选后按分号回填', async () => {
    const fieldRows = [
      { F_ID: 'C_ID', F_DESC: '公司编号', F_TYPE: 'nvarchar', IS_VISIBLE: true, IS_VIRTUAL: false, IS_QUERY: true },
      { F_ID: 'C_NAME', F_DESC: '公司名称', F_TYPE: 'nvarchar', IS_VISIBLE: true, IS_VIRTUAL: false, IS_QUERY: true },
    ]
    mockPageGet(async (path: string) => {
      if (path.includes('/admin/menus/fields')) return fieldRows
      return { total: 3, modules: [moduleNode(11, '基本参数', null), moduleNode(1101, '系统参数', 11), withTables] }
    })
    apiClientMock.post.mockImplementation(async (path: string) => {
      if (path === '/chooser/query') return fieldChooserData(fieldRows)
      throw new Error(`unexpected POST ${path}`)
    })
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    fireEvent.click(screen.getByRole('button', { name: /系统参数/ }))
    fireEvent.click(screen.getByRole('button', { name: /公司基本资料/ }))
    fireEvent.click(screen.getByRole('tab', { name: '子表' }))
    await waitFor(() => expect(screen.getByRole('button', { name: '默认列' })).toBeEnabled())

    fireEvent.click(screen.getByTitle('选择新增明细必需字段'))
    await waitFor(() => expect(screen.getByLabelText('选择字段 C_ID')).toBeInTheDocument())
    fireEvent.click(screen.getByLabelText('选择字段 C_ID'))
    fireEvent.click(screen.getByLabelText('选择字段 C_NAME'))
    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    await waitFor(() => expect(screen.getByLabelText('新增明细时必需字段')).toHaveValue('C_ID;C_NAME'))
  })

  it('主表过滤条件构建器：添加条件后回填谓词', async () => {
    const fieldRows = [
      { F_ID: 'C_ID', F_DESC: '公司编号', F_TYPE: 'nvarchar', IS_VISIBLE: true, IS_VIRTUAL: false, IS_QUERY: true },
    ]
    mockPageGet(async (path: string) => {
      if (path.includes('/admin/menus/fields')) return fieldRows
      return { total: 3, modules: [moduleNode(11, '基本参数', null), moduleNode(1101, '系统参数', 11), withTables] }
    })
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    fireEvent.click(screen.getByRole('button', { name: /系统参数/ }))
    fireEvent.click(screen.getByRole('button', { name: /公司基本资料/ }))
    fireEvent.click(screen.getByRole('tab', { name: '主表' }))
    await waitFor(() => expect(screen.getByRole('button', { name: '默认列' })).toBeEnabled())

    fireEvent.click(screen.getByTitle('构建主表过滤条件'))
    await waitFor(() => expect(screen.getByRole('button', { name: '添加条件' })).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '添加条件' }))
    const [fieldSelect, opSelect] = screen.getAllByRole('combobox')
    fireEvent.change(fieldSelect, { target: { value: 'C_ID' } })
    fireEvent.change(opSelect, { target: { value: '=' } })
    fireEvent.change(screen.getByPlaceholderText('值'), { target: { value: '1' } })
    fireEvent.click(screen.getByRole('button', { name: '确定' }))
    await waitFor(() => expect(screen.getByLabelText('主表过滤条件')).toHaveValue('(C_ID = 1)'))
  })

  it('编辑表单按页签分组：基础/主表/子表/分组/统一表单', async () => {
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    await waitFor(() => expect(screen.getByLabelText('菜单名称')).toHaveValue('基本参数'))

    expect(screen.getByRole('tab', { name: '基础' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByLabelText('页面链接（现代路由）')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('tab', { name: '主表' }))
    expect(screen.getByLabelText('操作主表名')).toBeInTheDocument()
    expect(screen.getByLabelText('主表过滤条件')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('tab', { name: '子表' }))
    expect(screen.getByLabelText('操作副表名')).toBeInTheDocument()
    expect(screen.getByLabelText('新增明细时必需字段')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('tab', { name: '分组' }))
    expect(screen.getByLabelText('表达式1（如 TABLE.COL、CASE 或日期函数）')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('tab', { name: '统一表单' }))
    expect(screen.getByLabelText('页签定义（FORM_TABS）')).toBeInTheDocument()
  })

  it('过滤条件构建器：未选择字段时给出错误并禁止保存', async () => {
    const fieldRows = [
      { F_ID: 'C_ID', F_DESC: '公司编号', F_TYPE: 'nvarchar', IS_VISIBLE: true, IS_VIRTUAL: false, IS_QUERY: true },
    ]
    mockPageGet(async (path: string) => {
      if (path.includes('/admin/menus/fields')) return fieldRows
      return { total: 3, modules: [moduleNode(11, '基本参数', null), moduleNode(1101, '系统参数', 11), withTables] }
    })
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    fireEvent.click(screen.getByRole('button', { name: /系统参数/ }))
    fireEvent.click(screen.getByRole('button', { name: /公司基本资料/ }))
    fireEvent.click(screen.getByRole('tab', { name: '主表' }))
    await waitFor(() => expect(screen.getByTitle('构建主表过滤条件')).toBeEnabled())

    fireEvent.click(screen.getByTitle('构建主表过滤条件'))
    await waitFor(() => expect(screen.getByRole('button', { name: '添加条件' })).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '添加条件' }))

    expect(screen.getByText('请选择字段')).toBeInTheDocument()
    expect(screen.getByText(/还有 1 处条件不完整/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '确定' })).toBeDisabled()
  })

  it('过滤条件构建器：数值字段填入非数字时阻止保存并提示', async () => {
    const fieldRows = [
      { F_ID: 'QTY', F_DESC: '数量', F_TYPE: 'decimal', IS_VISIBLE: true, IS_VIRTUAL: false, IS_QUERY: true },
    ]
    mockPageGet(async (path: string) => {
      if (path.includes('/admin/menus/fields')) return fieldRows
      return { total: 3, modules: [moduleNode(11, '基本参数', null), moduleNode(1101, '系统参数', 11), withTables] }
    })
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    fireEvent.click(screen.getByRole('button', { name: /系统参数/ }))
    fireEvent.click(screen.getByRole('button', { name: /公司基本资料/ }))
    fireEvent.click(screen.getByRole('tab', { name: '主表' }))
    await waitFor(() => expect(screen.getByTitle('构建主表过滤条件')).toBeEnabled())

    fireEvent.click(screen.getByTitle('构建主表过滤条件'))
    await waitFor(() => expect(screen.getByRole('button', { name: '添加条件' })).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '添加条件' }))
    fireEvent.change(screen.getAllByRole('combobox')[0], { target: { value: 'QTY' } })
    fireEvent.change(screen.getByPlaceholderText('值'), { target: { value: 'abc' } })

    expect(screen.getByText(/为数值类型，比较值应为数字/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '确定' })).toBeDisabled()
  })

  it('过滤条件构建器：保留无法解析的既有复杂条件，不静默清空', async () => {
    const complexModule = { ...withTables, FILTER: "ISNULL(C_ID,'')=''" }
    mockPageGet(async (path: string) => {
      if (path.includes('/admin/menus/fields')) {
        return [{ F_ID: 'C_ID', F_DESC: '公司编号', F_TYPE: 'nvarchar', IS_VISIBLE: true, IS_VIRTUAL: false, IS_QUERY: true }]
      }
      return { total: 3, modules: [moduleNode(11, '基本参数', null), moduleNode(1101, '系统参数', 11), complexModule] }
    })
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    fireEvent.click(screen.getByRole('button', { name: /系统参数/ }))
    fireEvent.click(screen.getByRole('button', { name: /公司基本资料/ }))
    fireEvent.click(screen.getByRole('tab', { name: '主表' }))
    await waitFor(() => expect(screen.getByTitle('构建主表过滤条件')).toBeEnabled())

    fireEvent.click(screen.getByTitle('构建主表过滤条件'))
    await waitFor(() => expect(screen.getByText(/已保留原值/)).toBeInTheDocument())
    expect(screen.getByRole('button', { name: '确定' })).toBeEnabled()
    fireEvent.click(screen.getByRole('button', { name: '确定' }))

    await waitFor(() => expect(screen.getByLabelText('主表过滤条件')).toHaveValue("ISNULL(C_ID,'')=''"))
  })

  it('表/字段/过滤/存储过程等配置输入为只读，防止手工录入', async () => {
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    await waitFor(() => expect(screen.getByLabelText('菜单名称')).toHaveValue('基本参数'))

    fireEvent.click(screen.getByRole('tab', { name: '主表' }))
    for (const label of ['操作主表名', '主表过滤条件', '排序字段', '字段有值时不可解批（主表）']) {
      expect(screen.getByLabelText(label)).toHaveAttribute('readonly')
    }

    fireEvent.click(screen.getByRole('tab', { name: '子表' }))
    for (const label of ['操作副表名', '新增明细时必需字段', '字段有值时不可解批（副表）']) {
      expect(screen.getByLabelText(label)).toHaveAttribute('readonly')
    }
  })

  it('主表缺状态列且具备批核能力时提示发布将被拦截', async () => {
    mockPageGet(async (path: string) => {
      if (path.includes('/admin/tables/') && path.endsWith('/columns')) return [{ name: 'ORDER_NO' }]
      return { total: 2, modules: [moduleNode(11, '基本参数', null), { ...withTables, M_IDX: 1405, M_DESC: '客户订单', M_P_IDX: null, AUTO_APPROVE: true }] }
    })
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /客户订单/ }))
    fireEvent.click(screen.getByRole('tab', { name: '主表' }))
    await waitFor(() => expect(screen.getByText(/缺少 CONFIRM_TAG/)).toBeInTheDocument())
  })

  it('主表具备状态列时不提示', async () => {
    mockPageGet(async (path: string) => {
      if (path.includes('/admin/tables/') && path.endsWith('/columns')) return [{ name: 'ORDER_NO' }, { name: 'CONFIRM_TAG' }]
      return { total: 2, modules: [moduleNode(11, '基本参数', null), { ...withTables, M_IDX: 1405, M_DESC: '客户订单', M_P_IDX: null, AUTO_APPROVE: true }] }
    })
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /客户订单/ }))
    fireEvent.click(screen.getByRole('tab', { name: '主表' }))
    await waitFor(() => expect(screen.getByLabelText('操作主表名')).toHaveValue('COMPANY'))
    expect(screen.queryByText(/缺少 CONFIRM_TAG/)).not.toBeInTheDocument()
  })

  it('切页签保草稿：切走再切回，未保存的行为配置编辑仍在且脏标记不回退', async () => {
    mockPageWithBusinessConfig()
    await openModuleTab()
    await screen.findByText('业务动作（1）')

    fireEvent.click(screen.getByText('收料量回写采购单'))
    fireEvent.click(screen.getAllByRole('button', { name: '编辑' })[0])
    const dialog = within(await screen.findByRole('dialog'))
    fireEvent.change(dialog.getByDisplayValue('收料量回写采购单'), { target: { value: '改过的名称' } })
    fireEvent.click(dialog.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(screen.getByText('已修改未保存')).toBeInTheDocument())

    // 切到别的页签再切回来：面板不得被卸载重挂，草稿与脏标记都要保持。
    fireEvent.click(screen.getByRole('tab', { name: '基础' }))
    fireEvent.click(screen.getByRole('tab', { name: '行为动作' }))

    // 动作列表的名称列与影响面自检的问题文案都会出现该名称，故只断言"仍在"。
    await waitFor(() => expect(screen.getAllByText('改过的名称').length).toBeGreaterThan(0))
    expect(screen.getByText('已修改未保存')).toBeInTheDocument()
  })

  it('行为配置拆成三个页签，无操作主/副表的模块三个页签禁用并说明原因', async () => {
    mockPageWithBusinessConfig()
    renderPage()
    await waitForMenuTree()

    // 纯菜单节点（无主表也无副表）：三个行为页签都在，但点不动，且说明原因。
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    expect(screen.getAllByRole('tab')).toHaveLength(8)
    for (const label of ['行为动作', '校验规则', '自定义按钮']) {
      expect(screen.getByRole('tab', { name: label })).toBeDisabled()
    }
    expect(screen.getByRole('tab', { name: '校验规则' })).toHaveAttribute(
      'title',
      expect.stringContaining('未配置操作主表/副表'),
    )

    // 含表模块：三个页签可用。
    fireEvent.click(screen.getByRole('button', { name: /系统参数/ }))
    fireEvent.click(screen.getByRole('button', { name: /公司基本资料/ }))
    for (const label of ['行为动作', '校验规则', '自定义按钮']) {
      expect(screen.getByRole('tab', { name: label })).toBeEnabled()
    }
  })

  it('三个行为页签之间来回切换后草稿与脏标记都保持', async () => {
    mockPageWithBusinessConfig()
    await openModuleTab()
    await screen.findByText('业务动作（1）')

    fireEvent.click(screen.getByText('收料量回写采购单'))
    fireEvent.click(screen.getAllByRole('button', { name: '编辑' })[0])
    const dialog = within(await screen.findByRole('dialog'))
    fireEvent.change(dialog.getByDisplayValue('收料量回写采购单'), { target: { value: '改过的名称' } })
    fireEvent.click(dialog.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(screen.getByText('已修改未保存')).toBeInTheDocument())

    fireEvent.click(screen.getByRole('tab', { name: '校验规则' }))
    expect(await screen.findByText('校验规则（0）')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('tab', { name: '自定义按钮' }))
    expect(await screen.findByText('自定义按钮（0）')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('tab', { name: '行为动作' }))

    await waitFor(() => expect(screen.getAllByText('改过的名称').length).toBeGreaterThan(0))
    expect(screen.getByText('已修改未保存')).toBeInTheDocument()
  })

  it('「取消」丢弃改动：行为配置回到服务端配置', async () => {
    mockPageWithBusinessConfig()
    await openModuleTab()
    await screen.findByText('业务动作（1）')

    fireEvent.click(screen.getByText('收料量回写采购单'))
    fireEvent.click(screen.getAllByRole('button', { name: '编辑' })[0])
    const dialog = within(await screen.findByRole('dialog'))
    fireEvent.change(dialog.getByDisplayValue('收料量回写采购单'), { target: { value: '改过的名称' } })
    fireEvent.click(dialog.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(screen.getByText('已修改未保存')).toBeInTheDocument())

    fireEvent.click(screen.getByRole('button', { name: '取消' }))

    // 名称列与影响面自检文案都会出现动作名，故只断言"回到服务端配置"。
    await waitFor(() => expect(screen.getAllByText('收料量回写采购单').length).toBeGreaterThan(0))
    expect(screen.queryByText('改过的名称')).not.toBeInTheDocument()
    expect(screen.getByText('未发布')).toBeInTheDocument()
  })

  it('切换模块前提示未保存改动：取消则留在原模块，确认后才切走', async () => {
    mockPageWithBusinessConfig()
    await openModuleTab()
    await screen.findByText('业务动作（1）')

    fireEvent.click(screen.getByText('收料量回写采购单'))
    fireEvent.click(screen.getAllByRole('button', { name: '编辑' })[0])
    const dialog = within(await screen.findByRole('dialog'))
    fireEvent.change(dialog.getByDisplayValue('收料量回写采购单'), { target: { value: '改过的名称' } })
    fireEvent.click(dialog.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(screen.getByText('已修改未保存')).toBeInTheDocument())

    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    fireEvent.click(screen.getByRole('button', { name: /系统参数/ }))
    expect(confirm).toHaveBeenCalled()
    expect(screen.getByText('已选择：公司基本资料（ID：110101）')).toBeInTheDocument()

    confirm.mockReturnValue(true)
    fireEvent.click(screen.getByRole('button', { name: /系统参数/ }))
    await waitFor(() => expect(screen.getByLabelText('菜单名称')).toHaveValue('系统参数'))
  })

  it('统一表单页签把「内置动作」与「自定义按钮」分清楚并互相指引', async () => {
    mockPageWithBusinessConfig()
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    fireEvent.click(screen.getByRole('tab', { name: '统一表单' }))

    expect(screen.getByLabelText('内置动作（受控注册码）')).toBeInTheDocument()
    expect(screen.getByText(/见「行为 › 自定义按钮」/)).toBeInTheDocument()
  })

  it('无模块配置权的账号不渲染行为页签，预演与说明书入口随之不出现', async () => {
    mockPageWithBusinessConfig(undefined, { canBrowse: true, canSetup: true, canModuleConfig: false })
    renderPage()
    await waitForMenuTree()
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    fireEvent.click(screen.getByRole('button', { name: /系统参数/ }))
    fireEvent.click(screen.getByRole('button', { name: /公司基本资料/ }))

    expect(screen.queryByRole('tab', { name: '行为动作' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '行为说明书' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '预演（不改数据）' })).not.toBeInTheDocument()
  })

})

const rect = (height: number): DOMRect => ({
  top: 0,
  bottom: height,
  left: 0,
  right: 300,
  width: 300,
  height,
  x: 0,
  y: 0,
  toJSON: () => ({}),
})
