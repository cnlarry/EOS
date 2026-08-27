import { useEffect } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { usePageBreadcrumb } from '../../components/layout/PageBreadcrumbContext'
import { ReportRightsMatrix } from '../rights-admin/ReportRightsMatrix'
import { RightsMatrix } from '../rights-admin/RightsMatrix'

function useUserId() {
  const { userId } = useParams<{ userId: string }>()
  return (userId ?? '').trim()
}

/** 子页上抛面包屑：系统管理 > 用户权限设定 > {用户名} {动作}。 */
function useUserBreadcrumb(id: string, actionLabel: string) {
  const { setBreadcrumb } = usePageBreadcrumb()
  useEffect(() => {
    setBreadcrumb({
      leads: [{ label: '系统管理' }, { label: '用户权限设定', to: '/admin/users' }],
      title: `${id} ${actionLabel}`,
    })
  }, [setBreadcrumb, id, actionLabel])
}

/** 用户模块权限完整页面（2306 定制页子页）。 */
export function UserRightsPage() {
  const id = useUserId()
  const navigate = useNavigate()
  useUserBreadcrumb(id, '权限')
  return (
    <RightsMatrix
      open
      variant="page"
      mode="user"
      targetId={id}
      title={`用户模块权限：${id}`}
      onClose={() => navigate('/admin/users')}
    />
  )
}

/** 用户报表权限完整页面（2306 定制页子页）。 */
export function UserReportRightsPage() {
  const id = useUserId()
  const navigate = useNavigate()
  useUserBreadcrumb(id, '报表权限')
  return (
    <ReportRightsMatrix
      open
      variant="page"
      mode="user"
      targetId={id}
      title={`用户报表权限：${id}`}
      onClose={() => navigate('/admin/users')}
    />
  )
}
