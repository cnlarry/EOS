import { createBrowserRouter, Navigate } from 'react-router-dom'
import { AppShell } from '../components/layout/AppShell'
import { LoginPage } from '../features/auth/LoginPage'
import { RequireAuth, RequirePermission } from '../features/auth/RouteGuards'
import { DashboardPage } from '../features/dashboard/DashboardPage'
import { DocumentWorkbenchPage } from '../features/document-workbench/DocumentWorkbenchPage'
import { LegacyModulePage } from '../features/legacy/LegacyModulePage'
import { PurchaseOrdersPage } from '../features/procurement/pages/PurchaseOrdersPage'
import { ProfilePage } from '../features/settings/ProfilePage'
import { ErrorPage } from './ErrorPage'

function ForbiddenPage() { return <main className="erp-error-page"><div className="text-center"><div className="display-5 fw-bold">403</div><h1>没有访问权限</h1><p className="text-secondary">当前账号无权访问此页面。</p></div></main> }

export const router = createBrowserRouter([
  { path: '/login', element: <LoginPage /> },
  { path: '/forbidden', element: <ForbiddenPage /> },
  {
    element: <RequireAuth />,
    children: [{
      path: '/', element: <AppShell />, errorElement: <ErrorPage />, children: [
        { index: true, element: <Navigate to="/dashboard" replace /> },
        { path: 'dashboard', element: <DashboardPage /> },
        { path: 'legacy/modules/:moduleId', element: <LegacyModulePage /> },
        { element: <RequirePermission permission="purchase-order.read" />, children: [{ path: 'procurement/purchase-orders', element: <PurchaseOrdersPage /> }] },
        { path: 'document-workbench/:moduleId', element: <DocumentWorkbenchPage /> },
        { path: 'legacy/modules/:moduleId', element: <LegacyModulePage /> },
        { path: 'settings/profile', element: <ProfilePage /> },
      ],
    }],
  },
])
