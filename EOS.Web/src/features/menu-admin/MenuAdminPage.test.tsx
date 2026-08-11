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
})

const modules = [moduleNode(11, '基本参数', null), moduleNode(1101, '系统参数', 11), moduleNode(110101, '公司基本资料', 1101)]
const withTables: MenuAdminModule = { ...moduleNode(110101, '公司基本资料', 1101), MASTER_TABLE: 'COMPANY', DETAIL_TABLE: 'COMPANY_D' }

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MenuAdminPage />
    </QueryClientProvider>,
  )
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
    await waitFor(() => expect(screen.getByRole('button', { name: /基本参数/ })).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    await waitFor(() => expect(screen.getByLabelText('菜单名称')).toHaveValue('基本参数'))
    expect(screen.getByLabelText('菜单编号')).toHaveValue(11)
  })

  it('保存已有节点调用 PUT', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByRole('button', { name: /基本参数/ })).toBeInTheDocument())
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
    fireEvent.change(screen.getByLabelText('菜单编号'), { target: { value: '999' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith('/admin/menus', expect.objectContaining({ M_IDX: 999, M_DESC: '新菜单' })))
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
    await waitFor(() => expect(screen.getByRole('button', { name: /基本参数/ })).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: /基本参数/ }))
    fireEvent.click(screen.getByRole('button', { name: /系统参数/ }))
    fireEvent.click(screen.getByRole('button', { name: /公司基本资料/ }))
    await waitFor(() => expect(screen.getByRole('button', { name: '默认查询（主表）' })).toBeEnabled())
    fireEvent.click(screen.getByRole('button', { name: '默认查询（主表）' }))
    await waitFor(() => expect(screen.getByText('主表默认查询列')).toBeInTheDocument())
    await waitFor(() => expect(screen.getByText('公司编号')).toBeInTheDocument())
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith(
      '/admin/menus/110101/default-columns',
      { table: 'master', fieldIds: ['C_ID', 'ADDR'] },
    ))
  })
})
