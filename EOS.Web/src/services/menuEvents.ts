/** 菜单树/导航数据变更事件：菜单管理改动后派发，AuthProvider 监听并刷新 bootstrap（侧栏即时生效）。 */
export const MENU_CHANGED_EVENT = 'eos:menu-changed'

export function notifyMenuChanged(): void {
  window.dispatchEvent(new Event(MENU_CHANGED_EVENT))
}
