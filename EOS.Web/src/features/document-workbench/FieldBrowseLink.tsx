import { Link } from 'react-router-dom'

interface FieldBrowseLinkProps {
  value: string
  /** 目标模块（BROWSE_M_IDX）；为空或 ≤0 时按纯文本渲染 */
  browseModuleId?: number | null
  /** 当前用户是否拥有目标模块的查看权限（bootstrap 权限位） */
  canBrowse?: boolean
}

/**
 * 字段浏览链接（方向二：映射到现代工作台）。
 *
 * 服务端已对 BROWSE_URL 模板做白名单校验；此处仅在目标模块可用且当前用户有权限时
 * 渲染为跳转到 `/document-workbench/{browseModuleId}` 的链接，权限最终由工作台自身把关。
 * 点击链接不触发行选择（stopPropagation）。
 */
export function FieldBrowseLink({ value, browseModuleId, canBrowse = true }: FieldBrowseLinkProps) {
  if (!value || !browseModuleId || browseModuleId <= 0 || !canBrowse) return <>{value}</>
  return (
    <Link className="erp-browse-link" to={`/document-workbench/${browseModuleId}`} onClick={(event) => event.stopPropagation()}>
      {value}
    </Link>
  )
}
