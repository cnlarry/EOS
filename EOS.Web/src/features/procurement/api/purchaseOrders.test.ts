import { describe, expect, it, vi } from 'vitest'
import { apiClient } from '../../../services/api'
import { getPurchaseOrders, purchaseOrdersQueryOptions } from './purchaseOrders'

vi.mock('../../../services/api', () => ({
  apiClient: { get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn(), postFile: vi.fn() },
}))

describe('purchaseOrders api', () => {
  it('getPurchaseOrders 透传查询参数', async () => {
    vi.mocked(apiClient.get).mockResolvedValue({ items: [], page: 1, pageSize: 10, total: 0 })
    await getPurchaseOrders({ keyword: 'k', status: 'pending', dateFrom: '2026-08-01', dateTo: '2026-08-02', sortBy: 'purchaseDate', sortDirection: 'asc', page: 1, pageSize: 10 })
    expect(apiClient.get).toHaveBeenCalledWith('/purchase-orders', {
      query: {
        keyword: 'k', status: 'pending', dateFrom: '2026-08-01', dateTo: '2026-08-02',
        sortBy: 'purchaseDate', sortDirection: 'asc', page: 1, pageSize: 10,
      },
      signal: undefined,
    })
  })

  it('purchaseOrdersQueryOptions 生成 queryKey 与 queryFn', async () => {
    vi.mocked(apiClient.get).mockResolvedValue({ items: [], page: 1, pageSize: 5, total: 0 })
    const options = purchaseOrdersQueryOptions({ page: 1, pageSize: 5 })
    expect(options.queryKey).toEqual(['purchase-orders', { page: 1, pageSize: 5 }])
    await options.queryFn?.({ signal: undefined } as never)
    expect(apiClient.get).toHaveBeenCalled()
  })
})
