/** 多级菜单深度几何（px）——侧栏（AppShell）与菜单管理（MenuAdminPage）共用。
 *  2~4 层为既有验收像素值；5 层起按等差（每层 +19px）延伸，天然支持任意层级。
 *  ::before 的 left 相对元素自身：侧栏行含 7px 外边距、菜单管理行无外边距，
 *  故虚线基准线两侧不同，用 lineSidebar / lineTree 区分。 */
export const groupPad = (d: number): number => (d >= 2 ? 40 + 19 * (d - 2) : 11)
export const childPad = (d: number): number => {
  if (d <= 2) return 59
  if (d === 3) return 78
  if (d === 4) return 96.5
  return 96.5 + 19 * (d - 4)
}
export const dotLeft = (d: number): number => {
  if (d <= 2) return 46.5
  if (d === 3) return 65.5
  if (d === 4) return 84
  return 84 + 19 * (d - 4)
}
export const lineSidebar = (d: number): number => {
  if (d <= 2) return 28
  if (d === 3) return 53
  if (d === 4) return 72.5
  return 72.5 + 19 * (d - 4)
}
export const lineTree = (d: number): number => {
  if (d <= 2) return 20
  if (d === 3) return 46.5
  if (d === 4) return 65.5
  return 65.5 + 19 * (d - 4)
}