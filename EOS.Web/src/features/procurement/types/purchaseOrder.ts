export type PurchaseOrderStatus =
  | 'draft'
  | 'pending'
  | 'approved'
  | 'rejected'
  | 'cancelled'
  | 'closed'

export interface PurchaseOrderLine {
  id: string
  productCode: string
  productName: string
  specification: string
  unit: string
  quantity: string
  unitPrice: string
  taxRate: string
  lineAmount: string
}

export interface PurchaseOrderSummary {
  id: string
  number: string
  supplierName: string
  purchaseDate: string
  deliveryDate: string
  totalAmount: string
  currency: 'CNY'
  status: PurchaseOrderStatus
  buyerName: string
  lines?: PurchaseOrderLine[]
}

export type PurchaseOrderSortField = 'number' | 'supplierName' | 'purchaseDate' | 'totalAmount' | 'status'
export type SortDirection = 'asc' | 'desc'

export interface PurchaseOrderListQuery {
  keyword?: string
  status?: PurchaseOrderStatus
  dateFrom?: string
  dateTo?: string
  sortBy?: PurchaseOrderSortField
  sortDirection?: SortDirection
  page: number
  pageSize: number
}
