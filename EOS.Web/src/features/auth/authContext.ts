import { createContext, useContext } from 'react'
import type { AppBootstrap, LoginCredentials } from './types'

export interface AuthContextValue {
  bootstrap: AppBootstrap | null
  loading: boolean
  login: (credentials: LoginCredentials) => Promise<void>
  logout: () => Promise<void>
  hasPermission: (permission: string) => boolean
}

export const AuthContext = createContext<AuthContextValue | null>(null)

export function useAuth() {
  const context = useContext(AuthContext)
  if (!context) throw new Error('useAuth must be used within AuthProvider')
  return context
}
