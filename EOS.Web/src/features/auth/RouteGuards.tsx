import { Navigate, Outlet, useLocation } from 'react-router-dom'
import { LoadingState } from '../../components/common/AsyncState'
import { useAuth } from './authContext'

export function RequireAuth() {
  const { bootstrap, loading } = useAuth()
  const location = useLocation()
  if (loading) return <LoadingState label="正在恢复登录状态…" />
  if (!bootstrap) return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />
  return <Outlet />
}

export function RequirePermission({ permission }: { permission: string }) {
  const { hasPermission } = useAuth()
  if (!hasPermission(permission)) return <Navigate to="/forbidden" replace />
  return <Outlet />
}
