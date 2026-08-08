/** 结构化查询条件（对应 EOS.API WorkbenchQueryCondition） */
export interface QueryCondition {
  field: string
  operator: string
  value: string
  valueTo: string
  logic: string
}

export const queryOperators = [
  ['eq', '等于'], ['ne', '不等于'], ['gt', '大于'], ['gte', '大于等于'],
  ['lt', '小于'], ['lte', '小于等于'], ['contains', '包含'], ['notcontains', '不包含'],
  ['startswith', '开头为'], ['endswith', '结尾为'], ['empty', '为空'], ['notempty', '不为空'],
  ['between', '区间'],
] as const

export function emptyQueryCondition(): QueryCondition {
  return { field: '', operator: 'eq', value: '', valueTo: '', logic: 'and' }
}
