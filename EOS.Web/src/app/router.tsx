import { createBrowserRouter, Navigate, useParams } from 'react-router-dom'
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
import { UserAdminPage } from '../features/user-admin/UserAdminPage'
import { ProfilePage } from '../features/settings/ProfilePage'
import { ErrorPage } from './ErrorPage'

function ForbiddenPage() { return <main className="erp-error-page"><div className="text-center"><div className="display-5 fw-bold">403</div><h1>没有访问权限</h1><p className="text-secondary">当前账号无权访问此页面。</p></div></main> }

/**
 * 以 moduleId 作为 key 强制重挂载，避免上一个模块的排序/分页/查询条件等状态
 * 带进下一个模块（否则会以无效 sortField 请求导致“排序字段无效”）。
 */
function WorkbenchRoute() {
  const { moduleId = '' } = useParams()
  return <DocumentWorkbenchPage key={moduleId} />
}

/** 数据表切换（tableId 变化）时同样重置字段维护页的搜索/分页状态 */
function FieldAdminRoute() {
  const { tableId = '' } = useParams()
  return <FieldAdminPage key={tableId} />
}

/** 表单编辑页（新增/编辑）跨模块打开时同样重置草稿/校验状态 */
function FormEditorRoute() {
  const { moduleId = '' } = useParams()
  return <FormEditorPage key={moduleId} />
}

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
        { element: <RequirePermission permission="legacy-module.2302.read" />, children: [{ path: 'admin/tables', element: <TableAdminPage /> }, { path: 'admin/tables/:tableId/fields', element: <FieldAdminRoute /> }] },
        { element: <RequirePermission permission="legacy-module.2306.read" />, children: [{ path: 'admin/users', element: <UserAdminPage /> }] },
        { path: 'document-workbench/:moduleId', element: <WorkbenchRoute /> },
        { path: 'document-workbench/:moduleId/new', element: <FormEditorRoute /> },
        { path: 'document-workbench/:moduleId/edit', element: <FormEditorRoute /> },
        { path: 'legacy/modules/:moduleId', element: <LegacyModulePage /> },
        { path: 'settings/profile', element: <ProfilePage /> },
      ],
    }],
  },
])
