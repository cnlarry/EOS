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

// 环境负载下（并跑 web dev/API/playwright）弹窗交互用例 5s 默认超时偶发超时，
// 单独跑 10/10 通过；放宽到 15s 消除偶发（同仓库其它表单页测试同此配置风格）。
vi.setConfig({ testTimeout: 15000 })

const tables = Array.from({ length: 20 }, (_, index) => ({
  tableId: `TABLE_${String(index).padStart(2, '0')}`,
  description: `表${index}`,
  kind: index % 2 === 0 ? 'P' : 'S',
  type: 'M',
  fieldCount: 10 + index,
  unmanagedCount: index === 0 ? 3 : 0,
  orphanCount: index === 1 ? 2 : 0,
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
    vi.unstubAllGlobals()
  })

  it('加载失败显示错误', async () => {
    apiClientMock.get.mockRejectedValue(new ApiError(500, { code: 'X', message: '表挂了' }))
    renderPage()
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('表挂了'))
  })

  it('渲染数据表并客户端过滤（无分页，一次性加载）', async () => {
    renderPage()
    await loaded()
    expect(screen.getByText('TABLE_00')).toBeInTheDocument()
    expect(screen.queryByText('共 20 条，第 1/2 页')).not.toBeInTheDocument()
    fireEvent.change(screen.getByRole('searchbox', { name: '搜索数据表' }), { target: { value: 'TABLE_19' } })
    await waitFor(() => expect(screen.queryByText('TABLE_00')).not.toBeInTheDocument())
    expect(screen.getByText('TABLE_19')).toBeInTheDocument()
  })

  it('管理字段按钮进入该表的字段维护页', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getAllByRole('button', { name: '管理字段' })[0])
    expect(screen.getByText('FIELDS_PAGE')).toBeInTheDocument()
  })

  it('编辑按钮打开表信息弹窗并提交', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path === '/admin/tables') return tables
      if (path === '/admin/tables/TABLE_00') {
        return {
          tableId: 'TABLE_00', description: '表0', kind: 'P', type: 'M', remark: null,
          fkTable1: null, fkTable2: null, fkTable3: null, fkTable4: null, fkTable5: null,
          queryRelation: 'COMPANY', defaultCondition: null, defaultVerify: null,
          canImport: false, lastUpdatedBy: 'admin', lastUpdatedAt: '2026-08-01T00:00:00Z',
        }
      }
      throw new Error(`unexpected GET ${path}`)
    })
    renderPage()
    await loaded()
    fireEvent.click(screen.getAllByRole('button', { name: /^编辑/ })[0])
    await waitFor(() => expect(screen.getByText('数据表信息（TABLE_00）')).toBeInTheDocument())
    await waitFor(() => expect(screen.getByDisplayValue('表0')).toBeInTheDocument())
    fireEvent.change(screen.getByDisplayValue('表0'), { target: { value: '表0改' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith(
      '/admin/tables/TABLE_00',
      expect.objectContaining({ table: expect.objectContaining({ description: '表0改' }), original: expect.anything() }),
    ))
  })

  it('首列渲染单选列且行点击选中', async () => {
    renderPage()
    await loaded()
    const radios = screen.getAllByRole('radio', { name: '选择此行' })
    expect(radios).toHaveLength(20)
    fireEvent.click(radios[3])
    expect(radios[3]).toBeChecked()
    expect(radios[0]).not.toBeChecked()
  })

  it('按性质筛选后仅显示对应行', async () => {
    renderPage()
    await loaded()
    fireEvent.change(screen.getByRole('combobox', { name: '按性质筛选' }), { target: { value: 'S' } })
    await waitFor(() => expect(screen.getByText('TABLE_01')).toBeInTheDocument())
    expect(screen.queryByText('TABLE_00')).not.toBeInTheDocument()
  })

  it('显示未管理与幽灵字段计数', async () => {
    renderPage()
    await loaded()
    expect(screen.getByText('3')).toBeInTheDocument()
    expect(screen.getByText('2')).toBeInTheDocument()
    expect(document.querySelector('.badge.bg-green-lt')).not.toBeNull()
  })

  it('新增按钮打开表编辑弹窗并提交', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '新增' }))
    await waitFor(() => expect(screen.getByText('新增数据表元数据')).toBeInTheDocument())
    const dialog = screen.getByRole('dialog')
    const inputs = Array.from(dialog.querySelectorAll<HTMLInputElement>('input.form-control'))
    fireEvent.change(inputs[0], { target: { value: 'NEW_TABLE' } })
    fireEvent.change(inputs[1], { target: { value: '新表' } })
    const save = screen.getByRole('button', { name: '保存' })
    await waitFor(() => expect(save).toBeEnabled())
    fireEvent.click(save)
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/admin/tables',
      expect.objectContaining({ tableId: 'NEW_TABLE', table: expect.objectContaining({ description: '新表' }) }),
    ))
  })

  it('删除需要确认并调用删除接口', async () => {
    vi.stubGlobal('confirm', vi.fn(() => true))
    renderPage()
    await loaded()
    fireEvent.click(screen.getAllByRole('button', { name: /^删除/ })[0])
    await waitFor(() => expect(apiClientMock.delete).toHaveBeenCalledWith('/admin/tables/TABLE_00'))
  })

  it('删除取消时不调用接口', async () => {
    vi.stubGlobal('confirm', vi.fn(() => false))
    renderPage()
    await loaded()
    fireEvent.click(screen.getAllByRole('button', { name: /^删除/ })[0])
    expect(apiClientMock.delete).not.toHaveBeenCalled()
  })
})
