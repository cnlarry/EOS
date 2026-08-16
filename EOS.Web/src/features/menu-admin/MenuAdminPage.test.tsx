import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MenuAdminPage, type MenuAdminModule } from './MenuAdminPage'

const apiClientMock = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn() }))
vi.mock('../../services/api', () => ({ apiClient: apiClientMock }))

const moduleNode = (id: number, desc: string, parent: number | null): MenuAdminModule => ({
  M_IDX: id, M_ALIAS: null, M_DESC: desc, M_URL: null, NEW_URL: null, MODI_URL: null, HELP_URL: null,
  DETAIL_NO_FIELDS: null, DETAIL_NO_SAVE: false, SEARCH_1: false, SEARCH_2: false, M_P_IDX: parent,
  SORT_IDX: 0, M_TAG: true, AUTO_APPROVE: false, IF_COPY: false, ERROR_NO_SAVE: false, SORT_FIELDS: null,
  MASTER_TABLE: null, FILTER: null, DETAIL_TABLE: null, UPDATE_SP: null, AFTERSAVE_SP: null,
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
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MenuAdminPage />
    </QueryClientProvider>,
  )
}

/** 等待菜单树初始渲染（全量并发测试下放宽超时，避免首帧超时抖动）。 */
async function waitForMenuTree() {
  await waitFor(() => expect(screen.getByRole('button', { name: /基本参数/ })).toBeInTheDocument(), { timeout: 5000 })
}

describe('MenuAdminPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockResolvedValue({ total: modules.length, modules })
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
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith('/admin/menus/11', expect.objectContaining({ M_DESC: '基本参数-改' })))
  })

  it('新增根节点调用 POST', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByRole('button', { name: '新增根节点' })).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '新增根节点' }))
    await waitFor(() => expect(screen.getByLabelText('菜单名称')).toHaveValue(''))
    fireEvent.change(screen.getByLabelText('菜单名称'), { target: { value: '新菜单' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith('/admin/menus', expect.objectContaining({ M_IDX: 0, M_DESC: '新菜单' })))
  })

  it('新增根节点保存后选中服务端返回的新编号', async () => {
    let posted = false
    apiClientMock.get.mockImplementation(async () => {
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
    apiClientMock.get.mockImplementation(async (path: string) => {
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
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith(
      '/admin/menus/110101/default-columns',
      { table: 'master', fieldIds: ['C_ID', 'ADDR'] },
    ))
  })

  it('渲染同级排序按钮：最前/前一步/后一步/最后', async () => {
    apiClientMock.get.mockResolvedValue({ total: twoRoots.length, modules: twoRoots })
    renderPage()
    await waitForMenuTree()
    for (const name of ['最前', '前一步', '后一步', '最后']) {
      expect(screen.getByRole('button', { name })).toBeInTheDocument()
    }
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
    apiClientMock.get.mockResolvedValue({ total: twoRoots.length, modules: twoRoots })
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
    apiClientMock.get.mockResolvedValue({ total: twoRoots.length, modules: twoRoots.map((module) => ({ ...module })) })
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
    apiClientMock.get.mockResolvedValue({ total: nestedModules.length, modules: nestedModules.map((module) => ({ ...module })) })
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
    apiClientMock.get.mockResolvedValue({ total: twoLeafRoots.length, modules: twoLeafRoots.map((module) => ({ ...module })) })
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
    apiClientMock.get.mockResolvedValue({ total: threeLeaves.length, modules: threeLeaves.map((module) => ({ ...module })) })
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
    apiClientMock.get.mockResolvedValue({ total: threeLeaves.length, modules: threeLeaves.map((module) => ({ ...module })) })
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
    apiClientMock.get.mockResolvedValue({ total: twoRoots.length, modules: twoRoots.map((module) => ({ ...module })) })
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
    apiClientMock.get.mockResolvedValue({ total: nestedModules.length, modules: nestedModules.map((module) => ({ ...module })) })
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
      '/admin/menus/11',
      expect.objectContaining({ M_DESC: '基本参数-新名' }),
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
      '/admin/menus/11',
      expect.objectContaining({ M_TAG: false }),
    ))
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
      expect.objectContaining({ M_ICON: 'product' }),
    ))
  })

  it('表选择器选择操作主表并回填', async () => {
    apiClientMock.get.mockResolvedValue({ total: modules.length, modules })
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
    apiClientMock.get.mockImplementation(async (path: string) => {
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
    await waitFor(() => expect(screen.getByText('公司编号')).toBeInTheDocument())
    fireEvent.click(screen.getByText('公司编号'))
    fireEvent.click(screen.getAllByRole('button', { name: '升序' })[0])
    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    await waitFor(() => expect(screen.getByLabelText('排序字段')).toHaveValue('C_ID DESC'))
  })

  it('新增明细必需字段选择器：多选后按分号回填', async () => {
    const fieldRows = [
      { F_ID: 'C_ID', F_DESC: '公司编号', F_TYPE: 'nvarchar', IS_VISIBLE: true, IS_VIRTUAL: false, IS_QUERY: true },
      { F_ID: 'C_NAME', F_DESC: '公司名称', F_TYPE: 'nvarchar', IS_VISIBLE: true, IS_VIRTUAL: false, IS_QUERY: true },
    ]
    apiClientMock.get.mockImplementation(async (path: string) => {
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
    await waitFor(() => expect(screen.getByText('公司编号')).toBeInTheDocument())
    fireEvent.click(screen.getByText('公司编号'))
    fireEvent.click(screen.getByText('公司名称'))
    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    await waitFor(() => expect(screen.getByLabelText('新增明细时必需字段')).toHaveValue('C_ID;C_NAME'))
  })

  it('主表过滤条件构建器：添加条件后回填谓词', async () => {
    const fieldRows = [
      { F_ID: 'C_ID', F_DESC: '公司编号', F_TYPE: 'nvarchar', IS_VISIBLE: true, IS_VIRTUAL: false, IS_QUERY: true },
    ]
    apiClientMock.get.mockImplementation(async (path: string) => {
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
    apiClientMock.get.mockImplementation(async (path: string) => {
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
    apiClientMock.get.mockImplementation(async (path: string) => {
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
    apiClientMock.get.mockImplementation(async (path: string) => {
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
