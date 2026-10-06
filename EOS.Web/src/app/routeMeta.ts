/**
 * 页面标题/分区元数据：
 * 键与 router.tsx 的静态路由 path 一一对应，AppShell 面包屑兜底消费。
 *
 * **标题的优先级：模块页取模块名（`MODULES.M_DESC`），本表只兜底非模块页面**——
 * 一个页面叫什么，唯一真源是元数据里的模块名：模块在 2301 改名后，标签与面包屑要随之变，
 * 因此本表的 `title` 对**有对应模块的路径**不生效（只在其导航叶子到达前顶一帧，见 AppShell 的
 * `moduleTitleForPath`），数值应与模块名保持一致，免得装载瞬间闪一个旧名。
 * `section`（分区）始终取本表：模块在导航树里归属的分区与之一致。
 *
 * 新增静态页面时在此登记标题与分区，避免改 path 后面包屑静默失效；
 * 带参子页（workbench/fields/groups 等）经 usePageBreadcrumb/useFormBreadcrumb 上抛，不走本表。
 */
export interface PageMeta {
  section: string
  title: string
}

export const PAGE_META: Record<string, PageMeta> = {
  '/dashboard': { section: '首页', title: '首页' },
  '/admin/tables': { section: '系统管理', title: '数据表、字段维护' },
  '/admin/business-flow': { section: '系统管理', title: '业务流程图' },
  '/admin/menus': { section: '系统管理', title: '模块管理' },
  '/admin/groups': { section: '系统管理', title: '用户组管理' },
  '/admin/users': { section: '系统管理', title: '用户权限设定' },
  '/settings/profile': { section: '系统设置', title: '个人设置' },
  '/assistant': { section: '工作助手', title: '工作助手' },
  '/assistant/sessions': { section: '工作助手', title: '我的会话' },
  // 工作助手管理（菜单组 31，见 ADR-030）：跨用户视角，与上面的个人会话管理是两件事
  '/admin/assistant/sessions': { section: '工作助手管理', title: '会话管理' },
  '/admin/assistant/mechanism': { section: '工作助手管理', title: '机制与工具总览' },
  '/admin/assistant/kb': { section: '工作助手管理', title: '知识库管理' },
  '/admin/assistant/models': { section: '工作助手管理', title: '模型与用量' },
  '/admin/assistant/settings': { section: '工作助手管理', title: '助手设置' },
}
