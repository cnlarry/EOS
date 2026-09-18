import { createBrowserRouter } from 'react-router-dom'
import { AppShell } from '../components/layout/AppShell'
import { RequireAuth } from '../features/auth/RouteGuards'
import { ErrorPage } from './ErrorPage'
import { LoginPage } from './lazyRoutes'
import { ForbiddenPage } from './routeElements'
import { withSuspense } from './suspense'

/**
 * 外壳只声明到 AppShell 一层：具体工作区路由由 AppShell 内的标签 host 按各标签地址求值渲染
 * （见 app/workspaceRoutes.tsx），因此这里必须是通配路径，否则深层地址匹配不到外壳。
 */
export const router = createBrowserRouter([
  { path: '/login', element: withSuspense(<LoginPage />) },
  { path: '/forbidden', element: <ForbiddenPage /> },
  {
    element: <RequireAuth />,
    children: [{ path: '/*', element: <AppShell />, errorElement: <ErrorPage /> }],
  },
])
