import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { UserGroupAdminPage } from './UserGroupAdminPage'

const apiClientMock = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  postFile: vi.fn(),
}))

vi.mock('../../services/api', () => ({ apiClient: apiClientMock }))

const groups = [
  { groupId: 'CG', groupDescription: '采购', memberCount: 2 },
  { groupId: 'CW', groupDescription: '财务', memberCount: 0 },
]
const usersPage = { items: [{ userId: 'puser01', employeeName: '李示例' }, { userId: 'puser02', employeeName: '王示例' }], total: 2, page: 1, pageSize: 100 }
const members = [{ userId: 'puser01', employeeId: 'puser01', employeeName: '李示例' }]

function mockGet(path: string) {
  if (path === '/admin/groups') return Promise.resolve(groups)
  if (path === '/admin/users' || path.startsWith('/admin/users?')) return Promise.resolve(usersPage)
  if (path === '/admin/groups/CG/members') return Promise.resolve(members)
  if (path === '/admin/groups/CG/rights') return Promise.resolve([])
  if (path === '/admin/groups/CG/report-rights') return Promise.resolve([])
  return Promise.resolve([])
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={queryClient}><UserGroupAdminPage /></QueryClientProvider>)
}

describe('UserGroupAdminPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation((path: string) => mockGet(path))
    apiClientMock.put.mockResolvedValue(undefined)
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('渲染用户组列表', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText('CG')).toBeInTheDocument())
    expect(screen.getByText('采购')).toBeInTheDocument()
    expect(screen.getByText('财务')).toBeInTheDocument()
  })

  it('打开组权限矩阵并保存', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText('CG')).toBeInTheDocument())
    const row = screen.getByText('CG').closest('tr')!
    fireEvent.click(within(row).getByRole('button', { name: '组权限' }))
    await waitFor(() => expect(screen.getByRole('dialog')).toBeInTheDocument())
    expect(apiClientMock.get).toHaveBeenCalledWith('/admin/groups/CG/rights')
  })

  it('打开成员选择器并全量保存', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText('CG')).toBeInTheDocument())
    const row = screen.getByText('CG').closest('tr')!
    fireEvent.click(within(row).getByRole('button', { name: '成员' }))
    await waitFor(() => expect(screen.getByText('李示例')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith('/admin/groups/CG/members', { ids: ['puser01'] }))
  })
})
