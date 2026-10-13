import { IconRefresh, IconRotate } from '@tabler/icons-react'
import { useState } from 'react'
import { Button } from '../../components/ui/Button'
import { useAuth } from '../auth/authContext'
import { useThemePreference, type ThemePreference } from '../../components/layout/themePreference'
import { workspaceTabsEnabled } from '../../components/layout/workspaceTabs'
import { SIDEBAR_COLLAPSED_KEY, SIDEBAR_WIDTH_KEY, WORKSPACE_TABS_ENABLED_KEY } from '../../lib/storageKeys'

const avatarPalette = ['#6366f1', '#0ea5e9', '#10b981', '#f59e0b', '#ef4444', '#8b5cf6', '#ec4899', '#14b8a6']

function avatarColor(username: string): string {
  let hash = 0
  for (let index = 0; index < username.length; index += 1) {
    hash = (hash * 31 + username.charCodeAt(index)) >>> 0
  }
  return avatarPalette[hash % avatarPalette.length]
}

const THEME_OPTIONS: { value: ThemePreference; label: string }[] = [
  { value: 'system', label: '跟随系统' },
  { value: 'light', label: '浅色' },
  { value: 'dark', label: '深色' },
]

/**
 * 个人设置（/settings/profile）：只管**用户自己的界面偏好**，账号信息一律只读。
 *
 * 为什么是这些项：显示名、工号、部门、用户组都来自人事主档与登录票据（`SYSDN` / `SYSDG`），
 * 属组织权威数据——允许自改就等于在人事档案之外造第二处真源，刷新一次就被覆盖回去。
 * 改密码是凭据操作，走侧栏用户菜单的弹窗（见 `PasswordChangeModal`），本页不重复承载。
 *
 * 偏好里的主题立即生效（外壳统一持有，见 `useThemePreference`）；
 * 多标签工作区与侧栏折叠是外壳启动时读一次的配置，改完要重新加载界面才生效，故显式提示。
 */
export function ProfilePage() {
  const { bootstrap } = useAuth()
  const user = bootstrap?.user
  const { preference, setPreference } = useThemePreference()

  const [tabsEnabled, setTabsEnabled] = useState(workspaceTabsEnabled)
  const [sidebarCollapsed, setSidebarCollapsed] = useState(() => localStorage.getItem(SIDEBAR_COLLAPSED_KEY) === 'true')
  const [reloadNeeded, setReloadNeeded] = useState(false)

  /** 多标签工作区的开关值是"关"即 `off`（默认开启），与外壳读取时同一约定。 */
  const toggleTabs = (enabled: boolean) => {
    setTabsEnabled(enabled)
    setReloadNeeded(true)
    if (enabled) localStorage.removeItem(WORKSPACE_TABS_ENABLED_KEY)
    else localStorage.setItem(WORKSPACE_TABS_ENABLED_KEY, 'off')
  }

  const toggleSidebarCollapsed = (collapsed: boolean) => {
    setSidebarCollapsed(collapsed)
    setReloadNeeded(true)
    localStorage.setItem(SIDEBAR_COLLAPSED_KEY, String(collapsed))
  }

  const resetSidebarWidth = () => {
    localStorage.removeItem(SIDEBAR_WIDTH_KEY)
    setReloadNeeded(true)
  }

  return (
    <div className="erp-profile-page">
      <section className="card">
        <div className="card-header"><h2 className="card-title">账号信息</h2></div>
        <div className="card-body">
          <div className="d-flex align-items-center gap-3 mb-4">
            {user?.avatarUrl ? (
              <span className="avatar avatar-lg"><img src={user.avatarUrl} alt="" /></span>
            ) : (
              <span className="avatar avatar-lg" style={{ backgroundColor: avatarColor(user?.username ?? 'user') }}>{user?.avatarText}</span>
            )}
            <div>
              <div className="fw-semibold fs-4">{user?.displayName}</div>
              <div className="text-secondary">{user?.username}</div>
            </div>
          </div>
          <div className="row g-3">
            <div className="col-md-6"><label className="form-label">登录账号</label><input className="form-control" value={user?.username ?? ''} disabled /></div>
            <div className="col-md-6"><label className="form-label">员工号</label><input className="form-control" value={user?.employeeId ?? ''} disabled /></div>
            <div className="col-md-6"><label className="form-label">姓名</label><input className="form-control" value={user?.displayName ?? ''} disabled /></div>
            <div className="col-md-6"><label className="form-label">所属部门</label><input className="form-control" value={user?.organization.name ?? ''} disabled /></div>
            <div className="col-md-6"><label className="form-label">所属用户组</label><input className="form-control" value={user?.groupNames ?? ''} disabled /></div>
          </div>
          <p className="text-secondary small mt-3 mb-0">
            以上信息来自人事主档与用户组配置，如需变更请联系管理员在 2306 用户管理 / 2305 用户组管理中维护。
          </p>
        </div>
      </section>

      <section className="card">
        <div className="card-header"><h2 className="card-title">界面偏好</h2></div>
        <div className="card-body">
          <div className="erp-profile-field">
            <label className="form-label" htmlFor="profile-theme">显示模式</label>
            <select
              id="profile-theme"
              className="form-select erp-profile-input"
              value={preference}
              onChange={(event) => setPreference(event.target.value as ThemePreference)}
            >
              {THEME_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
            </select>
            <div className="form-hint">与侧栏底部的模式切换按钮是同一个设置；「跟随系统」时随操作系统的深浅模式变化。</div>
          </div>

          <div className="erp-profile-toggle">
            <input
              id="profile-tabs"
              className="form-check-input"
              type="checkbox"
              checked={tabsEnabled}
              onChange={(event) => toggleTabs(event.target.checked)}
            />
            <label htmlFor="profile-tabs">
              <span className="fw-medium">多标签工作区</span>
              <span className="text-secondary d-block small">开启后每打开一个页面占一个标签，可来回切换且不丢填写内容；关闭则退回单页面导航。</span>
            </label>
          </div>

          <div className="erp-profile-toggle">
            <input
              id="profile-sidebar-collapsed"
              className="form-check-input"
              type="checkbox"
              checked={sidebarCollapsed}
              onChange={(event) => toggleSidebarCollapsed(event.target.checked)}
            />
            <label htmlFor="profile-sidebar-collapsed">
              <span className="fw-medium">默认折叠左侧菜单</span>
              <span className="text-secondary d-block small">折叠后菜单收成图标列，鼠标移上去再展开各层。</span>
            </label>
          </div>

          <div className="d-flex align-items-center gap-2 flex-wrap">
            <Button variant="ghost" size="sm" icon={<IconRotate size={15} />} onClick={resetSidebarWidth}>恢复默认菜单宽度</Button>
            <span className="text-secondary small">拖动菜单右缘调整的宽度恢复为默认值。</span>
          </div>

          {reloadNeeded && (
            <div className="alert alert-info py-2 mt-3 mb-0" role="status">
              这项偏好在下次加载界面时生效，重新打开页面即可看到效果。
            </div>
          )}
        </div>
      </section>

      <section className="card">
        <div className="card-header"><h2 className="card-title">会话信息</h2></div>
        <div className="card-body">
          <div className="text-secondary small">当前登录会话由服务端 Cookie 维护（HttpOnly + SameSite），不会在浏览器本地存储令牌。</div>
          <div className="mt-3">
            <Button variant="ghost" size="sm" icon={<IconRefresh size={15} />} onClick={() => location.reload()}>刷新用户信息</Button>
          </div>
        </div>
      </section>
    </div>
  )
}
