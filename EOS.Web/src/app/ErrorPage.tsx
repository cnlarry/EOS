import { IconArrowLeft, IconAlertTriangle } from '@tabler/icons-react'
import { isRouteErrorResponse, Link, useRouteError } from 'react-router-dom'

export function ErrorPage() {
  const error = useRouteError()
  const isNotFound = isRouteErrorResponse(error) && error.status === 404

  return (
    <main className="erp-error-page">
      <div className="container-tight text-center">
        <span className="erp-error-icon"><IconAlertTriangle size={36} /></span>
        <div className="display-4 fw-bold mt-4">{isNotFound ? '404' : '出现错误'}</div>
        <h1 className="h2 mt-3">{isNotFound ? '没有找到这个页面' : '暂时无法加载页面'}</h1>
        <p className="text-secondary mt-3">
          {isNotFound ? '页面可能已被移动，或者您输入的地址不正确。' : '请稍后重试；如果问题持续存在，请联系系统管理员。'}
        </p>
        <Link className="btn btn-primary mt-3" to="/dashboard">
          <IconArrowLeft size={18} /> 返回首页
        </Link>
      </div>
    </main>
  )
}
