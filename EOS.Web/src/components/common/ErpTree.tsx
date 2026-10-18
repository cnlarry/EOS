import { IconChevronRight } from '@tabler/icons-react'
import { Fragment, type CSSProperties, type ReactNode } from 'react'
import { childPad, dotLeft, groupPad, lineTree } from '../layout/menuDepth'

/**
 * 统一树视图。
 *
 * **画法直接复用 230101（菜单管理）那一套**，不另造几何：`--menu-gpad` / `--menu-cpad` /
 * `--menu-dot` / `--menu-line` 四个深度变量由 `menuDepth` 算出，虚线、圆点、箭头旋转
 * 全在 `app.css` 的 `.erp-nav-*` 与 `.erp-menu-tree` 里定义——那套坐标是逐层对过像素的，
 * 换一层实现就又得重新对齐。
 *
 * 与 230101 的一处差别：**一级节点用调用方给的图标**（BOM 树用文件夹的展开/折叠两态），
 * 深层用箭头。230101 的一级是模块图标，语义上就是"这个节点是什么"，这里同理。
 *
 * 组件只负责画：展开集合与选中项由调用方持有，节点树也由调用方组装（扁平行转嵌套
 * 只是个小函数，放在业务侧能顺手复用同一份层级数据）。
 */
export interface ErpTreeNode {
  /** 节点标识；同一次渲染内唯一。 */
  id: string
  label: ReactNode
  /** 右侧辅助信息（用量、计数等）。 */
  aux?: ReactNode
  title?: string
  children?: ErpTreeNode[]
}

interface ErpTreeProps {
  /** 根节点数组（通常只有一项）。 */
  nodes: ErpTreeNode[]
  /** 已展开节点的 id 集合。 */
  expandedIds: ReadonlySet<string>
  selectedId?: string | null
  onSelect?: (id: string) => void
  onToggle?: (id: string) => void
  /** 一级节点的图标，按展开状态给两态；不传则该层也用箭头。 */
  rootIcon?: (expanded: boolean) => ReactNode
  ariaLabel?: string
  empty?: ReactNode
}

export function ErpTree({
  nodes,
  expandedIds,
  selectedId = null,
  onSelect,
  onToggle,
  rootIcon,
  ariaLabel = '树',
  empty,
}: ErpTreeProps) {
  if (nodes.length === 0) return <>{empty ?? null}</>

  const renderList = (list: ErpTreeNode[], depth: number): ReactNode => list.map(node => {
    const children = node.children ?? []
    const hasChildren = children.length > 0
    const expanded = expandedIds.has(node.id)
    const active = node.id === selectedId
    // 深度变量的口径与 230101 完全一致：传给 menuDepth 的是「第几层」（1 起）
    const groupStyle = depth > 0 && hasChildren
      ? ({ '--menu-gpad': `${groupPad(depth + 1)}px` } as CSSProperties)
      : undefined
    const childStyle = depth > 0 && !hasChildren
      ? ({ '--menu-cpad': `${childPad(depth + 1)}px`, '--menu-dot': `${dotLeft(depth + 1)}px` } as CSSProperties)
      : undefined

    const label = (
      <span className="nav-link-title">
        <span>{node.label}</span>
        {node.aux != null ? <span className="erp-tree-inline-aux">{node.aux}</span> : null}
      </span>
    )

    if (hasChildren) {
      return (
        <Fragment key={node.id}>
          <div
            className={`erp-nav-group erp-nav-group-depth-${depth + 1}`}
            style={groupStyle}
            role="treeitem"
            aria-level={depth + 1}
            aria-expanded={expanded}
            aria-selected={active}
          >
            <button
              type="button"
              className={`nav-link erp-nav-group-toggle${active ? ' group-active' : ''}`}
              aria-expanded={expanded}
              title={node.title}
              onClick={() => onSelect?.(node.id)}
            >
              {depth === 0 && rootIcon ? (
                <span
                  className="nav-link-icon"
                  role="button"
                  tabIndex={-1}
                  aria-label={expanded ? '折叠' : '展开'}
                  onClick={event => { event.stopPropagation(); onToggle?.(node.id) }}
                >
                  {rootIcon(expanded)}
                </span>
              ) : (
                <span
                  className="erp-tree-toggle-hit"
                  role="button"
                  tabIndex={-1}
                  aria-label={expanded ? '折叠' : '展开'}
                  onClick={event => { event.stopPropagation(); onToggle?.(node.id) }}
                >
                  <IconChevronRight className="erp-nav-chevron" size={13} />
                </span>
              )}
              {label}
            </button>
          </div>
          {expanded ? (
            <div className="erp-nav-children" style={{ '--menu-line': `${lineTree(depth + 2)}px` } as CSSProperties}>
              {renderList(children, depth + 1)}
            </div>
          ) : null}
        </Fragment>
      )
    }

    return (
      <div key={node.id} role="treeitem" aria-level={depth + 1} aria-selected={active}>
        <button
          type="button"
          className={`nav-link erp-nav-child${active ? ' active' : ''}`}
          style={childStyle}
          title={node.title}
          onClick={() => onSelect?.(node.id)}
        >
          {label}
        </button>
      </div>
    )
  })

  return (
    <div className="erp-menu-tree erp-tree" role="tree" aria-label={ariaLabel}>
      {renderList(nodes, 0)}
    </div>
  )
}
