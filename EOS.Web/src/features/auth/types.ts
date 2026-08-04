export interface AuthUser {
  id: string
  username: string
  displayName: string
  avatarText: string
  roleName: string
  organization: { id: string; name: string }
}

export interface NavigationItem {
  id: string
  label: string
  route?: string
  icon: 'dashboard' | 'procurement' | 'sales' | 'inventory' | 'settings'
  children?: NavigationItem[] | null
}

export interface AppBootstrap {
  user: AuthUser
  permissions: string[]
  navigation: NavigationItem[]
}

export interface LoginCredentials { userId: string; password: string; rememberMe: boolean }
