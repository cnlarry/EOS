import { lazy } from 'react'
import { useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { ReportAdminPage } from '../features/admin/ReportAdminPage'
import { ReportViewerPage } from '../features/reports/ReportViewerPage'
import { useAuth } from '../features/auth/authContext'
import { moduleReadPermission } from '../features/auth/modulePermissions'
import { withSuspense } from './suspense'

const DocumentWorkbenchPage = lazy(() => import('../features/document-workbench/DocumentWorkbenchPage').then((module) => ({ default: module.DocumentWorkbenchPage })))
const FormEditorPage = lazy(() => import('../features/document-workbench/FormEditorPage').then((module) => ({ default: module.FormEditorPage })))
const FormDesignerPage = lazy(() => import('../features/form-designer/FormDesignerPage'))
const FieldAdminPage = lazy(() => import('../features/field-admin/FieldAdminPage').then((module) => ({ default: module.FieldAdminPage })))

export function ForbiddenPage() {
  return <main className="erp-error-page"><div className="text-center"><div className="display-5 fw-bold">403</div><h1>没有访问权限</h1><p className="text-secondary">当前账号无权访问此页面。</p></div></main>
}

/**
 * 未定义地址的兜底页：标签地址匹配不到任何工作区路由时渲染它。
 * 标签可能来自上一次会话的持久化列表（地址已下线或改名），也可能来自外部链接；
 * 静默渲染空白面板会让用户以为页面坏了，给出地址才能自助处置（关掉该标签）。
 */
export function UnknownRoutePage() {
  const location = useLocation()
  return (
    <main className="erp-error-page">
      <div className="text-center">
        <div className="display-5 fw-bold">404</div>
        <h1>页面不存在</h1>
        <p className="text-secondary">
          当前标签没有对应的页面：<code>{`${location.pathname}${location.search}`}</code>
        </p>
      </div>
    </main>
  )
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
  const [searchParams] = useSearchParams()
  const navigate = useNavigate()
  const location = useLocation()
  // 设计态与表单页**同一个路由**（`?design=1`）：所见即所得的心智模型，不另开页面路由
  if (searchParams.get('design') === '1' && /^\d+$/.test(moduleId)) {
    const exit = () => {
      const next = new URLSearchParams(location.search)
      next.delete('design')
      const query = next.toString()
      navigate(`${location.pathname}${query.length > 0 ? `?${query}` : ''}`)
    }
    return withSuspense(<FormDesignerPage moduleId={Number(moduleId)} onExit={exit} />)
  }
  return <FormEditorPage key={moduleId} />
}

/** 报表定义维护（2201）：REPORT + REPORT_SORT 主子表。 */
export function ReportAdminRoute() {
  const { hasPermission } = useAuth()
  if (!hasPermission(moduleReadPermission(2201))) return <ForbiddenPage />
  return <ReportAdminPage />
}

/** 报表过滤条件设置（2205）已随  下线：参数定义并入报表定义资产，
 *  用户填值 SYSQR_USER 保留为运行态，管理写侧退役。 */

/** 报表查看器：跨模块打开时重置条件/分页/打印面板状态。 */
export function ReportViewerRoute() {
  const { moduleId = '' } = useParams()
  return <ReportViewerPage key={moduleId} />
}
