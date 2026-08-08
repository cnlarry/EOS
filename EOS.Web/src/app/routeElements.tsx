import { lazy } from 'react'
import { useParams } from 'react-router-dom'

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
