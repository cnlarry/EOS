import { renderWithProviders } from '../../test/renderWithProviders'
import { fireEvent, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useAuth } from '../auth/authContext'
import { ThemePreferenceContext, type ThemePreferenceValue, type ThemePreference } from '../../components/layout/themePreference'
import { WORKSPACE_TABS_ENABLED_KEY, SIDEBAR_COLLAPSED_KEY, SIDEBAR_WIDTH_KEY } from '../../lib/storageKeys'
import { ProfilePage } from './ProfilePage'

vi.mock('../auth/authContext', () => ({ useAuth: vi.fn() }))

const user = {
  id: 'u1', username: 'admin', displayName: 'Demo User', employeeId: 'E001', avatarText: 'LW',
  avatarUrl: null, groupNames: 'ERP 管理员、采购组', organization: { id: 'east', name: '华东运营中心' },
}

/** 页面消费外壳下发的主题偏好，测试里就自己提供一份（capture 改动便于断言）。 */
function renderPage(overrides: Partial<ThemePreferenceValue> = {}) {
  vi.mocked(useAuth).mockReturnValue({
    bootstrap: { user, permissions: [], navigation: [] },
    loading: false,
    login: vi.fn(),
    logout: vi.fn(),
    hasPermission: () => false,
  })
  const setPreference = vi.fn()
  const value: ThemePreferenceValue = {
    preference: 'system' as ThemePreference,
    theme: 'light',
    setPreference,
    ...overrides,
  }
  return {
    setPreference,
    ...renderWithProviders(
      <ThemePreferenceContext.Provider value={value}>
        <ProfilePage />
      </ThemePreferenceContext.Provider>,
    ),
  }
}

describe('ProfilePage', () => {
  beforeEach(() => {
    localStorage.clear()
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('展示账号信息且全部只读（含所属用户组）', () => {
    renderPage()
    expect(screen.getByText('Demo User')).toBeInTheDocument()
    expect(screen.getByText('admin')).toBeInTheDocument()
    expect(screen.getByDisplayValue('Demo User')).toBeDisabled()
    expect(screen.getByDisplayValue('E001')).toBeDisabled()
    expect(screen.getByDisplayValue('华东运营中心')).toBeDisabled()
    expect(screen.getByDisplayValue('ERP 管理员、采购组')).toBeDisabled()
  })

  it('不再提供修改密码表单（改密码走侧栏用户菜单的弹窗）', () => {
    renderPage()
    expect(screen.queryByLabelText('当前密码')).toBeNull()
    expect(screen.queryByLabelText('新密码')).toBeNull()
    expect(screen.queryByRole('button', { name: '修改密码' })).toBeNull()
  })

  it('显示模式改的是外壳下发的主题偏好', () => {
    const { setPreference } = renderPage()
    fireEvent.change(screen.getByLabelText('显示模式'), { target: { value: 'dark' } })
    expect(setPreference).toHaveBeenCalledWith('dark')
  })

  it('多标签工作区开关写入本地并提示重新加载后生效', () => {
    renderPage()
    fireEvent.click(screen.getByLabelText(/多标签工作区/))
    // 关闭即写 'off'（默认开启，与外壳读取时同一约定）
    expect(localStorage.getItem(WORKSPACE_TABS_ENABLED_KEY)).toBe('off')
    expect(screen.getByRole('status')).toHaveTextContent('重新打开页面')
  })

  it('默认折叠菜单开关写入本地', () => {
    renderPage()
    fireEvent.click(screen.getByLabelText(/默认折叠左侧菜单/))
    expect(localStorage.getItem(SIDEBAR_COLLAPSED_KEY)).toBe('true')
  })

  it('恢复默认菜单宽度清掉本地宽度', () => {
    localStorage.setItem(SIDEBAR_WIDTH_KEY, '320')
    renderPage()
    fireEvent.click(screen.getByRole('button', { name: /恢复默认菜单宽度/ }))
    expect(localStorage.getItem(SIDEBAR_WIDTH_KEY)).toBeNull()
  })
})
