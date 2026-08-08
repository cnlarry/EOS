import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../types/api'
import { TableAdminPage } from './TableAdminPage'

const apiClientMock = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  postFile: vi.fn(),
}))

vi.mock('../../services/api', () => ({ apiClient: apiClientMock }))

const tables = Array.from({ length: 20 }, (_, index) => ({
  tableId: `TABLE_${String(index).padStart(2, '0')}`,
  description: `表${index}`,
  kind: '主表',
  type: 'M',
}))

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/admin/tables']}>
        <Routes>
          <Route path="/admin/tables" element={<TableAdminPage />} />
          <Route path="/admin/tables/:tableId/fields" element={<div>FIELDS_PAGE</div>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

async function loaded() {
  await waitFor(() => expect(screen.queryByText('正在加载数据表…')).not.toBeInTheDocument())
}

describe('TableAdminPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockResolvedValue(tables)
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('加载失败显示错误', async () => {
    apiClientMock.get.mockRejectedValue(new ApiError(500, { code: 'X', message: '表挂了' }))
    renderPage()
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('表挂了'))
  })

  it('渲染数据表并客户端过滤', async () => {
    renderPage()
    await loaded()
    expect(screen.getByText('TABLE_00')).toBeInTheDocument()
    expect(screen.getByText('共 20 条，第 1/2 页')).toBeInTheDocument()
    fireEvent.change(screen.getByRole('searchbox', { name: '搜索数据表' }), { target: { value: 'TABLE_19' } })
    await waitFor(() => expect(screen.getByText('TABLE_19')).toBeInTheDocument())
    expect(screen.queryByText('TABLE_00')).not.toBeInTheDocument()
  })

  it('管理字段跳转到字段维护页', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getAllByRole('button', { name: '管理字段' })[0])
    expect(screen.getByText('FIELDS_PAGE')).toBeInTheDocument()
  })

  it('刷新触发重新查询', async () => {
    renderPage()
    await loaded()
    const before = apiClientMock.get.mock.calls.length
    fireEvent.click(screen.getByRole('button', { name: '刷新' }))
    await waitFor(() => expect(apiClientMock.get.mock.calls.length).toBeGreaterThan(before))
  })
})
