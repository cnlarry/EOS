import { apiClientMock } from '../../test/apiMock'
import { screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter } from 'react-router-dom'
import { useAuth } from '../auth/authContext'
import { DashboardPage } from './DashboardPage'
import { renderWithProviders } from '../../test/renderWithProviders'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))
vi.mock('../auth/authContext', () => ({ useAuth: vi.fn() }))

const bootstrap = {
  user: {
    id: 'admin', username: 'admin', displayName: '系统管理员', employeeId: 'E001',
    avatarText: '管', avatarUrl: null, roleName: '系统管理员', organization: { id: 'o', name: '总公司' },
  },
  permissions: ['legacy-module.2102.read'],
  navigation: [
    {
      id: 'sales', label: '销售管理', icon: 'sales',
      children: [
        { id: 'so', label: '销售订单', route: '/workbench/1405', icon: 'sales', moduleId: 1405 },
        { id: 'quot', label: '报价单', route: '/workbench/1404', icon: 'sales', moduleId: 1404 },
      ],
    },
  ],
}

const myTasks = {
  tasks: [{ moduleId: 1405, title: '销售订单', pending: 3 }, { moduleId: 1404, title: '报价单', pending: 0 }],
  flowTasks: [{ myTaskId: 1 }],
  engineEnabled: true,
  note: '',
}

function renderPage() {
  vi.mocked(useAuth).mockReturnValue({
    bootstrap,
    loading: false,
    login: vi.fn(),
    logout: vi.fn(),
    hasPermission: (permission: string) => bootstrap.permissions.includes(permission),
  })
  return renderWithProviders(
    <MemoryRouter>
      <DashboardPage />
    </MemoryRouter>,
  )
}

describe('DashboardPage', () => {
  beforeEach(() => {
    localStorage.clear()
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path === '/workflow/my-tasks') return myTasks
      if (path === '/workflow/my-started') return { rows: [{ wfId: 1, moduleId: 1405, title: '销售订单', keyValue: 'A', keyValueDesc: 'SO-001', startDate: null, step: '1', stepDesc: '采购主管审批' }] }
      return undefined
    })
  })

  afterEach(() => {
    vi.mocked(useAuth).mockReset()
  })

  it('渲染真实待办指标', async () => {
    renderPage()
    expect(screen.queryByText(/你好，系统管理员/)).not.toBeInTheDocument()
    await waitFor(() => expect(screen.queryByText('…')).not.toBeInTheDocument())
    expect(screen.getByText('待批核单据')).toBeInTheDocument()
    expect(screen.getByText('流程审批待办')).toBeInTheDocument()
    expect(screen.getByText('我发起的流程')).toBeInTheDocument()
    expect(screen.getAllByText('3').length).toBeGreaterThan(0)
    expect(screen.getAllByText('1').length).toBeGreaterThan(0)
  })

  it('渲染待批核明细表格（仅显示待批核 > 0）', async () => {
    renderPage()
    await waitFor(() => expect(screen.getAllByText('销售订单').length).toBeGreaterThan(0))
    expect(screen.queryByText('待批核 1 项')).toBeNull()
    expect(screen.getByText('待批核单据 1 项')).toBeInTheDocument()
    expect(screen.getAllByText('去处理').length).toBeGreaterThan(0)
  })

  it('无 2102 权限时不展示待办明细', async () => {
    const withoutTasks = { ...bootstrap, permissions: [] }
    vi.mocked(useAuth).mockReturnValue({
      bootstrap: withoutTasks,
      loading: false,
      login: vi.fn(),
      logout: vi.fn(),
      hasPermission: () => false,
    })
    renderWithProviders(
      <MemoryRouter>
        <DashboardPage />
      </MemoryRouter>,
    )
    expect(screen.getAllByText('无「我的任务」模块权限').length).toBe(3)
    expect(screen.getByText('待批核单据 — 项')).toBeInTheDocument()
  })
})

