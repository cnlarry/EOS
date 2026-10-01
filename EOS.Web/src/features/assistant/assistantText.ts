import type { SituationSnapshot } from './types'

/**
 * 助手界面的纯文案函数：把服务端处境快照翻译成人话。
 * 与 React 无关，抽屉与全屏面板共用，也便于单独断言。
 */

const PAGE_TYPE_LABELS: Record<string, string> = {
  list: '列表',
  view: '查看',
  edit: '编辑',
  new: '新建',
  copy: '复制',
  'config-fields': '字段配置页',
  'config-datasource': '数据源配置页',
  'config-buttons': '按钮配置页',
  'config-effect': '效果配置页',
}

export function pageTypeLabel(pageType: string | null): string {
  if (!pageType) return ''
  return PAGE_TYPE_LABELS[pageType] ?? pageType
}

/** 处境快照结构校验：响应意外（非快照）时按"读不到处境"处理，不让界面崩掉。 */
export function isSituationSnapshot(value: unknown): value is SituationSnapshot {
  if (typeof value !== 'object' || value === null) return false
  const candidate = value as Partial<SituationSnapshot>
  return typeof candidate.where === 'object' && candidate.where !== null
    && typeof candidate.digest === 'object' && candidate.digest !== null
    && Array.isArray(candidate.digest?.items)
    && typeof candidate.identity === 'object' && candidate.identity !== null
    && typeof candidate.pending === 'object' && candidate.pending !== null
}

/** 「你在哪」：由服务端校验后的处境给出模块与页面，路由信息不含业务事实。 */
export function describeWhere(snapshot: SituationSnapshot): string {
  const where = snapshot.where
  const label = pageTypeLabel(where.pageType)
  if (where.moduleTitle) {
    return `你在「${where.moduleTitle}」的${label || '页面'}${where.docNo ? `，当前单据 ${where.docNo}` : ''}。`
  }

  if (label) return `你在${label}。`
  return '你当前不在具体的业务页面上。'
}

/** 摘要条目类别的中文标签（类别由服务端给出，前端不猜语义）。 */
export function digestKindLabel(kind: string): string {
  switch (kind) {
    case 'overdue': return '滞留未批核'
    case 'blocked-now': return '此刻办不下去'
    default: return '最近被拒'
  }
}

/** 「压着什么 / 哪件不对」：只报非零事实，避免打开就看到一片 0。 */
export function describePending(snapshot: SituationSnapshot): string {
  const overdue = snapshot.digest.items.filter(item => item.kind === 'overdue').length
  const blocked = snapshot.digest.items.filter(item => item.kind === 'blocked-now').length
  const rejected = snapshot.digest.items.filter(item => item.kind === 'rejected').length
  const parts: string[] = []
  if (overdue > 0) parts.push(`有 ${overdue} 条单据滞留未批核`)
  if (blocked > 0) parts.push(`有 ${blocked} 条单据此刻办不下去`)
  if (rejected > 0) parts.push(`最近有 ${rejected} 次操作被拒绝`)
  if (snapshot.pending.myApproval > 0 || snapshot.pending.startedInFlight > 0) {
    parts.push(`待我审批 ${snapshot.pending.myApproval} 条、我发起在途 ${snapshot.pending.startedInFlight} 条`)
  }

  return parts.length > 0 ? `${parts.join('；')}。` : '审批待办与滞留单据当前都是空的。'
}
