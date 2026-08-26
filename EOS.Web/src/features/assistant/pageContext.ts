/** 自动轻量页面上下文（ADR-007 §3）：从当前路由提取，随 chat 请求上报，仅作为内容注入。 */

export type AssistantPageType = 'list' | 'view' | 'edit' | 'new' | 'copy'

export interface AssistantPageContext {
  moduleId?: number
  pageType?: AssistantPageType
  docNo?: string
}

// 2026-08-26：路由前缀 /workbench；view/edit 的记录主键以路径段表达（主键序），docNo 取自路径段
const FORM_PAGE_PATTERN = /^\/workbench\/(\d+)(?:\/(new|copy)|\/(edit|view)(?:\/(.*))?)?$/

export function extractPageContext(pathname: string): AssistantPageContext | null {
  const match = FORM_PAGE_PATTERN.exec(pathname)
  if (!match) return null

  const moduleId = Number(match[1])
  const pageType = (match[2] ?? match[3] ?? 'list') as AssistantPageType
  const context: AssistantPageContext = { moduleId, pageType }

  // 单段主键视为单号；多段主键拼接展示
  const pathKey = match[4]
  if (pathKey) {
    const parts = pathKey.split('/').filter(part => part !== '')
    if (parts.length > 0) context.docNo = parts.length === 1 ? parts[0] : parts.join('/')
  }

  return context
}
