/**
 * 工作台浏览器路由工具（记录主键路径化）：
 *  - 前缀 `/workbench`；
 *  - view/edit 的记录主键以**路径段**表达（主键序，每段 URL 编码），段数即键列数，
 *    天然支持任意复合主键；`new`/`copy` 无主键段；
 *  - `from`（跨模块关联浏览来源）等导航上下文一律留 query，不进入资源路径；
 *  - API 路径 `/api/v1/document-workbench/...` 与本工具无关（非用户可见，保持不变）。
 */
export function workbenchList(moduleId: string | number): string {
  return `/workbench/${moduleId}`
}

export function workbenchNew(moduleId: string | number): string {
  return `/workbench/${moduleId}/new`
}

export function workbenchCopy(moduleId: string | number, key: string[]): string {
  const encoded = encodeURIComponent(JSON.stringify(key.map(trimKey)))
  return `/workbench/${moduleId}/copy?copyFrom=${encoded}`
}

export function workbenchView(moduleId: string | number, key: string[] | null | undefined, from?: string | number | null): string {
  const path = buildRecordPath('view', moduleId, key)
  return from ? `${path}?from=${from}` : path
}

export function workbenchEdit(moduleId: string | number, key: string[] | null | undefined): string {
  return buildRecordPath('edit', moduleId, key)
}

/**
 * 定长字符主键（nchar/char）存值带尾部填充空格（如 CURR_ID nchar(10) → 'RMB       '）：
 * 拼路径段前修剪尾空格（SQL Server `=` 比较忽略尾空格，修剪后查询仍精确命中），
 * 避免 URL 出现 `RMB%20%20%20...` 一长串编码空格。
 */
function trimKey(value: string): string {
  return value.trimEnd()
}

function buildRecordPath(action: 'view' | 'edit', moduleId: string | number, key: string[] | null | undefined): string {
  const segments = (key ?? []).map(value => encodeURIComponent(trimKey(value))).join('/')
  return `/workbench/${moduleId}/${action}${segments ? `/${segments}` : ''}`
}

/** 从路由 splat（useParams()['*']）解析主键值数组；无段（或空）返回 null。 */
export function parseWorkbenchKey(splat?: string): string[] | null {
  if (!splat) return null
  const parts = splat.split('/').filter(part => part !== '')
  if (parts.length === 0) return null
  return parts.map(part => {
    try {
      return decodeURIComponent(part)
    } catch {
      return part
    }
  })
}

/** 浏览器路径是否为工作台表单页（new/copy/edit/view），是则返回动作名，否则 null。 */
export function workbenchAction(pathname: string): 'new' | 'copy' | 'edit' | 'view' | null {
  const match = pathname.match(/^\/workbench\/\d+\/(new|copy|edit|view)/)
  return (match?.[1] as 'new' | 'copy' | 'edit' | 'view') ?? null
}

/** 从浏览器路径提取工作台模块 ID（列表/新增/编辑/浏览均可）；非工作台路径返回 null。 */
export function workbenchModuleId(pathname: string): string | null {
  const match = pathname.match(/^\/workbench\/(\d+)/)
  return match?.[1] ?? null
}