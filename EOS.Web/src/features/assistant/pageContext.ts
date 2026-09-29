/** 自动轻量页面处境：从当前路由解析，随 chat 请求上报，仅作为内容注入（非指令）。 */

export type AssistantPageType =
  | 'list'
  | 'view'
  | 'edit'
  | 'new'
  | 'copy'
  | 'config-fields'
  | 'config-datasource'
  | 'config-buttons'
  | 'config-effect'

/**
 * 配置页处境：正在配哪个对象。标识符由服务端按库内元数据白名单校验，
 * 只用于解释与定位，不作为权限依据。
 */
export interface AssistantConfigTarget {
  surface: 'fields' | 'datasource' | 'buttons' | 'effect'
  tableId?: string
  fieldId?: string
  actionId?: number
  effectKey?: string
}

export interface AssistantPageContext {
  moduleId?: number
  moduleTitle?: string
  pageType?: AssistantPageType
  docNo?: string
  configTarget?: AssistantConfigTarget
}

// 路由前缀 /workbench；view/edit 的记录主键以路径段表达（主键序），docNo 取自路径段
const FORM_PAGE_PATTERN = /^\/workbench\/(\d+)(?:\/(new|copy)|\/(edit|view)(?:\/(.*))?)?$/
const FIELD_EDITOR_PATTERN = /^\/admin\/fields\/([^/]+)(?:\/([^/]+))?\/?$/
const TABLE_FIELDS_PATTERN = /^\/admin\/tables\/([^/]+)\/fields\/?$/
const TABLE_ADMIN_PATTERN = /^\/admin\/tables\/?$/
const MENU_ADMIN_PATTERN = /^\/admin\/menus\/?$/

export function extractPageContext(pathname: string): AssistantPageContext | null {
  const match = FORM_PAGE_PATTERN.exec(pathname)
  if (match) {
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

  const fieldEditor = FIELD_EDITOR_PATTERN.exec(pathname)
  if (fieldEditor) {
    return {
      pageType: 'config-fields',
      configTarget: { surface: 'fields', tableId: fieldEditor[1], fieldId: fieldEditor[2] },
    }
  }

  const tableFields = TABLE_FIELDS_PATTERN.exec(pathname)
  if (tableFields) {
    return { pageType: 'config-fields', configTarget: { surface: 'fields', tableId: tableFields[1] } }
  }

  if (TABLE_ADMIN_PATTERN.test(pathname)) {
    return { pageType: 'config-fields', configTarget: { surface: 'fields' } }
  }

  // 自定义按钮与效果键都在菜单管理页内配置（模块级业务配置嵌在该页）
  if (MENU_ADMIN_PATTERN.test(pathname)) {
    return { pageType: 'config-buttons', configTarget: { surface: 'buttons' } }
  }

  return null
}
