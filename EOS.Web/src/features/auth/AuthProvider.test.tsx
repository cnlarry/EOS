import { render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { apiClient } from '../../services/api'
import { AuthProvider } from './AuthProvider'
import { useAuth } from './authContext'
import type { AppBootstrap } from './types'

vi.mock('../../services/api', () => ({
  apiClient: {
    get: vi.fn(),
    post: vi.fn(),
    put: vi.fn(),
    delete: vi.fn(),
    postFile: vi.fn(),
  },
}))

const bootstrap: AppBootstrap = {
  user: { id: 'u1', username: 'admin', displayName: '管理员', employeeId: 'E1', avatarText: '管', avatarUrl: null, roleName: '系统管理员', organization: { id: 'east', name: '华东' } },
  permissions: ['purchase-order.read'],
  navigation: [],
}

function Probe() {
  const auth = useAuth()
  return (
    <div>
      <span data-testid="user">{auth.bootstrap?.user.username ?? 'none'}</span>
      <span data-testid="loading">{String(auth.loading)}</span>
      <span data-testid="perm">{String(auth.hasPermission('purchase-order.read'))}</span>
      <button onClick={() => void auth.login({ userId: 'admin', password: 'x', rememberMe: false })}>login</button>
      <button onClick={() => void auth.logout()}>logout</button>
    </div>
  )
}

describe('AuthProvider', () => {
  it('挂载时加载 bootstrap', async () => {
    vi.mocked(apiClient.get).mockResolvedValue(bootstrap)
    render(<AuthProvider><Probe /></AuthProvider>)
    expect(screen.getByTestId('loading')).toHaveTextContent('true')
    await waitFor(() => expect(screen.getByTestId('user')).toHaveTextContent('admin'))
    expect(screen.getByTestId('loading')).toHaveTextContent('false')
    expect(screen.getByTestId('perm')).toHaveTextContent('true')
  })

  it('bootstrap 失败时保持未登录', async () => {
    vi.mocked(apiClient.get).mockRejectedValue(new Error('401'))
    render(<AuthProvider><Probe /></AuthProvider>)
    await waitFor(() => expect(screen.getByTestId('loading')).toHaveTextContent('false'))
    expect(screen.getByTestId('user')).toHaveTextContent('none')
  })

  it('login 后设置 bootstrap，logout 后清除', async () => {
    vi.mocked(apiClient.post).mockResolvedValue({ userId: 'admin', employeeName: '管理员' })
    vi.mocked(apiClient.get).mockResolvedValue(bootstrap)
    render(<AuthProvider><Probe /></AuthProvider>)
    await waitFor(() => expect(screen.getByTestId('loading')).toHaveTextContent('false'))
    await screen.getByRole('button', { name: 'login' }).click()
    await waitFor(() => expect(screen.getByTestId('user')).toHaveTextContent('admin'))
    await screen.getByRole('button', { name: 'logout' }).click()
    await waitFor(() => expect(screen.getByTestId('user')).toHaveTextContent('none'))
  })

  it('useAuth 在 Provider 外抛出', () => {
    expect(() => render(<Probe />)).toThrow('useAuth must be used within AuthProvider')
  })
})
