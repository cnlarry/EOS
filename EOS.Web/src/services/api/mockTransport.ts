import { ApiError } from '../../types/api'
import type { AppBootstrap, AuthUser, LoginCredentials, NavigationItem } from '../../features/auth/types'
import type { ApiRequest, ApiTransport } from './transport'

const baseNavigation: NavigationItem[] = [
  { id: 'dashboard', label: '首页', route: '/dashboard', icon: 'dashboard' },
  { id: 'sales', label: '销售管理', icon: 'sales', children: [{ id: 'sales-orders', label: '销售订单', route: '/sales/orders', icon: 'sales' }, { id: 'quotations', label: '报价单', route: '/sales/quotations', icon: 'sales' }] },
  { id: 'inventory', label: '库存管理', icon: 'inventory', children: [{ id: 'stock', label: '库存查询', route: '/inventory/stock', icon: 'inventory' }] },
  { id: 'profile', label: '个人设置', route: '/settings/profile', icon: 'settings' },
]
const users: Record<string, { user: AuthUser; permissions: string[]; menuIds: string[] }> = {
  admin: { user: { id: 'u1', username: 'admin', displayName: 'Demo User', employeeId: 'E001', avatarText: 'LW', avatarUrl: null, roleName: '系统管理员', organization: { id: 'east', name: '华东运营中心' } }, permissions: [], menuIds: ['dashboard','sales','inventory','profile'] },
  purchaser: { user: { id: 'u2', username: 'purchaser', displayName: '赵示例', employeeId: 'E002', avatarText: '陈晓', avatarUrl: null, roleName: '采购员', organization: { id: 'east', name: '华东运营中心' } }, permissions: [], menuIds: ['dashboard','inventory','profile'] },
  sales: { user: { id: 'u3', username: 'sales', displayName: '周文静', employeeId: 'E003', avatarText: '周文', avatarUrl: null, roleName: '销售员', organization: { id: 'east', name: '华东运营中心' } }, permissions: [], menuIds: ['dashboard','sales','inventory','profile'] },
  viewer: { user: { id: 'u4', username: 'viewer', displayName: '只读用户', employeeId: 'E004', avatarText: '只读', avatarUrl: null, roleName: '查询用户', organization: { id: 'east', name: '华东运营中心' } }, permissions: [], menuIds: ['dashboard','profile'] },
}
let activeUsername = sessionStorage.getItem('erp-mock-session')
function bootstrapFor(username: string): AppBootstrap { const account = users[username]; return { user: account.user, permissions: account.permissions, navigation: baseNavigation.filter((item) => account.menuIds.includes(item.id)) } }

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

    throw new ApiError(404, {
      code: 'MOCK_ROUTE_NOT_FOUND',
      message: `Mock API 未实现：${request.method} ${request.path}`,
      requestId: 'mock-request-404',
    })
  }
}
