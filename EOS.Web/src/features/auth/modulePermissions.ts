/**
 * 前端权限键集中定义（代码质量批 3 E3 收编）：
 * 消除 5 个文件散落的 `legacy-module.X.read` 字符串；服务端仍以
 * ModuleRouteValidator/CanBrowse 为最终权限边界，前端键只用于路由与 UI 门。
 */
export function moduleReadPermission(moduleId: number | string): string {
  return `legacy-module.${moduleId}.read`
}
