import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../types/api'
import { useAuth } from '../auth/authContext'
import { UserAdminPage } from './UserAdminPage'

const apiClientMock = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  postFile: vi.fn(),
}))

vi.mock('../../services/api', () => ({ apiClient: apiClientMock }))
vi.mock('../auth/authContext', () => ({ useAuth: vi.fn() }))

const usersPage = {
  items: [
    { userId: 'admin', employeeId: 'E001', employeeName: 'Demo User', departmentId: 'D1', departmentName: '信息部', companyId: 'C1', groupId: 'G1', isActive: true, hasPassword: true, lastUpdatedBy: 'admin', lastUpdatedAt: '2026-08-01T00:00:00Z' },
    { userId: 'viewer', employeeId: 'E002', employeeName: '只读用户', departmentId: 'D2', departmentName: '财务部', companyId: 'C1', groupId: 'G2', isActive: false, hasPassword: false, lastUpdatedBy: 'admin', lastUpdatedAt: null },
  ],
  page: 1,
  pageSize: 10,
  total: 2,
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  vi.mocked(useAuth).mockReturnValue({
    bootstrap: { user: { id: 'admin', username: 'admin', displayName: 'Demo User', employeeId: 'E001', avatarText: 'LW', avatarUrl: null, roleName: '系统管理员', organization: { id: 'o', name: 'O' } }, permissions: [], navigation: [] },
    loading: false,
    login: vi.fn(),
    logout: vi.fn(),
    hasPermission: () => true,
  })
  return render(<QueryClientProvider client={queryClient}><UserAdminPage /></QueryClientProvider>)
}

async function loaded() {
  await waitFor(() => expect(screen.queryByText('正在加载用户…')).not.toBeInTheDocument())
}

function mockGet(path: string) {
  if (path === '/admin/groups') return Promise.resolve([])
  if (path.startsWith('/admin/users/') && path.endsWith('/groups')) return Promise.resolve([])
  if (path.startsWith('/admin/users/') && path.endsWith('/rights')) return Promise.resolve([])
  if (path.startsWith('/admin/users/') && path.endsWith('/report-rights')) return Promise.resolve([])
  return Promise.resolve(usersPage)
}

describe('UserAdminPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation((path: string) => mockGet(path))
    apiClientMock.put.mockResolvedValue(undefined)
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

  it('渲染用户列表与状态徽标', async () => {
    renderPage()
    await loaded()
    expect(screen.getByText('admin')).toBeInTheDocument()
    expect(screen.getByText('E001')).toBeInTheDocument()
    expect(screen.getByText('Demo User')).toBeInTheDocument()
    expect(screen.getAllByText('启用').length).toBeGreaterThan(0)
    expect(screen.getAllByText('停用').length).toBeGreaterThan(0)
    expect(screen.getByText('已设置')).toBeInTheDocument()
    expect(screen.getByText('未设置')).toBeInTheDocument()
    expect(screen.getByText(/共 2 个账号/)).toBeInTheDocument()
  })

  it('当前登录账号的停用按钮禁用', async () => {
    renderPage()
    await loaded()
    const adminRow = screen.getByText('admin').closest('tr')!
    expect(within(adminRow).getByRole('button', { name: '停用' })).toBeDisabled()
  })

  it('停用用户需要确认并调用状态接口', async () => {
    renderPage()
    await loaded()
    const viewerRow = screen.getByText('viewer').closest('tr')!
    fireEvent.click(within(viewerRow).getByRole('button', { name: '启用' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith(
      '/admin/users/viewer/status',
      { isActive: true },
    ))
  })

  it('停用取消时不调用接口', async () => {
    vi.stubGlobal('confirm', vi.fn(() => false))
    renderPage()
    await loaded()
    const viewerRow = screen.getByText('viewer').closest('tr')!
    fireEvent.click(within(viewerRow).getByRole('button', { name: '启用' }))
    expect(apiClientMock.put).not.toHaveBeenCalled()
  })

  it('设置密码：校验不通过禁用保存', async () => {
    renderPage()
    await loaded()
    const viewerRow = screen.getByText('viewer').closest('tr')!
    fireEvent.click(within(viewerRow).getByRole('button', { name: '设置密码' }))
    const dialog = screen.getByRole('dialog')
    const save = within(dialog).getByRole('button', { name: '保存密码' })
    expect(save).toBeDisabled()
    fireEvent.change(within(dialog).getByLabelText('新密码'), { target: { value: 'short' } })
    fireEvent.change(within(dialog).getByLabelText('确认新密码'), { target: { value: 'short' } })
    expect(save).toBeDisabled()
  })

  it('设置密码：密码不一致提示并禁用保存', async () => {
    renderPage()
    await loaded()
    const viewerRow = screen.getByText('viewer').closest('tr')!
    fireEvent.click(within(viewerRow).getByRole('button', { name: '设置密码' }))
    const dialog = screen.getByRole('dialog')
    fireEvent.change(within(dialog).getByLabelText('新密码'), { target: { value: 'long-enough-1' } })
    fireEvent.change(within(dialog).getByLabelText('确认新密码'), { target: { value: 'different-1' } })
    expect(within(dialog).getByText('两次输入的密码不一致。')).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: '保存密码' })).toBeDisabled()
  })

  it('设置密码成功调用接口并关闭弹窗', async () => {
    renderPage()
    await loaded()
    const viewerRow = screen.getByText('viewer').closest('tr')!
    fireEvent.click(within(viewerRow).getByRole('button', { name: '设置密码' }))
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

  it('设置密码失败展示错误', async () => {
    apiClientMock.put.mockRejectedValue(new ApiError(400, { code: 'WEAK', message: '密码强度不足。' }))
    renderPage()
    await loaded()
    const viewerRow = screen.getByText('viewer').closest('tr')!
    fireEvent.click(within(viewerRow).getByRole('button', { name: '设置密码' }))
    const dialog = screen.getByRole('dialog')
    fireEvent.change(within(dialog).getByLabelText('新密码'), { target: { value: 'long-enough-1' } })
    fireEvent.change(within(dialog).getByLabelText('确认新密码'), { target: { value: 'long-enough-1' } })
    fireEvent.click(within(dialog).getByRole('button', { name: '保存密码' }))
    await waitFor(() => expect(within(dialog).getByRole('alert')).toHaveTextContent('密码强度不足。'))
  })

  it('打开模块权限矩阵', async () => {
    renderPage()
    await loaded()
    const viewerRow = screen.getByText('viewer').closest('tr')!
    fireEvent.click(within(viewerRow).getByRole('button', { name: '权限' }))
    await waitFor(() => expect(apiClientMock.get).toHaveBeenCalledWith('/admin/users/viewer/rights'))
    expect(screen.getByRole('dialog')).toBeInTheDocument()
  })

  it('打开所属组选择器并保存', async () => {
    renderPage()
    await loaded()
    const viewerRow = screen.getByText('viewer').closest('tr')!
    fireEvent.click(within(viewerRow).getByRole('button', { name: '所属组' }))
    await waitFor(() => expect(apiClientMock.get).toHaveBeenCalledWith('/admin/users/viewer/groups'))
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith('/admin/users/viewer/groups', { ids: [] }))
  })
})
