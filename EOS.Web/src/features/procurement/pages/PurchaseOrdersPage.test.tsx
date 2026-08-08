import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../../types/api'
import { useAuth } from '../../auth/authContext'
import type { PurchaseOrderSummary } from '../types/purchaseOrder'
import { PurchaseOrdersPage } from './PurchaseOrdersPage'

const apiClientMock = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  postFile: vi.fn(),
}))

vi.mock('../../../services/api', () => ({ apiClient: apiClientMock }))
vi.mock('../../auth/authContext', () => ({ useAuth: vi.fn() }))

const orders: PurchaseOrderSummary[] = [
  {
    id: '100018', number: 'PO-20260802-018', supplierName: 'recv_3包装材料有限公司', purchaseDate: '2026-08-02',
    deliveryDate: '2026-08-12', totalAmount: '48600.00', currency: 'CNY', status: 'pending', buyerName: '赵示例',
    lines: [{ id: 'L1', productCode: 'MAT-1', productName: '五层瓦楞纸箱', specification: '600×400', unit: '只', quantity: '3000', unitPrice: '12.00', taxRate: '13', lineAmount: '36000.00' }],
  },
  {
    id: '100006', number: 'PO-20260801-006', supplierName: '苏州精工设备有限公司', purchaseDate: '2026-08-01',
    deliveryDate: '2026-08-18', totalAmount: '126800.00', currency: 'CNY', status: 'draft', buyerName: 'Demo User', lines: [],
  },
]

const defaultData = { items: orders, page: 1, pageSize: 5, total: orders.length }

function mockOrders(data: unknown = defaultData) {
  apiClientMock.get.mockResolvedValue(data)
}

function renderPage(initialEntry = '/procurement/purchase-orders', permissions: string[] = []) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  vi.mocked(useAuth).mockReturnValue({
    bootstrap: null,
    loading: false,
    login: vi.fn(),
    logout: vi.fn(),
    hasPermission: (permission: string) => permissions.includes(permission),
  })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initialEntry]}>
        <Routes>
          <Route path="/procurement/purchase-orders" element={<PurchaseOrdersPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

async function loaded() {
  await waitFor(() => expect(screen.queryByText('正在加载采购订单…')).not.toBeInTheDocument())
}

describe('PurchaseOrdersPage', () => {
  beforeEach(() => {
    mockOrders()
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('加载中显示 LoadingState', () => {
    apiClientMock.get.mockReturnValue(new Promise(() => undefined))
    renderPage()
    expect(screen.getByText('正在加载采购订单…')).toBeInTheDocument()
  })

  it('加载失败显示错误并可重试', async () => {
    apiClientMock.get.mockRejectedValue(new ApiError(503, { code: 'DOWN', message: '模拟服务暂时不可用' }))
    renderPage()
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('模拟服务暂时不可用'))
    const before = apiClientMock.get.mock.calls.length
    fireEvent.click(screen.getByRole('button', { name: '重新加载' }))
    await waitFor(() => expect(apiClientMock.get.mock.calls.length).toBeGreaterThan(before))
  })

  it('渲染订单列表与金额格式', async () => {
    renderPage()
    await loaded()
    expect(screen.getByRole('link', { name: 'PO-20260802-018' })).toHaveAttribute('href', '/procurement/purchase-orders/100018')
    expect(screen.getByText('recv_3包装材料有限公司')).toBeInTheDocument()
    expect(screen.getAllByText('¥48,600.00').length).toBeGreaterThan(0)
    expect(screen.getByText('待审核')).toBeInTheDocument()
    expect(screen.getByText('订单明细')).toBeInTheDocument()
    expect(screen.getByText('五层瓦楞纸箱')).toBeInTheDocument()
  })

  it('点击行联动明细', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getByText('苏州精工设备有限公司'))
    const detailCard = document.querySelector('.erp-detail-card')
    await waitFor(() => expect(detailCard).toHaveTextContent('PO-20260801-006'))
  })

  it('按钮按权限与选中状态显示', async () => {
    renderPage('/procurement/purchase-orders', ['purchase-order.create', 'purchase-order.update', 'purchase-order.submit', 'purchase-order.export', 'purchase-order.delete'])
    await loaded()
    expect(screen.getByRole('link', { name: /新建/ })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '编辑' })).toBeInTheDocument()
    const submit = screen.getByRole('button', { name: '提交审核' })
    expect(submit).toBeDisabled()
    expect(screen.getByRole('button', { name: '导出' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '删除' })).toBeDisabled()
    fireEvent.click(screen.getAllByLabelText(/选择订单 PO-20260801-006/)[0])
    await waitFor(() => expect(screen.getByRole('button', { name: /导出所选 \(1\)/ })).toBeInTheDocument())
    expect(screen.getByRole('button', { name: '删除' })).toBeEnabled()
  })

  it('无权限时不显示新建与编辑', async () => {
    renderPage()
    await loaded()
    expect(screen.queryByRole('link', { name: /新建/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '编辑' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '提交审核' })).not.toBeInTheDocument()
  })

  it('选择列菜单可隐藏列', async () => {
    renderPage()
    await loaded()
    const mainTable = screen.getAllByRole('table')[0]
    expect(mainTable).toHaveTextContent('采购员')
    fireEvent.click(screen.getByRole('button', { name: '选择列' }))
    fireEvent.click(screen.getByLabelText('采购员'))
    await waitFor(() => expect(mainTable).not.toHaveTextContent('采购员'))
  })

  it('搜索关键词触发带 keyword 的查询', async () => {
    renderPage()
    await loaded()
    fireEvent.change(screen.getByRole('searchbox', { name: '搜索单据、供应商或商品' }), { target: { value: '苏州' } })
    await waitFor(() => expect(apiClientMock.get).toHaveBeenCalledWith(
      '/purchase-orders',
      expect.objectContaining({ query: expect.objectContaining({ keyword: '苏州' }) }),
    ))
  })

  it('URL 状态参数进入查询', async () => {
    renderPage('/procurement/purchase-orders?status=pending&sortBy=purchaseDate&sortDirection=asc')
    await loaded()
    expect(apiClientMock.get).toHaveBeenCalledWith(
      '/purchase-orders',
      expect.objectContaining({ query: expect.objectContaining({ status: 'pending', sortBy: 'purchaseDate', sortDirection: 'asc' }) }),
    )
  })

  it('分页跳转触发新查询', async () => {
    mockOrders({ items: orders, page: 1, pageSize: 5, total: 12 })
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '下一页' }))
    await waitFor(() => expect(apiClientMock.get).toHaveBeenCalledWith(
      '/purchase-orders',
      expect.objectContaining({ query: expect.objectContaining({ page: 2 }) }),
    ))
  })

  it('空数据渲染空状态', async () => {
    mockOrders({ items: [], page: 1, pageSize: 5, total: 0 })
    renderPage()
    await loaded()
    expect(screen.getByText('没有找到采购订单')).toBeInTheDocument()
  })
})
