/**
 * Frontend permission key factory: single place building the
 * `fallback-module.X.read` route guard keys. The server remains the final
 * authorization boundary (ModuleRouteValidator / CanBrowse); frontend keys
 * only gate routing and UI.
 */
export function moduleReadPermission(moduleId: number | string): string {
  return `fallback-module.${moduleId}.read`
}
