import { lazy, useEffect } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { LoadingState, EmptyState } from '../components/common/AsyncState'
import { ReportAdminPage } from '../features/admin/ReportAdminPage'
import { ReportViewerPage } from '../features/reports/ReportViewerPage'
import { useAuth } from '../features/auth/authContext'
import { moduleReadPermission } from '../features/auth/modulePermissions'
import { apiClient } from '../services/api'
import { ApiError } from '../types/api'
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

/**
 * 老模块地址 `/reports/:moduleId`：**跳转段**，不再直接渲染查看器。
 *
 * 报表身份进 URL 之后，"按模块打开"就只剩一个问题要回答——这个模块该打开哪一张？
 * 规则与查看器内部一致（最近用过 → 模块默认 → 清单第一张），答完立刻把地址换成报表身份地址。
 * 把这一步放在**查看器之前**而不是查看器里，是为了不出现"先渲染一次、再换地址重挂一次"：
 * 重挂会把刚取的数、刚填的条件全丢掉。
 */
export function ReportViewerRoute() {
  const { moduleId = '' } = useParams()
  const location = useLocation()
  const navigate = useNavigate()
  const settings = useQuery({
    queryKey: ['report', moduleId, 'print-settings'],
    queryFn: () => apiClient.get<{ reports: { reportId: string; isDefault: boolean }[]; userSettings: { reportId?: string } | null }>(
      `/reports/${moduleId}/print-settings`,
    ),
    enabled: moduleId !== '',
  })

  useEffect(() => {
    if (!moduleId || !settings.data) return
    const user = settings.data.userSettings
    const report = (user?.reportId ? settings.data.reports.find((item) => item.reportId === user.reportId) : undefined)
      ?? settings.data.reports.find((item) => item.isDefault)
      ?? settings.data.reports[0]
    if (!report) return
    navigate(`/report/${encodeURIComponent(report.reportId)}${location.search}`, { replace: true })
  }, [moduleId, settings.data, navigate, location.search])

  if (!moduleId) return <ForbiddenPage />
  if (settings.isError) return <EmptyState title="报表打不开" description="无法读取该模块的报表清单。" />
  // 模块下没有可见报表（归属搬走后就空了，如原来的报表承载页）：给一句话，不要一直转圈——
  // 一直转圈会被当成"系统卡住"，而真相是"这个模块已经没有报表了，报表在报表中心"。
  if (settings.isSuccess && (settings.data.reports?.length ?? 0) === 0) {
    return <EmptyState title="本模块没有报表" description="报表已按业务模块归位，请从报表中心或表单工具条的「报表」动作打开。" />
  }
  return <LoadingState />
}

/**
 * 报表身份入口 `/report/:reportId`：报表编号就是这条地址的身份。
 *
 * 模块号不在这条 URL 里（也**不该**由调用方塞进来）：必须由服务端按报表编号解析出归属模块，
 * 再拿这个模块号去渲染查看器——否则地址栏里改一个模块号就能换一套权限看同一张报表。
 * 解析失败（无此报表 404 / 无权限 403）时给出具名错误态，不静默落回默认报表。
 */
export function ReportIdentityRoute() {
  const { reportId = '' } = useParams()
  const identity = useQuery({
    queryKey: ['report-identity', reportId],
    queryFn: () => apiClient.get<ReportIdentityData>(`/report/${encodeURIComponent(reportId)}`),
    enabled: reportId !== '',
  })

  if (!reportId) return <ForbiddenPage />
  if (identity.isLoading) return <LoadingState />
  if (identity.isError || !identity.data) {
    return <EmptyState title="报表打不开" description={describeReportOpenError(identity.error)} />
  }
  return (
    <ReportViewerPage
      key={`${identity.data.moduleId}:${reportId}`}
      moduleIdOverride={String(identity.data.moduleId)}
      reportIdOverride={identity.data.reportId}
    />
  )
}

interface ReportIdentityData {
  moduleId: number
  reportId: string
  reportName: string
  moduleName: string
  siblings: { reportId: string; reportName: string; isDefault: boolean }[]
}

/** 打不开时的具名说明：无此报表（404）与无权限（403）是两回事，不能都写成"打开失败"。 */
function describeReportOpenError(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 404) return '没有这张报表（报表编号不存在，或已被删除）。'
    if (error.status === 403) return '当前账号对这张报表所属的模块没有浏览权限。'
  }
  return '报表打开失败，请稍后重试或联系管理员。'
}
