import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { apiClient } from '../../services/api'
import type { AppBootstrap, LoginCredentials } from './types'

interface AuthContextValue {
  bootstrap: AppBootstrap | null
  loading: boolean
  login: (credentials: LoginCredentials) => Promise<void>
  logout: () => Promise<void>
  hasPermission: (permission: string) => boolean
}

const AuthContext = createContext<AuthContextValue | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [bootstrap, setBootstrap] = useState<AppBootstrap | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    apiClient.get<AppBootstrap>('/app/bootstrap').then(setBootstrap).catch(() => setBootstrap(null)).finally(() => setLoading(false))
  }, [])

  const value = useMemo<AuthContextValue>(() => ({
    bootstrap,
    loading,
    async login(credentials) {
      await apiClient.post<{ userId: string; employeeName: string }, LoginCredentials>('/auth/login', credentials)
      const data = await apiClient.get<AppBootstrap>('/app/bootstrap')
      setBootstrap(data)
    },
    async logout() {
      await apiClient.post('/auth/logout')
      setBootstrap(null)
    },
    hasPermission: (permission) => bootstrap?.permissions.includes(permission) ?? false,
  }), [bootstrap, loading])

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth() {
  const context = useContext(AuthContext)
  if (!context) throw new Error('useAuth must be used within AuthProvider')
  return context
}
