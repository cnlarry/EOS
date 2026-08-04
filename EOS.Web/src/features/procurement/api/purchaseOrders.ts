import { queryOptions } from '@tanstack/react-query'
import { apiClient } from '../../../services/api'
import type { PageResponse } from '../../../types/api'
import type { PurchaseOrderListQuery, PurchaseOrderSummary } from '../types/purchaseOrder'

export function getPurchaseOrders(query: PurchaseOrderListQuery, signal?: AbortSignal) {
  return apiClient.get<PageResponse<PurchaseOrderSummary>>('/purchase-orders', {
    query: {
      keyword: query.keyword,
      status: query.status,
      dateFrom: query.dateFrom,
      dateTo: query.dateTo,
      sortBy: query.sortBy,
      sortDirection: query.sortDirection,
      page: query.page,
      pageSize: query.pageSize,
    },
    signal,
  })
}

export function purchaseOrdersQueryOptions(query: PurchaseOrderListQuery) {
  return queryOptions({
    queryKey: ['purchase-orders', query],
    queryFn: ({ signal }) => getPurchaseOrders(query, signal),
  })
}
