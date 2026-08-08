import { createBrowserRouter, Navigate } from 'react-router-dom'
import { AppShell } from '../components/layout/AppShell'
import { RequireAuth, RequirePermission } from '../features/auth/RouteGuards'
import { ErrorPage } from './ErrorPage'
import {
  DashboardPage,
  ImportPage,
  LegacyModulePage,
  LoginPage,
  ProfilePage,
  PurchaseOrdersPage,
  ReportViewerPage,
  SearchCenterPage,
  TableAdminPage,
  UserAdminPage,
} from './lazyRoutes'
import { FieldAdminRoute, ForbiddenPage, FormEditorRoute, WorkbenchRoute } from './routeElements'
import { withSuspense } from './suspense'

export const router = createBrowserRouter([
  { path: '/login', element: withSuspense(<LoginPage />) },
  { path: '/forbidden', element: <ForbiddenPage /> },
  {
    element: <RequireAuth />,
    children: [{
      path: '/', element: <AppShell />, errorElement: <ErrorPage />, children: [
        { index: true, element: <Navigate to="/dashboard" replace /> },
        { path: 'dashboard', element: withSuspense(<DashboardPage />) },
        { path: 'legacy/modules/:moduleId', element: withSuspense(<LegacyModulePage />) },
        { element: <RequirePermission permission="purchase-order.read" />, children: [{ path: 'procurement/purchase-orders', element: withSuspense(<PurchaseOrdersPage />) }] },
        { element: <RequirePermission permission="legacy-module.2302.read" />, children: [{ path: 'admin/tables', element: withSuspense(<TableAdminPage />) }, { path: 'admin/tables/:tableId/fields', element: withSuspense(<FieldAdminRoute />) }] },
        { element: <RequirePermission permission="legacy-module.2306.read" />, children: [{ path: 'admin/users', element: withSuspense(<UserAdminPage />) }] },
        { path: 'document-workbench/:moduleId', element: withSuspense(<WorkbenchRoute />) },
        { path: 'document-workbench/:moduleId/new', element: withSuspense(<FormEditorRoute />) },
        { path: 'document-workbench/:moduleId/edit', element: withSuspense(<FormEditorRoute />) },
        { path: 'reports/:moduleId', element: withSuspense(<ReportViewerPage />) },
        { path: 'search-center/:moduleId?', element: withSuspense(<SearchCenterPage />) },
        { path: 'import', element: withSuspense(<ImportPage />) },
        { path: 'legacy/modules/:moduleId', element: withSuspense(<LegacyModulePage />) },
        { path: 'settings/profile', element: withSuspense(<ProfilePage />) },
      ],
    }],
  },
])
