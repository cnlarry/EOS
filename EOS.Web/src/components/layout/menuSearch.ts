import type { NavigationItem } from '../../features/auth/types'

/** 菜单搜索命中项：命中的节点，以及从顶层到该节点的面包屑文字。 */
export interface MenuSearchHit {
  item: NavigationItem
  breadcrumb: string
}

/**
 * 菜单搜索：按显示名 / 模块编号 / 别名匹配，**只认有落点（route）的节点**。
 *
 * - 导航树本身已是权限过滤后的结果（无权限的模块不在树上，`M_TAG=0` 的隐藏模块在服务端 SQL 里就被排除），
 *   所以这里不需要再做权限判断：搜得到的都是能进的；
 * - 叶子节点一律有落点，按模块编号即可直达；
 * - 「既是分组又有自己页面」的节点也带落点，同样按编号搜得到（分组自身没有页面时不参与匹配，
 *   免得给出一个点不开的结果）；
 * - 命中与否都继续下钻子节点，结果按树的中序遍历顺序返回，最多 `limit` 条；
 * - 输入的就是某个模块的完整编号时，把该模块提到首位——回车（打开第一项）因此就是「编号直达」。
 */
export function searchMenu(items: NavigationItem[], query: string, limit = 50): MenuSearchHit[] {
  const needle = query.trim().toLowerCase()
  if (!needle) return []
  const hits: MenuSearchHit[] = []
  const walk = (nodes: NavigationItem[], trail: string[]) => {
    for (const item of nodes) {
      if (item.route) {
        const label = item.label.toLowerCase()
        const moduleId = item.moduleId != null ? String(item.moduleId) : ''
        const alias = (item.alias ?? '').toLowerCase()
        if (label.includes(needle) || moduleId.includes(needle) || alias.includes(needle)) {
          hits.push({ item, breadcrumb: [...trail, item.label].join(' / ') })
        }
      }
      if (item.children?.length) walk(item.children, [...trail, item.label])
    }
  }
  walk(items, [])
  const exact = hits.findIndex((hit) => hit.item.moduleId != null && String(hit.item.moduleId) === needle)
  if (exact > 0) hits.unshift(...hits.splice(exact, 1))
  return hits.slice(0, limit)
}
