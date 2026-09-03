/**
 * 页面标题/分区元数据：
 * 键与 router.tsx 的静态路由 path 一一对应，AppShell 面包屑兜底消费。
 * 新增静态页面时在此登记标题与分区，避免改 path 后面包屑静默失效；
 * 带参子页（workbench/fields/groups 等）经 usePageBreadcrumb/useFormBreadcrumb 上抛，不走本表。
 */
export interface PageMeta {
  section: string
  title: string
}

export const PAGE_META: Record<string, PageMeta> = {
  '/dashboard': { section: '首页', title: '首页' },
  '/admin/tables': { section: '系统管理', title: '数据表维护' },
  '/admin/menus': { section: '系统管理', title: '菜单管理' },
  '/admin/groups': { section: '系统管理', title: '用户组管理' },
  '/admin/users': { section: '系统管理', title: '用户管理' },
  '/settings/profile': { section: '系统设置', title: '个人设置' },
}
