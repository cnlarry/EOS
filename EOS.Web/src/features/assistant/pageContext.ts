/** 自动轻量页面上下文（ADR-007 §3）：从当前路由提取，随 chat 请求上报，仅作为内容注入。 */

export type AssistantPageType = 'list' | 'view' | 'edit' | 'new' | 'copy'

export interface AssistantPageContext {
  moduleId?: number
  pageType?: AssistantPageType
  docNo?: string
}

const FORM_PAGE_PATTERN = /^\/document-workbench\/(\d+)(?:\/(new|edit|view|copy))?$/

export function extractPageContext(pathname: string, search: string): AssistantPageContext | null {
  const match = FORM_PAGE_PATTERN.exec(pathname)
  if (!match) return null

  const moduleId = Number(match[1])
  const pageType = (match[2] ?? 'list') as AssistantPageType
  const context: AssistantPageContext = { moduleId, pageType }

  if (search) {
    const keyParam = new URLSearchParams(search).get('key')
    if (keyParam) {
      try {
        const parsed: unknown = JSON.parse(keyParam)
        if (Array.isArray(parsed) && parsed.length > 0 && parsed.every(v => typeof v === 'string')) {
          // 单段主键视为单号；多段主键拼接展示
          context.docNo = parsed.length === 1 ? (parsed[0] as string) : (parsed as string[]).join('/')
        }
      } catch {
        // key 非法时不注入 docNo，不影响对话
      }
    }
  }

  return context
}
