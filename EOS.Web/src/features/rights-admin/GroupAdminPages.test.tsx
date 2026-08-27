import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { GroupMembersPage } from './GroupAdminPages'

const apiClientMock = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  postFile: vi.fn(),
}))

vi.mock('../../services/api', () => ({ apiClient: apiClientMock }))

const groups = [
  { groupId: 'CG', groupDescription: '采购', memberCount: 1, remark: null },
]
const members = [
  { userId: 'puser01', employeeId: 'puser01', employeeName: '李示例' },
]
const usersPage = {
  items: [
    { userId: 'puser01', employeeName: '李示例' },
    { userId: 'puser02', employeeName: '王示例' },
    { userId: 'pur03', employeeName: '李四' },
  ],
  total: 3,
  page: 1,
  pageSize: 50,
}

function mockGet(path: string) {
  if (path === '/admin/groups') return Promise.resolve(groups)
  if (path === '/admin/groups/CG/members') return Promise.resolve(members)
  if (path.startsWith('/admin/users')) return Promise.resolve(usersPage)
  return Promise.resolve([])
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/admin/groups/CG/members']}>
        <Routes>
          <Route path="/admin/groups/:groupId/members" element={<GroupMembersPage />} />
          <Route path="/admin/groups" element={<div>GROUPS_LIST</div>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('GroupMembersPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation((path: string) => mockGet(path))
    apiClientMock.put.mockResolvedValue(undefined)
    vi.stubGlobal('confirm', vi.fn(() => true))
  })

  afterEach(() => {
    vi.clearAllMocks()
    vi.unstubAllGlobals()
  })

  it('成员列表以电子表格渲染（首列选择 + 用户ID/姓名）', async () => {
    const { container } = renderPage()
    expect(await screen.findByText('李示例')).toBeInTheDocument()
    expect(screen.getByText('puser01')).toBeInTheDocument()
    expect(container.querySelector('.erp-full-list-page')).not.toBeNull()
    expect(screen.getByLabelText('选择当前页')).toBeInTheDocument()
    expect(screen.getAllByLabelText('选择此行')).toHaveLength(1)
    expect(screen.getByRole('button', { name: '添加成员' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '移除所选' })).toBeDisabled()
  })

  it('选择器添加成员：搜索排除当前成员，确认后进入草稿并保存全量替换', async () => {
    renderPage()
    await screen.findByText('李示例')
    fireEvent.click(screen.getByRole('button', { name: '添加成员' }))
    expect(await screen.findByRole('dialog')).toBeInTheDocument()
    // 已选成员 puser01 不在可添加列表
    expect(within(screen.getByRole('dialog')).queryByText('李示例')).not.toBeInTheDocument()
    fireEvent.click(within(screen.getByRole('dialog')).getByText('王示例'))
    fireEvent.click(screen.getByRole('button', { name: '确认添加' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(screen.getByText('王示例')).toBeInTheDocument()
    // 保存按组全量替换
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith(
      '/admin/groups/CG/members',
      { ids: ['puser01', 'puser02'] },
    ))
  })

  it('移除所选成员需确认并更新草稿', async () => {
    renderPage()
    await screen.findByText('李示例')
    fireEvent.click(screen.getByLabelText('选择此行'))
    fireEvent.click(screen.getByRole('button', { name: '移除所选' }))
    expect(window.confirm).toHaveBeenCalled()
    await waitFor(() => expect(screen.queryByText('李示例')).not.toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith(
      '/admin/groups/CG/members',
      { ids: [] },
    ))
  })
})
