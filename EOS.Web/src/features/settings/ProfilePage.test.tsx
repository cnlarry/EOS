import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../types/api'
import { useAuth } from '../auth/authContext'
import { ProfilePage } from './ProfilePage'


vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))
vi.mock('../auth/authContext', () => ({ useAuth: vi.fn() }))

const user = {
  id: 'u1', username: 'admin', displayName: 'Demo User', employeeId: 'E001', avatarText: 'LW',
  avatarUrl: null, roleName: '系统管理员', organization: { id: 'east', name: '华东运营中心' },
}

function renderPage() {
  vi.mocked(useAuth).mockReturnValue({
    bootstrap: { user, permissions: [], navigation: [] },
    loading: false,
    login: vi.fn(),
    logout: vi.fn(),
    hasPermission: () => false,
  })
  apiClientMock.put.mockResolvedValue(undefined)
  return renderWithProviders(<ProfilePage />)
}

describe('ProfilePage', () => {
  beforeEach(() => {
    renderPage()
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('展示个人资料信息', () => {
    expect(screen.getByText('Demo User')).toBeInTheDocument()
    expect(screen.getByText('admin')).toBeInTheDocument()
    expect(screen.getByDisplayValue('Demo User')).toBeDisabled()
    expect(screen.getByDisplayValue('E001')).toBeDisabled()
    expect(screen.getByDisplayValue('华东运营中心')).toBeDisabled()
    expect(screen.getByDisplayValue('系统管理员')).toBeDisabled()
  })

  it('密码不合规时禁用修改按钮', () => {
    const button = screen.getByRole('button', { name: '修改密码' })
    expect(button).toBeDisabled()
    fireEvent.change(screen.getByLabelText('当前密码'), { target: { value: 'old-password' } })
    fireEvent.change(screen.getByLabelText('新密码'), { target: { value: 'new-password' } })
    fireEvent.change(screen.getByLabelText('确认新密码'), { target: { value: 'new-password' } })
    expect(button).toBeEnabled()
  })

  it('两次新密码不一致时提示', () => {
    fireEvent.change(screen.getByLabelText('新密码'), { target: { value: 'new-password-1' } })
    fireEvent.change(screen.getByLabelText('确认新密码'), { target: { value: 'other-password-1' } })
    expect(screen.getByText('两次输入的新密码不一致。')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '修改密码' })).toBeDisabled()
  })

  it('修改密码成功展示成功消息并清空输入', async () => {
    fireEvent.change(screen.getByLabelText('当前密码'), { target: { value: 'old-password' } })
    fireEvent.change(screen.getByLabelText('新密码'), { target: { value: 'new-password' } })
    fireEvent.change(screen.getByLabelText('确认新密码'), { target: { value: 'new-password' } })
    fireEvent.click(screen.getByRole('button', { name: '修改密码' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith('/auth/password', { currentPassword: 'old-password', newPassword: 'new-password' }))
    expect(screen.getByRole('alert')).toHaveTextContent('密码修改成功。')
    expect(screen.getByLabelText('新密码')).toHaveValue('')
  })

  it('修改密码失败展示服务端消息', async () => {
    apiClientMock.put.mockRejectedValue(new ApiError(400, { code: 'OLD_WRONG', message: '当前密码不正确。' }))
    fireEvent.change(screen.getByLabelText('当前密码'), { target: { value: 'wrong' } })
    fireEvent.change(screen.getByLabelText('新密码'), { target: { value: 'new-password' } })
    fireEvent.change(screen.getByLabelText('确认新密码'), { target: { value: 'new-password' } })
    fireEvent.click(screen.getByRole('button', { name: '修改密码' }))
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('当前密码不正确。'))
  })
})
