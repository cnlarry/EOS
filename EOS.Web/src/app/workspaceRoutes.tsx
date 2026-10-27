import { Navigate, type RouteObject } from 'react-router-dom'
import { RequirePermission } from '../features/auth/RouteGuards'
import { moduleReadPermission } from '../features/auth/modulePermissions'
import {
  DashboardPage,
  DepotStockPolicyPage,
  FieldAuditPage,
  FieldEditorRoute,
  CarSummaryPage,
  ImportPage,
  MrpRecalcPage,
  CardBatchPage,
  AttendanceSimulatePage,
  AttendanceExtractPage,
  AttendanceAdjustWagePage,
  FallbackModulePage,
  LogAdminPage,
  BusinessFlowPage,
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
  ModuleGroupsPage,
  ModuleButtonsPage,
  ModuleActionsPage,
  AdminSessionsPage,
  MechanismOverviewPage,
  KbAdminPage,
  ModelAdminPage,
  AssistantSettingsPage,
  ReportCenterPage,
  ReportInboxPage,
  LayoutDesignerPage,
  BomProductListPage,
  BomProductEditorPage,
  BomProductNewPage,
  BomComparePage,
  BomTreePage,
  BomWhereUsedPage,
} from './lazyRoutes'
import { FieldAdminRoute, FormEditorRoute, ReportAdminRoute, ReportIdentityRoute, ReportViewerRoute, UnknownRoutePage, WorkbenchRoute } from './routeElements'
import { AssistantPage } from '../features/assistant/AssistantPage'
import { MyUsagePage } from '../features/assistant/MyUsagePage'
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
  { element: <RequirePermission permission={moduleReadPermission(230101)} />, children: [{ path: 'admin/menus', element: withSuspense(<MenuAdminPage />) }] },
  // 模块分组（模块 230102，根 23 系统管理）：为其它模块维护列表分组；定制页，不走统一表单与发布
  { element: <RequirePermission permission={moduleReadPermission(230102)} />, children: [
    { path: 'admin/module-groups', element: withSuspense(<ModuleGroupsPage />) },
  ] },
  // 自定义按钮（模块 230103，根 23 系统管理）：为其它统一工作台模块维护 MANUAL 按钮行；定制页，
  // 保存走服务端的 manual 作用域（只替换按钮行，不碰该模块的行为动作与校验规则）
  { element: <RequirePermission permission={moduleReadPermission(230103)} />, children: [
    { path: 'admin/module-buttons', element: withSuspense(<ModuleButtonsPage />) },
  ] },
  // 行为动作（模块 230104，根 23 系统管理）：页内两个页签共用一份草稿与一次提交；
  // 保存走服务端的 actions 作用域（只替换效果链动作 + 校验规则，不碰该模块的自定义按钮），保存后自动发布
  { element: <RequirePermission permission={moduleReadPermission(230104)} />, children: [
    { path: 'admin/module-actions', element: withSuspense(<ModuleActionsPage />) },
  ] },
  { element: <RequirePermission permission={moduleReadPermission(2305)} />, children: [
    { path: 'admin/groups', element: withSuspense(<UserGroupAdminPage />) },
    { path: 'admin/groups/:groupId/rights', element: withSuspense(<GroupRightsPage />) },

    { path: 'admin/groups/:groupId/button-rights', element: withSuspense(<GroupButtonRightsPage />) },
    { path: 'admin/groups/:groupId/members', element: withSuspense(<GroupMembersPage />) },
  ] },
  { element: <RequirePermission permission={moduleReadPermission(2303)} />, children: [{ path: 'admin/field-audit', element: withSuspense(<FieldAuditPage />) }] },
  // 日志管理（模块 2313，根 23 系统管理本级）：读磁盘上的运行日志文件；
  // 打包下载由服务端另行要求设置权限
  { element: <RequirePermission permission={moduleReadPermission(2313)} />, children: [{ path: 'admin/logs', element: withSuspense(<LogAdminPage />) }] },
  // 操作审计（模块 2316，根 23 系统管理本级）**没有前端页面**：它是统一工作台的只读承载页
  // （`M_URL=/workbench`，见迁移 354），列表与浏览态由 `/workbench/2316` 提供；
  // 与日志管理 2313 各有一个模块号，故权限仍可分别授予。
  // 业务流程图（模块 2314，23 系统管理直下）：按字段数据来源与已登记单据关系画模块间的单据走向，只读
  { element: <RequirePermission permission={moduleReadPermission(2314)} />, children: [
    { path: 'admin/business-flow', element: withSuspense(<BusinessFlowPage />) },
  ] },
  { element: <RequirePermission permission={moduleReadPermission(110310)} />, children: [{ path: 'admin/depot-stock-policy', element: withSuspense(<DepotStockPolicyPage />) }] },
  { element: <RequirePermission permission={moduleReadPermission(2306)} />, children: [
    { path: 'admin/users', element: withSuspense(<UserAdminPage />) },
    { path: 'admin/users/:userId/rights', element: withSuspense(<UserRightsPage />) },

    { path: 'admin/users/:userId/button-rights', element: withSuspense(<UserButtonRightsPage />) },
  ] },
  { path: 'admin/report-setup', element: withSuspense(<ReportAdminRoute />) },
  // 产品 BOM（模块 1204）：定制承载面（ADR-039 D1/D10）。
  // 三个视图共用 1204 的读权限；单层维护仍走 `/workbench/1204/view/...`，
  // 复用统一表单与写入管线——权限、审计、效果链都不在这里重写。
  // 路径用静态段而不是 query：深链可分享、前进后退语义一致、`RequirePermission` 可按路径配。
  { element: <RequirePermission permission={moduleReadPermission(1204)} />, children: [
    { path: 'bom/product', element: withSuspense(<BomProductListPage />) },
    // 静态段在前：`new` / `where-used` 与动态段都是三段，靠顺序消歧
    { path: 'bom/product/new', element: withSuspense(<BomProductNewPage />) },
    { path: 'bom/product/where-used/:proNo', element: withSuspense(<BomWhereUsedPage />) },
    // 结构树就是浏览态：双击列表进 `/tree`；「编辑」按钮才进可写态。
    // 没有独立的浏览页——维护页只剩可写一种形态，少一层"只读渲染"的分支。
    { path: 'bom/product/:proNo/:edition/tree', element: withSuspense(<BomTreePage />) },
    { path: 'bom/product/:proNo/:edition/edit', element: withSuspense(<BomProductEditorPage />) },
    { path: 'bom/product/:proNo/:edition/compare/:otherEdition', element: withSuspense(<BomComparePage />) },
  ] },
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
  // 查询中心（模块 34）：跨模块检索入口，菜单可见性与页面可达性同一条读权限；
  // 页面内的模块清单与行范围另有各自的模块权限与数据范围，入口不等于放大权限。
  { element: <RequirePermission permission={moduleReadPermission(34)} />, children: [
    { path: 'search-center/:moduleId?', element: withSuspense(<SearchCenterPage />) },
  ] },
  // 基本资料导入（模块 230902，系统管理）：入口按模块读权限显示，写入按目标模块的新增权限逐次鉴权
  { element: <RequirePermission permission={moduleReadPermission(230902)} />, children: [
    { path: 'import', element: withSuspense(<ImportPage />) },
  ] },
  { path: 'print/:moduleId', element: withSuspense(<PrintViewPage />) },
  { element: <RequirePermission permission={moduleReadPermission(199901)} />, children: [{ path: 'car-summary', element: withSuspense(<CarSummaryPage />) }] },
  { element: <RequirePermission permission={moduleReadPermission(2102)} />, children: [{ path: 'my-tasks', element: withSuspense(<MyTasksPage />) }] },
  { element: <RequirePermission permission={moduleReadPermission(2101)} />, children: [{ path: 'workflow/design', element: withSuspense(<FlowDesignPage />) }] },
  { element: <RequirePermission permission={moduleReadPermission(2103)} />, children: [{ path: 'workflow/monitor', element: withSuspense(<FlowMonitorPage />) }] },
  // 助手全屏形态：普通工作区标签页（与半屏抽屉共用同一份会话，见 AssistantProvider）
  { path: 'assistant', element: <AssistantPage /> },
  // 会话管理：同样是普通工作区标签页（历史会话一览 / 重命名 / 归档 / 已归档删除）
  { path: 'assistant/sessions', element: <SessionAdminPage /> },
  // 我的用量：本人视角（花了自己的多少额度），登录即可看，不加模块权限门——
  // 它与 3102 的跨用户用量看板是两个视角，入口不同、数据范围不同
  { path: 'assistant/usage', element: <MyUsagePage /> },
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
  // 后台作业按模块拆分：每个模块一个承载页，MODULES.M_URL 与这里一一对应（迁移 428）。
  // 各自挂自己的读权限门——一个作业的浏览权不该带出另一个作业的入口。
  { element: <RequirePermission permission={moduleReadPermission(230901)} />, children: [
    { path: 'jobs/mrp-recalc', element: withSuspense(<MrpRecalcPage />) },
  ] },
  { element: <RequirePermission permission={moduleReadPermission(180218)} />, children: [
    { path: 'jobs/card-batch', element: withSuspense(<CardBatchPage />) },
  ] },
  { element: <RequirePermission permission={moduleReadPermission(180654)} />, children: [
    { path: 'jobs/attendance-simulate', element: withSuspense(<AttendanceSimulatePage />) },
  ] },
  { element: <RequirePermission permission={moduleReadPermission(180659)} />, children: [
    { path: 'jobs/attendance-extract', element: withSuspense(<AttendanceExtractPage />) },
  ] },
  { element: <RequirePermission permission={moduleReadPermission(180505)} />, children: [
    { path: 'jobs/attendance-adjust-wage', element: withSuspense(<AttendanceAdjustWagePage />) },
  ] },
  { path: 'settings/profile', element: withSuspense(<ProfilePage />) },
  { path: 'settings/:table', element: withSuspense(<SystemSettingsPage />) },
  { path: '*', element: <UnknownRoutePage /> },
]
