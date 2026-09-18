import { Link } from 'react-router-dom'
import { tabLinkHandler, useOpenTab } from '../../components/layout/WorkspaceNavContext'
import { workbenchList, workbenchView } from './workbenchPath'

interface FieldBrowseLinkProps {
  value: string
  /** 目标模块（BROWSE_M_IDX）；为空或 ≤0 时按纯文本渲染 */
  browseModuleId?: number | null
  /** 记录浏览键源列（服务端按目标主键序解析）；为空时降级为目标模块列表链接 */
  browseKeyFields?: string[] | null
  /** 当前行数据（用于组装目标记录主键值数组） */
  row?: Record<string, unknown>
  /** 来源工作台模块 ID（FieldBrowseLink 所在列表的 moduleId）：目标记录浏览返回与面包屑按来源呈现 */
  fromModuleId?: string | number | null
  /** 当前用户是否拥有目标模块的查看权限（bootstrap 权限位） */
  canBrowse?: boolean
}

/**
 * 字段浏览链接（跨模块关联单据浏览）。
 *
 * 服务端已对 BROWSE_URL 模板受控解析并下发 browseModuleId / browseKeyFields
 * （目标模块可达性 + 主键映射 + 白名单均服务端判定；权限最终由目标 /view、/record
 * 端点重新授权）。此处：
 *  - 目标模块可用且有权限时，优先跳转目标记录浏览
 *    `/workbench/{m}/view/{主键段...}?from={来源模块}`，并在新标签中打开，来源工作台保持不动；
 *  - 键无法完整组装（目标不可记录浏览/键值缺失）时降级为目标模块列表 `/workbench/{m}`；
 *  - 无权限或目标不可达时按纯文本渲染。
 * 点击链接不触发行选择（stopPropagation）。
 */
export function FieldBrowseLink({ value, browseModuleId, browseKeyFields, row, fromModuleId, canBrowse = true }: FieldBrowseLinkProps) {
  const openTab = useOpenTab()
  if (!value || !browseModuleId || browseModuleId <= 0 || !canBrowse) return <>{value}</>
  const recordKeys = browseKeyFields?.length && row
    ? browseKeyFields.map(key => String(row[key] ?? '')).filter(keyValue => keyValue !== '')
    : []
  const isRecordLink = recordKeys.length > 0 && recordKeys.length === (browseKeyFields?.length ?? 0)
  const target = isRecordLink
    ? workbenchView(browseModuleId, recordKeys, fromModuleId)
    : workbenchList(browseModuleId)
  const handleClick = tabLinkHandler(openTab, target)
  return (
    <Link
      className="erp-browse-link"
      to={target}
      onClick={(event) => {
        event.stopPropagation()
        handleClick(event)
      }}
    >
      {value}
    </Link>
  )
}