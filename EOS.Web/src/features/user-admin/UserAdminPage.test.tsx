import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../types/api'
import { useAuth } from '../auth/authContext'
import { UserAdminPage } from './UserAdminPage'


vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))
vi.mock('../auth/authContext', () => ({ useAuth: vi.fn() }))

const usersPage = {
  items: [
    { userId: 'admin', employeeId: 'E001', employeeName: 'Demo User', departmentId: 'D1', departmentName: '信息部', companyId: 'C1', groupId: 'G1', groups: '超级用户组', isActive: true, hasPassword: true, lastUpdatedBy: 'admin', lastUpdatedAt: '2026-08-01T00:00:00Z' },
    { userId: 'viewer', employeeId: 'E002', employeeName: '只读用户', departmentId: 'D2', departmentName: '财务部', companyId: 'C1', groupId: 'G2', groups: '采购、财务', isActive: false, hasPassword: false, lastUpdatedBy: 'admin', lastUpdatedAt: null },
  ],
  page: 1,
  pageSize: 50,
  total: 2,
}

const allGroups = [
  { groupId: 'CG', groupDescription: '采购', memberCount: 2, remark: null },
  { groupId: 'CW', groupDescription: '财务', memberCount: 0, remark: null },
]
// 统一选择器 sourceKey 响应：列键来自 110104（SYSDN）字段元数据（大写 F_ID）
const employeeChooserData = {
  columns: [
    { key: 'EMP_ID', label: '员工号', dataType: 'string' },
    { key: 'EMP_NAME', label: '姓名', dataType: 'string' },
    { key: 'DEPT_NAME', label: '部门', dataType: 'string' },
  ],
  defaultKeys: ['EMP_ID', 'EMP_NAME', 'DEPT_NAME'],
  rows: [
    { EMP_ID: 'E999', EMP_NAME: '新员工', DEPT_ID: 'D9', DEPT_NAME: '新部门' },
  ],
  total: 1,
}

const groupChooserData = {
  columns: [
    { key: 'G_IDX', label: '组ID', dataType: 'string' },
    { key: 'G_DESC', label: '组名', dataType: 'string' },
  ],
  defaultKeys: ['G_IDX', 'G_DESC'],
  rows: [
    { G_IDX: 'CG', G_DESC: '采购' },
    { G_IDX: 'CW', G_DESC: '财务' },
  ],
  total: 2,
}

function renderPage() {
  vi.mocked(useAuth).mockReturnValue({
    bootstrap: { user: { id: 'admin', username: 'admin', displayName: 'Demo User', employeeId: 'E001', avatarText: 'LW', avatarUrl: null, roleName: '系统管理员', organization: { id: 'o', name: 'O' } }, permissions: [], navigation: [] },
    loading: false,
    login: vi.fn(),
    logout: vi.fn(),
    hasPermission: () => true,
  })
  return renderWithProviders(
      <MemoryRouter initialEntries={['/admin/users']}>
        <Routes>
          <Route path="/admin/users" element={<UserAdminPage />} />
          <Route path="/admin/users/:userId/rights" element={<div>USER_RIGHTS_PAGE</div>} />
          <Route path="/admin/users/:userId/report-rights" element={<div>USER_REPORT_RIGHTS_PAGE</div>} />
        </Routes>
      </MemoryRouter>
)
}

async function loaded() {
  await waitFor(() => expect(screen.queryByText('正在加载用户…')).not.toBeInTheDocument())
}

function rowOf(userId: string) {
  return screen.getByText(userId).closest('tr')!
}

function mockGet(path: string) {
  if (path === '/admin/groups') return Promise.resolve(allGroups)
  if (path.startsWith('/admin/users/') && path.endsWith('/groups')) return Promise.resolve([])
  // 真实 API 每次返回新对象；副本保证引用变化（结构共享关闭后 effect 依赖引用重跑）
  return Promise.resolve(structuredClone(usersPage))
}

describe('UserAdminPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation((path: string) => mockGet(path))
    apiClientMock.put.mockResolvedValue(undefined)
    apiClientMock.post.mockImplementation((path: string, body?: { sourceKey?: string }) => {
      if (path === '/chooser/query') {
        if (body?.sourceKey === 'rights-admin.groups') return Promise.resolve(groupChooserData)
        return Promise.resolve(employeeChooserData)
      }
      return Promise.resolve(undefined)
    })
    vi.stubGlobal('confirm', vi.fn(() => true))
  })

  afterEach(() => {
    vi.clearAllMocks()
    vi.unstubAllGlobals()
  })

  it('加载失败显示错误', async () => {
    apiClientMock.get.mockRejectedValue(new ApiError(500, { code: 'X', message: '用户列表挂了' }))
    renderPage()
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('用户列表挂了'))
  })

  it('渲染用户列表（全高铺满 + 首列选择 + 表头排序 + 所属组列 + 操作列）', async () => {
    const { container } = renderPage()
    await loaded()
    expect(screen.getByText('Demo User')).toBeInTheDocument()
    expect(screen.getByText(/共 2 个账号/)).toBeInTheDocument()
    expect(container.querySelector('.erp-full-list-page')).not.toBeNull()
    expect(screen.getByLabelText('选择当前页')).toBeInTheDocument()
    expect(screen.getAllByLabelText('选择此行')).toHaveLength(2)
    expect(screen.getByLabelText('表头操作用户名')).toBeInTheDocument()
    // 所属组列一目了然
    expect(within(rowOf('viewer')).getByText('采购')).toBeInTheDocument()
    expect(within(rowOf('viewer')).getByText('财务')).toBeInTheDocument()
    const actions = within(rowOf('viewer'))
    for (const name of ['设置密码', '权限', '报表权限', '所属组', '启用']) {
      expect(actions.getByRole('button', { name })).toBeInTheDocument()
    }
  })

  it('点击行内单选选中', async () => {
    renderPage()
    await loaded()
    fireEvent.click(rowOf('viewer'))
    const boxes = screen.getAllByLabelText('选择此行')
    expect(boxes[0]).not.toBeChecked()
    expect(boxes[1]).toBeChecked()
  })

  it('刷新后列表不回空', async () => {
    renderPage()
    await loaded()
    expect(screen.getAllByLabelText('选择此行')).toHaveLength(2)
    fireEvent.click(screen.getByRole('button', { name: '刷新' }))
    await waitFor(() => expect(screen.getAllByLabelText('选择此行')).toHaveLength(2))
    expect(screen.getByText('Demo User')).toBeInTheDocument()
    expect(screen.queryByText('没有找到用户')).not.toBeInTheDocument()
  })

  it('当前登录账号的停用按钮禁用', async () => {
    renderPage()
    await loaded()
    expect(within(rowOf('admin')).getByRole('button', { name: '停用' })).toBeDisabled()
  })

  it('停用用户需要确认并调用状态接口', async () => {
    renderPage()
    await loaded()
    fireEvent.click(within(rowOf('viewer')).getByRole('button', { name: '启用' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith(
      '/admin/users/viewer/status',
      { isActive: true },
    ))
  })

  it('设置密码成功调用接口并关闭弹窗', async () => {
    renderPage()
    await loaded()
    fireEvent.click(within(rowOf('viewer')).getByRole('button', { name: '设置密码' }))
    const dialog = screen.getByRole('dialog')
    fireEvent.change(within(dialog).getByLabelText('新密码'), { target: { value: 'long-enough-1' } })
    fireEvent.change(within(dialog).getByLabelText('确认新密码'), { target: { value: 'long-enough-1' } })
    fireEvent.click(within(dialog).getByRole('button', { name: '保存密码' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith(
      '/admin/users/viewer/password',
      { newPassword: 'long-enough-1' },
    ))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('操作列导航到权限完整子页面', async () => {
    renderPage()
    await loaded()
    fireEvent.click(within(rowOf('viewer')).getByRole('button', { name: '权限' }))
    expect(await screen.findByText('USER_RIGHTS_PAGE')).toBeInTheDocument()
  })

  it('操作列导航到报表权限完整子页面', async () => {
    renderPage()
    await loaded()
    fireEvent.click(within(rowOf('viewer')).getByRole('button', { name: '报表权限' }))
    expect(await screen.findByText('USER_REPORT_RIGHTS_PAGE')).toBeInTheDocument()
  })

  it('所属组弹窗选择并全量保存', async () => {
    renderPage()
    await loaded()
    fireEvent.click(within(rowOf('viewer')).getByRole('button', { name: '所属组' }))
    await waitFor(() => expect(apiClientMock.get).toHaveBeenCalledWith('/admin/users/viewer/groups'))
    const dialogs = await screen.findAllByRole('dialog')
    const chooser = dialogs[dialogs.length - 1]
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith('/chooser/query', expect.objectContaining({ sourceKey: 'rights-admin.groups' })))
    await waitFor(() => expect(within(chooser).getByText('采购')).toBeInTheDocument())
    fireEvent.click(within(chooser).getByText('采购'))
    await waitFor(() => expect(within(chooser).getByRole('button', { name: '确认' })).toBeEnabled())
    fireEvent.click(within(chooser).getByRole('button', { name: '确认' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith(
      '/admin/users/viewer/groups',
      { ids: ['CG'] },
    ))
  })

  it('新增用户：选择员工开户并 POST', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '新增用户' }))
    const dialog = screen.getByRole('dialog')
    fireEvent.change(within(dialog).getByLabelText('用户名'), { target: { value: 'newuser' } })
    fireEvent.click(within(dialog).getByRole('button', { name: '选择员工' }))
    const dialogs = await screen.findAllByRole('dialog')
    const chooser = dialogs[dialogs.length - 1]
    await waitFor(() => expect(within(chooser).queryByText('正在加载…')).not.toBeInTheDocument())
    // 唤起统一选择器（sourceKey），不再走自建 /admin/users/employees
    expect(apiClientMock.post).toHaveBeenCalledWith('/chooser/query', expect.objectContaining({
      sourceKey: 'user-admin.employees',
      page: 1,
      pageSize: 50,
    }))
    expect(apiClientMock.get).not.toHaveBeenCalledWith(expect.stringContaining('/admin/users/employees'), expect.anything())
    // 点击行即选中（单选语义），确认后回填员工
    fireEvent.click(within(chooser).getByText('新员工'))
    await waitFor(() => expect(within(chooser).getByRole('button', { name: '确认' })).toBeEnabled())
    fireEvent.click(within(chooser).getByRole('button', { name: '确认' }))
    expect(within(dialog).getByLabelText('已选员工')).toHaveValue('E999（新员工 / 新部门）')
    fireEvent.change(within(dialog).getByLabelText('初始密码'), { target: { value: 'long-enough-1' } })
    fireEvent.change(within(dialog).getByLabelText('确认密码'), { target: { value: 'long-enough-1' } })
    fireEvent.click(within(dialog).getByRole('button', { name: '开户' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/admin/users',
      { userId: 'newuser', employeeId: 'E999', password: 'long-enough-1', groupId: null },
    ))
  })
})
