import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { EffectiveModuleRights, ModuleRightsRow } from './types'
import { RightsMatrix } from './RightsMatrix'

const apiClientMock = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  postFile: vi.fn(),
}))

vi.mock('../../services/api', () => ({ apiClient: apiClientMock }))

const noneEffective: EffectiveModuleRights = {
  source: 'none', canBrowse: false, execTag: 'A',
  addNew: false, edit: false, delete: false, approve: false, deapprove: false, report: false,
  cost: false, setup: false, secrecy: false, endCase: false, unEndCase: false,
  other1: false, other2: false, other3: false, other4: false,
  fileView: false, fileUpda: false, fileEdit: false, fileDele: false,
  denyViewMaster: [], denyViewDetail: [], denyNewMaster: [], denyNewDetail: [], denyModiMaster: [], denyModiDetail: [],
  dataFilter: '',
}

function makeRow(moduleId: number, title: string, parentId = 0, overrides: Partial<ModuleRightsRow> = {}): ModuleRightsRow {
  return {
    moduleId, title, groupPath: '', parentId, rootId: moduleId, sortIndex: 1,
    execTag: null, addNew: false, edit: false, delete: false, approve: false, deapprove: false, report: false,
    cost: false, setup: false, secrecy: false, endCase: false, unEndCase: false,
    other1: false, other2: false, other3: false, other4: false,
    fileView: false, fileUpda: false, fileEdit: false, fileDele: false,
    denyViewMaster: '', denyViewDetail: '', denyNewMaster: '', denyNewDetail: '', denyModiMaster: '', denyModiDetail: '',
    dataFilter: '', hasPersonal: false, effective: noneEffective,
    ...overrides,
  }
}

const matrixRows = [
  makeRow(2306, '用户权限设定'),
  makeRow(1206, '成品资料', 0, { hasPersonal: true, execTag: 'B', addNew: true, effective: { ...noneEffective, source: 'personal', canBrowse: true, execTag: 'B', addNew: true } }),
]
const usersPage = { items: [{ userId: 'viewer', employeeName: '只读用户' }, { userId: 'other', employeeName: '其它用户' }], total: 2, page: 1, pageSize: 100 }

function mockGet(path: string) {
  if (path === '/admin/users') return Promise.resolve(usersPage)
  if (path.startsWith('/admin/users/') && path.endsWith('/rights')) return Promise.resolve(matrixRows)
  return Promise.resolve(matrixRows)
}

function renderMatrix() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <RightsMatrix open mode="user" targetId="viewer" onClose={vi.fn()} />
    </QueryClientProvider>,
  )
}

describe('RightsMatrix', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation((path: string) => mockGet(path))
    apiClientMock.put.mockResolvedValue(undefined)
    vi.stubGlobal('confirm', vi.fn(() => true))
  })

  afterEach(() => {
    vi.clearAllMocks()
    vi.unstubAllGlobals()
  })

  it('渲染模块树与来源标记', async () => {
    renderMatrix()
    await waitFor(() => expect(screen.getAllByText('用户权限设定').length).toBeGreaterThan(0))
    expect(screen.getByText('成品资料')).toBeInTheDocument()
    expect(screen.getByText('个人')).toBeInTheDocument()
    expect(screen.getAllByText('无').length).toBeGreaterThan(0)
    expect(screen.getByLabelText('执行级别 EXEC_TAG（A=禁止执行）')).toBeInTheDocument()
  })

  it('编辑后批量保存提交脏行', async () => {
    renderMatrix()
    await waitFor(() => expect(screen.getAllByText('用户权限设定').length).toBeGreaterThan(0))
    fireEvent.click(screen.getAllByText('用户权限设定')[0])
    const addNew = screen.getByLabelText('新增')
    fireEvent.click(addNew)
    fireEvent.click(screen.getByRole('button', { name: '批量保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalled())
    const [url, body] = apiClientMock.put.mock.calls[0]
    expect(url).toBe('/admin/users/viewer/rights')
    expect(body.items).toHaveLength(1)
    expect(body.items[0]).toMatchObject({ moduleId: 2306, addNew: true })
  })

  it('清空个人权限需确认并提交空行（回退组权限）', async () => {
    renderMatrix()
    await waitFor(() => expect(screen.getAllByText('用户权限设定').length).toBeGreaterThan(0))
    fireEvent.click(screen.getByRole('button', { name: /清空个人权限/ }))
    await waitFor(() => expect(screen.getByText(/已把 2 个模块标记为/)).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '批量保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalled())
    const [url, body] = apiClientMock.put.mock.calls[0]
    expect(url).toBe('/admin/users/viewer/rights')
    expect(body.items).toHaveLength(2)
    expect(body.items.every((item: { addNew: boolean; execTag: string | null }) => !item.addNew && (item.execTag === null || item.execTag === 'A'))).toBe(true)
  })

  it('复制权限：选择来源后调用来源矩阵并标记脏行', async () => {
    renderMatrix()
    await waitFor(() => expect(screen.getAllByText('用户权限设定').length).toBeGreaterThan(0))
    fireEvent.click(screen.getByRole('button', { name: /复制权限/ }))
    const select = await screen.findByLabelText('复制来源')
    await waitFor(() => expect(select.querySelectorAll('option').length).toBeGreaterThan(1))
    fireEvent.change(select, { target: { value: 'other' } })
    apiClientMock.get.mockImplementation((path: string) => {
      if (path === '/admin/users/other/rights') return Promise.resolve([makeRow(2306, '用户权限设定', 0, { execTag: 'Z', addNew: true, fileDele: true })])
      return mockGet(path)
    })
    fireEvent.click(screen.getByRole('button', { name: '复制' }))
    await waitFor(() => expect(screen.getByText(/已从 other 复制 1 个模块/)).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '批量保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalled())
    const body = apiClientMock.put.mock.calls[0][1]
    expect(body.items[0]).toMatchObject({ moduleId: 2306, execTag: 'Z', addNew: true, fileDele: true })
  })
})
