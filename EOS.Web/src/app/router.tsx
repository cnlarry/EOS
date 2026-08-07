import { createBrowserRouter, Navigate } from 'react-router-dom'
import { AppShell } from '../components/layout/AppShell'
import { LoginPage } from '../features/auth/LoginPage'
import { RequireAuth, RequirePermission } from '../features/auth/RouteGuards'
import { DashboardPage } from '../features/dashboard/DashboardPage'
import { DocumentWorkbenchPage } from '../features/document-workbench/DocumentWorkbenchPage'
import { FormEditorPage } from '../features/document-workbench/FormEditorPage'
import { LegacyModulePage } from '../features/legacy/LegacyModulePage'
import { PurchaseOrdersPage } from '../features/procurement/pages/PurchaseOrdersPage'
import { FieldAdminPage } from '../features/field-admin/FieldAdminPage'
import { TableAdminPage } from '../features/field-admin/TableAdminPage'
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
        { element: <RequirePermission permission="legacy-module.2302.read" />, children: [{ path: 'admin/tables', element: <TableAdminPage /> }, { path: 'admin/tables/:tableId/fields', element: <FieldAdminPage /> }] },
        { path: 'document-workbench/:moduleId', element: <DocumentWorkbenchPage /> },
        { path: 'document-workbench/:moduleId/new', element: <FormEditorPage /> },
        { path: 'document-workbench/:moduleId/edit', element: <FormEditorPage /> },
        { path: 'legacy/modules/:moduleId', element: <LegacyModulePage /> },
        { path: 'settings/profile', element: <ProfilePage /> },
      ],
    }],
  },
])
