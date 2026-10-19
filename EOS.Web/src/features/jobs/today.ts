/** 本地日期（yyyy-MM-dd）：日期输入框的默认值，不能用 toISOString（按 UTC 截断会差一天）。 */
export function todayInputValue(): string {
  const now = new Date()
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`
}

/** 本地月份（yyyyMM）：月份输入框的默认值。 */
export function currentMonthValue(): string {
  const now = new Date()
  return `${now.getFullYear()}${pad(now.getMonth() + 1)}`
}

function pad(value: number): string {
  return String(value).padStart(2, '0')
}
