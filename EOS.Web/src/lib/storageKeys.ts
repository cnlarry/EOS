/**
 * 本地/会话存储键集中定义：
 * 消除跨文件裸字符串与字面巧合（如 erp-dashboard-recent 双份、erp-assistant-prefill 写入/消费各写一份）。
 * 新增存储键一律在本模块登记后引用。
 */

/** 主题（light/dark，AppShell）。 */
export const THEME_KEY = 'erp-theme'

/** 侧栏折叠状态（AppShell）。 */
export const SIDEBAR_COLLAPSED_KEY = 'erp-sidebar-collapsed'

/** 侧栏宽度（AppShell）。 */
export const SIDEBAR_WIDTH_KEY = 'erp-sidebar-width'

/** 首页最近使用模块（AppShell 写入 / DashboardPage 消费）。 */
export const RECENT_MODULES_KEY = 'erp-dashboard-recent'

/** 登录页记住用户名（LoginPage）。 */
export const REMEMBERED_USER_KEY = 'erp-remembered-user'

/** 助手表单预填（AssistantDock 写入 / FormEditorPage 消费），键 = 前缀 + moduleId。 */
export const ASSISTANT_PREFILL_PREFIX = 'erp-assistant-prefill-'

export function assistantPrefillKey(moduleId: number | string): string {
  return `${ASSISTANT_PREFILL_PREFIX}${moduleId}`
}

/** 工作区标签列表（AppShell 写入并恢复），键 = 前缀 + 用户标识。 */
export const WORKSPACE_TABS_PREFIX = 'erp-workspace-tabs-'

export function workspaceTabsKey(userId: string): string {
  return `${WORKSPACE_TABS_PREFIX}${userId}`
}

/** 工作区多标签特性开关：置为 `off` 即回退到单标签行为（默认开启）。 */
export const WORKSPACE_TABS_ENABLED_KEY = 'erp-workspace-tabs-enabled'
