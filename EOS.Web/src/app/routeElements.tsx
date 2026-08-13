import { lazy } from 'react'
import { useParams } from 'react-router-dom'
import { ReportAdminPage } from '../features/admin/ReportAdminPage'
import { PrintSetupPage } from '../features/admin/PrintSetupPage'
import { ReportViewerPage } from '../features/reports/ReportViewerPage'
import { useAuth } from '../features/auth/authContext'

const DocumentWorkbenchPage = lazy(() => import('../features/document-workbench/DocumentWorkbenchPage').then((module) => ({ default: module.DocumentWorkbenchPage })))
const FormEditorPage = lazy(() => import('../features/document-workbench/FormEditorPage').then((module) => ({ default: module.FormEditorPage })))
const FieldAdminPage = lazy(() => import('../features/field-admin/FieldAdminPage').then((module) => ({ default: module.FieldAdminPage })))

export function ForbiddenPage() {
  return <main className="erp-error-page"><div className="text-center"><div className="display-5 fw-bold">403</div><h1>没有访问权限</h1><p className="text-secondary">当前账号无权访问此页面。</p></div></main>
}

/**
 * 以 moduleId 作为 key 强制重挂载，避免上一个模块的排序/分页/查询条件等状态
 * 带进下一个模块（否则会以无效 sortField 请求导致“排序字段无效”）。
 */
export function WorkbenchRoute() {
  const { moduleId = '' } = useParams()
  return <DocumentWorkbenchPage key={moduleId} />
}

/** 数据表切换（tableId 变化）时同样重置字段维护页的搜索/分页状态 */
export function FieldAdminRoute() {
  const { tableId = '' } = useParams()
  return <FieldAdminPage key={tableId} />
}

/** 表单编辑页（新增/编辑）跨模块打开时同样重置草稿/校验状态 */
export function FormEditorRoute() {
  const { moduleId = '' } = useParams()
  return <FormEditorPage key={moduleId} />
}

/** 报表定义维护（2201）：REPORT + REPORT_SORT 主子表。 */
export function ReportAdminRoute() {
  const { hasPermission } = useAuth()
  if (!hasPermission('legacy-module.2201.read')) return <ForbiddenPage />
  return <ReportAdminPage />
}

/** 页头/表尾/页脚维护（2202/2203/2204）：任一相关模块有浏览权即可进入。 */
export function PrintSetupRoute() {
  const { hasPermission } = useAuth()
  const { tab = '' } = useParams()
  const allowed = ['2202', '2203', '2204']
    .some((id) => hasPermission(`legacy-module.${id}.read`))
  if (!allowed) return <ForbiddenPage />
  return <PrintSetupPage key={tab} />
}

/** 报表查看器：跨模块打开时重置条件/分页/打印面板状态。 */
export function ReportViewerRoute() {
  const { moduleId = '' } = useParams()
  return <ReportViewerPage key={moduleId} />
}
