export interface AuthUser {
  id: string
  username: string
  displayName: string
  employeeId: string
  avatarText: string
  avatarUrl: string | null
  roleName: string
  organization: { id: string; name: string }
}

export interface NavigationItem {
  id: string
  label: string
  route?: string
  icon: string
  children?: NavigationItem[] | null
}

export interface AppBootstrap {
  user: AuthUser
  permissions: string[]
  navigation: NavigationItem[]
}

export interface LoginCredentials { userId: string; password: string; rememberMe: boolean }
