import { Navigate, Outlet, useLocation } from 'react-router-dom'
import { ForbiddenPage } from '../../app/routeElements'
import { LoadingState } from '../../components/common/AsyncState'
import { useAuth } from './authContext'

export function RequireAuth() {
  const { bootstrap, loading } = useAuth()
  const location = useLocation()
  if (loading) return <LoadingState label="正在恢复登录状态…" />
  if (!bootstrap) return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />
  return <Outlet />
}

/**
 * 权限不足时就地渲染 403：不跳转、不关闭所在标签。
 * 跳去独立页面会把整个工作区连同其它标签一起卸载，并连带丢弃当前标签里未保存的改动。
 */
export function RequirePermission({ permission }: { permission: string }) {
  const { hasPermission } = useAuth()
  if (!hasPermission(permission)) return <ForbiddenPage />
  return <Outlet />
}
