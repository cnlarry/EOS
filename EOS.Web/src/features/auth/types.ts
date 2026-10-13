export interface AuthUser {
  id: string
  username: string
  displayName: string
  employeeId: string
  avatarText: string
  avatarUrl: string | null
  /** 所属用户组描述（SYSDG.G_DESC，多组用顿号连接）：这套权限模型里没有独立的"角色"概念，用户组即是角色。 */
  groupNames: string
  organization: { id: string; name: string }
}

export interface NavigationItem {
  id: string
  label: string
  route?: string
  icon: string
  alias?: string
  /** MODULES.M_IDX：叶子与分组都带（分组是纯目录时也有编号，供菜单提示与按编号直达）。 */
  moduleId?: number
  masterTable?: string
  groups?: NavigationGroup[] | null
  children?: NavigationItem[] | null
}

export interface NavigationGroup {
  /** MODULE_GROUPS.GROUP_ID：分组的身份（不是"第几组"，序号只决定下拉顺序）。 */
  groupId: number
  description: string
}

export interface AppBootstrap {
  user: AuthUser
  permissions: string[]
  navigation: NavigationItem[]
}

export interface LoginCredentials { userId: string; password: string; rememberMe: boolean }
