import { ApiError, type PageResponse } from '../../types/api'
import type { PurchaseOrderSummary } from '../../features/procurement/types/purchaseOrder'
import type { AppBootstrap, AuthUser, LoginCredentials, NavigationItem } from '../../features/auth/types'
import type { ApiRequest, ApiTransport } from './transport'

const baseNavigation: NavigationItem[] = [
  { id: 'dashboard', label: '工作台', route: '/dashboard', icon: 'dashboard' },
  { id: 'procurement', label: '采购管理', icon: 'procurement', children: [{ id: 'purchase-orders', label: '采购订单', route: '/procurement/purchase-orders', icon: 'procurement' }, { id: 'requisitions', label: '请购单', route: '/procurement/requisitions', icon: 'procurement' }] },
  { id: 'sales', label: '销售管理', icon: 'sales', children: [{ id: 'sales-orders', label: '销售订单', route: '/sales/orders', icon: 'sales' }, { id: 'quotations', label: '报价单', route: '/sales/quotations', icon: 'sales' }] },
  { id: 'inventory', label: '库存管理', icon: 'inventory', children: [{ id: 'stock', label: '库存查询', route: '/inventory/stock', icon: 'inventory' }] },
  { id: 'profile', label: '个人设置', route: '/settings/profile', icon: 'settings' },
]
const users: Record<string, { user: AuthUser; permissions: string[]; menuIds: string[] }> = {
  admin: { user: { id: 'u1', username: 'admin', displayName: 'Demo User', employeeId: 'E001', avatarText: 'LW', avatarUrl: null, roleName: '系统管理员', organization: { id: 'east', name: '华东运营中心' } }, permissions: ['purchase-order.read','purchase-order.create','purchase-order.update','purchase-order.submit','purchase-order.export','purchase-order.delete'], menuIds: ['dashboard','procurement','sales','inventory','profile'] },
  purchaser: { user: { id: 'u2', username: 'purchaser', displayName: '赵示例', employeeId: 'E002', avatarText: '陈晓', avatarUrl: null, roleName: '采购员', organization: { id: 'east', name: '华东运营中心' } }, permissions: ['purchase-order.read','purchase-order.create','purchase-order.update','purchase-order.submit','purchase-order.export'], menuIds: ['dashboard','procurement','inventory','profile'] },
  sales: { user: { id: 'u3', username: 'sales', displayName: '周文静', employeeId: 'E003', avatarText: '周文', avatarUrl: null, roleName: '销售员', organization: { id: 'east', name: '华东运营中心' } }, permissions: [], menuIds: ['dashboard','sales','inventory','profile'] },
  viewer: { user: { id: 'u4', username: 'viewer', displayName: '只读用户', employeeId: 'E004', avatarText: '只读', avatarUrl: null, roleName: '查询用户', organization: { id: 'east', name: '华东运营中心' } }, permissions: ['purchase-order.read'], menuIds: ['dashboard','procurement','profile'] },
}
let activeUsername = sessionStorage.getItem('erp-mock-session')
function bootstrapFor(username: string): AppBootstrap { const account = users[username]; return { user: account.user, permissions: account.permissions, navigation: baseNavigation.filter((item) => account.menuIds.includes(item.id)) } }

const purchaseOrders: PurchaseOrderSummary[] = [
  { id: '100018', number: 'PO-20260802-018', supplierName: 'recv_3包装材料有限公司', purchaseDate: '2026-08-02', deliveryDate: '2026-08-12', totalAmount: '48600.00', currency: 'CNY', status: 'pending', buyerName: '赵示例', lines: [{ id: 'L1801', productCode: 'MAT-PKG-001', productName: '五层瓦楞纸箱', specification: '600×400×350mm', unit: '只', quantity: '3000', unitPrice: '12.00', taxRate: '13', lineAmount: '36000.00' }, { id: 'L1802', productCode: 'MAT-PKG-018', productName: '防震珍珠棉', specification: '20mm / EPE', unit: '张', quantity: '900', unitPrice: '14.00', taxRate: '13', lineAmount: '12600.00' }] },
  { id: '100006', number: 'PO-20260801-006', supplierName: '苏州精工设备有限公司', purchaseDate: '2026-08-01', deliveryDate: '2026-08-18', totalAmount: '126800.00', currency: 'CNY', status: 'draft', buyerName: 'Demo User', lines: [{ id: 'L0601', productCode: 'EQP-CNC-012', productName: '伺服驱动器', specification: 'SD-750 / 7.5kW', unit: '台', quantity: '10', unitPrice: '12680.00', taxRate: '13', lineAmount: '126800.00' }] },
  { id: '100032', number: 'PO-20260731-032', supplierName: '宁波海际贸易有限公司', purchaseDate: '2026-07-31', deliveryDate: '2026-08-09', totalAmount: '32460.00', currency: 'CNY', status: 'approved', buyerName: '周文静', lines: [{ id: 'L3201', productCode: 'RAW-AL-6061', productName: '铝合金板材', specification: '6061-T6 / 3mm', unit: '张', quantity: '120', unitPrice: '270.50', taxRate: '13', lineAmount: '32460.00' }] },
  { id: '100029', number: 'PO-20260730-029', supplierName: '杭州瑞科电子有限公司', purchaseDate: '2026-07-30', deliveryDate: '2026-08-15', totalAmount: '78290.50', currency: 'CNY', status: 'rejected', buyerName: '赵示例' },
  { id: '100011', number: 'PO-20260729-011', supplierName: '上海工业原料有限公司', purchaseDate: '2026-07-29', deliveryDate: '2026-08-20', totalAmount: '215000.00', currency: 'CNY', status: 'closed', buyerName: 'Demo User' },
]

function wait(signal?: AbortSignal) {
  return new Promise<void>((resolve, reject) => {
    const timer = window.setTimeout(resolve, 450)
    signal?.addEventListener('abort', () => {
      window.clearTimeout(timer)
      reject(new DOMException('Request aborted', 'AbortError'))
    }, { once: true })
  })
}

export class MockTransport implements ApiTransport {
  async request<TResponse>(request: ApiRequest): Promise<TResponse> {
    await wait(request.signal)

    if (request.method === 'POST' && request.path === '/auth/login') {
      const credentials = request.body as LoginCredentials
      if (!users[credentials.userId] || credentials.password !== 'erp123') throw new ApiError(401, { code: 'INVALID_CREDENTIALS', message: '用户名或密码错误。' })
      activeUsername = credentials.userId; sessionStorage.setItem('erp-mock-session', credentials.userId)
      return bootstrapFor(credentials.userId) as TResponse
    }
    if (request.method === 'POST' && request.path === '/auth/logout') { activeUsername = null; sessionStorage.removeItem('erp-mock-session'); return undefined as TResponse }
    if (request.method === 'GET' && request.path === '/app/bootstrap') {
      if (!activeUsername || !users[activeUsername]) throw new ApiError(401, { code: 'UNAUTHENTICATED', message: '当前没有有效会话。' })
      return bootstrapFor(activeUsername) as TResponse
    }

    if (request.method === 'GET' && request.path === '/purchase-orders') {
      const keyword = String(request.query?.keyword ?? '').trim().toLocaleLowerCase()
      const status = String(request.query?.status ?? '')
      const dateFrom = String(request.query?.dateFrom ?? '')
      const dateTo = String(request.query?.dateTo ?? '')
      const sortBy = String(request.query?.sortBy ?? 'purchaseDate') as keyof PurchaseOrderSummary
      const sortDirection = String(request.query?.sortDirection ?? 'desc')
      const page = Math.max(1, Number(request.query?.page ?? 1))
      const pageSize = Math.min(100, Math.max(1, Number(request.query?.pageSize ?? 20)))

      if (keyword === 'error') {
        throw new ApiError(503, {
          code: 'MOCK_SERVICE_UNAVAILABLE',
          message: '模拟服务暂时不可用，请清除关键词后重试。',
          requestId: 'mock-request-503',
        })
      }

      const filtered = purchaseOrders.filter((order) => {
        const matchesKeyword = !keyword || `${order.number} ${order.supplierName} ${order.buyerName}`.toLocaleLowerCase().includes(keyword)
        const matchesStatus = !status || order.status === status
        const matchesFrom = !dateFrom || order.purchaseDate >= dateFrom
        const matchesTo = !dateTo || order.purchaseDate <= dateTo
        return matchesKeyword && matchesStatus && matchesFrom && matchesTo
      }).sort((left, right) => {
        const leftValue = sortBy === 'totalAmount' ? Number(left[sortBy]) : String(left[sortBy])
        const rightValue = sortBy === 'totalAmount' ? Number(right[sortBy]) : String(right[sortBy])
        const result = leftValue < rightValue ? -1 : leftValue > rightValue ? 1 : 0
        return sortDirection === 'asc' ? result : -result
      })
      const offset = (page - 1) * pageSize
      const response: PageResponse<PurchaseOrderSummary> = {
        items: filtered.slice(offset, offset + pageSize),
        page,
        pageSize,
        total: filtered.length,
      }
      return response as TResponse
    }

    throw new ApiError(404, {
      code: 'MOCK_ROUTE_NOT_FOUND',
      message: `Mock API 未实现：${request.method} ${request.path}`,
      requestId: 'mock-request-404',
    })
  }
}
