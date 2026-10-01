import { Navigate, type RouteObject } from 'react-router-dom'
import { RequirePermission } from '../features/auth/RouteGuards'
import { moduleReadPermission } from '../features/auth/modulePermissions'
import {
  DashboardPage,
  DepotStockPolicyPage,
  DetailQueryPage,
  FieldAuditPage,
  FieldEditorRoute,
  BomExpandPage,
  CarSummaryPage,
  ImportPage,
  JobPage,
  FallbackModulePage,
  LogAdminPage,
  MyTasksPage,
  FlowDesignPage,
  FlowMonitorPage,
  PrintViewPage,
  ProfilePage,
  SearchCenterPage,
  SystemSettingsPage,
  TableAdminPage,
  UserAdminPage,
  UserRightsPage,
  UserButtonRightsPage,
  UserGroupAdminPage,
  GroupRightsPage,
  GroupButtonRightsPage,
  GroupMembersPage,
  MenuAdminPage,
  AdminSessionsPage,
  MechanismOverviewPage,
  KbAdminPage,
  ModelAdminPage,
  AssistantSettingsPage,
  ReportCenterPage,
  ReportInboxPage,
  LayoutDesignerPage,
} from './lazyRoutes'
import { FieldAdminRoute, FormEditorRoute, ReportAdminRoute, ReportIdentityRoute, ReportViewerRoute, UnknownRoutePage, WorkbenchRoute } from './routeElements'
import { AssistantPage } from '../features/assistant/AssistantPage'
import { SessionAdminPage } from '../features/assistant/SessionAdminPage'
import { withSuspense } from './suspense'

/**
 * 工作区路由表：标签 host 用嵌套 useRoutes 按各标签自身的 URL 求值渲染。
 * 表中只允许 element 形式（含 React.lazy + Suspense）——useRoutes 不执行 loader/action/数据 lazy，
 * 此类声明不会报错但静默不生效；需要数据 API 时须改造标签 host，不得直接写进本表。
 */
export const WORKSPACE_ROUTES: RouteObject[] = [
  { index: true, element: <Navigate to="/dashboard" replace /> },
  { path: 'dashboard', element: withSuspense(<DashboardPage />) },
  { path: 'fallback/modules/:moduleId', element: withSuspense(<FallbackModulePage />) },
  { element: <RequirePermission permission={moduleReadPermission(2302)} />, children: [
    { path: 'admin/tables', element: withSuspense(<TableAdminPage />) },
    { path: 'admin/tables/:tableId/fields', element: withSuspense(<FieldAdminRoute />) },
    { path: 'admin/fields/:tableId/:fieldId', element: withSuspense(<FieldEditorRoute />) },
  ] },
  { element: <RequirePermission permission={moduleReadPermission(2301)} />, children: [{ path: 'admin/menus', element: withSuspense(<MenuAdminPage />) }] },
  { element: <RequirePermission permission={moduleReadPermission(2305)} />, children: [
    { path: 'admin/groups', element: withSuspense(<UserGroupAdminPage />) },
    { path: 'admin/groups/:groupId/rights', element: withSuspense(<GroupRightsPage />) },

    { path: 'admin/groups/:groupId/button-rights', element: withSuspense(<GroupButtonRightsPage />) },
    { path: 'admin/groups/:groupId/members', element: withSuspense(<GroupMembersPage />) },
  ] },
  { element: <RequirePermission permission={moduleReadPermission(2303)} />, children: [{ path: 'admin/field-audit', element: withSuspense(<FieldAuditPage />) }] },
  // 日志管理（模块 2313，根 23 系统管理 / 父 2311 数据表维护）：读日志与诊断信息；
  // 打包下载由服务端另行要求设置权限
  { element: <RequirePermission permission={moduleReadPermission(2313)} />, children: [{ path: 'admin/logs', element: withSuspense(<LogAdminPage />) }] },
  { element: <RequirePermission permission={moduleReadPermission(110310)} />, children: [{ path: 'admin/depot-stock-policy', element: withSuspense(<DepotStockPolicyPage />) }] },
  { element: <RequirePermission permission={moduleReadPermission(2306)} />, children: [
    { path: 'admin/users', element: withSuspense(<UserAdminPage />) },
    { path: 'admin/users/:userId/rights', element: withSuspense(<UserRightsPage />) },

    { path: 'admin/users/:userId/button-rights', element: withSuspense(<UserButtonRightsPage />) },
  ] },
  { path: 'admin/report-setup', element: withSuspense(<ReportAdminRoute />) },
  { path: 'workbench/:moduleId', element: withSuspense(<WorkbenchRoute />) },
  { path: 'workbench/:moduleId/new', element: withSuspense(<FormEditorRoute />) },
  { path: 'workbench/:moduleId/edit/*', element: withSuspense(<FormEditorRoute />) },
  { path: 'workbench/:moduleId/view/*', element: withSuspense(<FormEditorRoute />) },
  { path: 'workbench/:moduleId/copy', element: withSuspense(<FormEditorRoute />) },
  { path: 'reports/:moduleId', element: withSuspense(<ReportViewerRoute />) },
  // 报表身份地址：编号进 URL，模块号由服务端解析（深链/分享/收藏都以它为准）
  { path: 'report/:reportId', element: withSuspense(<ReportIdentityRoute />) },
  { path: 'report-center', element: withSuspense(<ReportCenterPage />) },
  { path: 'report-center/inbox', element: withSuspense(<ReportInboxPage />) },
  { path: 'layout-designer/:moduleId', element: withSuspense(<LayoutDesignerPage />) },
  { path: 'search-center/:moduleId?', element: withSuspense(<SearchCenterPage />) },
  { path: 'import', element: withSuspense(<ImportPage />) },
  { path: 'print/:moduleId', element: withSuspense(<PrintViewPage />) },
  { path: 'bom-expand', element: withSuspense(<BomExpandPage />) },
  { element: <RequirePermission permission={moduleReadPermission(199901)} />, children: [{ path: 'car-summary', element: withSuspense(<CarSummaryPage />) }] },
  { path: 'detail-query/:moduleId', element: withSuspense(<DetailQueryPage />) },
  { element: <RequirePermission permission={moduleReadPermission(2102)} />, children: [{ path: 'my-tasks', element: withSuspense(<MyTasksPage />) }] },
  { element: <RequirePermission permission={moduleReadPermission(2101)} />, children: [{ path: 'workflow/design', element: withSuspense(<FlowDesignPage />) }] },
  { element: <RequirePermission permission={moduleReadPermission(2103)} />, children: [{ path: 'workflow/monitor', element: withSuspense(<FlowMonitorPage />) }] },
  // 助手全屏形态：普通工作区标签页（与半屏抽屉共用同一份会话，见 AssistantProvider）
  { path: 'assistant', element: <AssistantPage /> },
  // 会话管理：同样是普通工作区标签页（历史会话一览 / 重命名 / 归档 / 已归档删除）
  { path: 'assistant/sessions', element: <SessionAdminPage /> },
  // 工作助手管理（菜单组 31，见 ADR-030）：跨用户会话管理，走模块 3101 的读权限。
  // 与上面的个人侧是两件事——那一侧登录即可用、只看自己的会话。
  { element: <RequirePermission permission={moduleReadPermission(3101)} />, children: [
    { path: 'admin/assistant/sessions', element: withSuspense(<AdminSessionsPage />) },
  ] },
  // 3104 机制与工具总览：纯只读
  { element: <RequirePermission permission={moduleReadPermission(3104)} />, children: [
    { path: 'admin/assistant/mechanism', element: withSuspense(<MechanismOverviewPage />) },
  ] },
  // 3103 知识库管理：清单与删除（入库仍走运维通道）
  { element: <RequirePermission permission={moduleReadPermission(3103)} />, children: [
    { path: 'admin/assistant/kb', element: withSuspense(<KbAdminPage />) },
  ] },
  // 3102 模型与用量：模型增改/切换/密钥 + 用量看板
  { element: <RequirePermission permission={moduleReadPermission(3102)} />, children: [
    { path: 'admin/assistant/models', element: withSuspense(<ModelAdminPage />) },
  ] },
  // 3105 助手设置：提示词 / 日上限 / 单价兜底 / 熔断（保存即生效，不再走 appsettings）
  { element: <RequirePermission permission={moduleReadPermission(3105)} />, children: [
    { path: 'admin/assistant/settings', element: withSuspense(<AssistantSettingsPage />) },
  ] },
  { path: 'jobs', element: withSuspense(<JobPage />) },
  { path: 'settings/profile', element: withSuspense(<ProfilePage />) },
  { path: 'settings/:table', element: withSuspense(<SystemSettingsPage />) },
  { path: '*', element: <UnknownRoutePage /> },
]
