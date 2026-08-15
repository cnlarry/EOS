/**
 * 单据明细行金额计算（前端预览，与服务端 AmountCalculator 完全等价）。
 *
 * 对齐旧系统 JScript calc_row_amount + Round：
 * - TAX_TYPE='I'（内含税）：价税合计 = 数量×单价×折扣；金额 = 价税合计/(1+税率)；税额 = 价税合计-金额；
 * - TAX_TYPE='O'（外含税）：金额 = 数量×单价×折扣；税额 = 金额×税率；价税合计 = 金额+税额；
 * - TAX_TYPE='N'/其它（不含税）：金额 = 价税合计 = 数量×单价×折扣；税额 = 0。
 * 金额统一保留 2 位小数（四舍五入，远离零，对齐 C# MidpointRounding.AwayFromZero）。
 * 仅作预览：保存时服务端 RecalculateDetailAmounts 权威复算覆盖。
 */

export interface AmountRow {
  amount: number
  taxSum: number
  amountTax: number
}

/** 金额预览联动触发字段（变更任一即重算该行金额） */
export const AMOUNT_TRIGGER_KEYS = new Set(['QTY', 'PRICE', 'TAX_RATE', 'TAX_TYPE', 'REBATE'])

/** 金额列（预览展示，只读） */
export const AMOUNT_COLUMN_KEYS = new Set(['AMOUNT', 'TAX_SUM', 'AMOUNT_TAX'])

function toNumber(value: unknown): number | null {
  if (value === null || value === undefined) return null
  if (typeof value === 'number') return Number.isNaN(value) ? null : value
  const text = String(value).trim()
  if (text === '') return null
  const parsed = Number(text)
  return Number.isNaN(parsed) ? null : parsed
}

/** 对齐 C# Math.Round(value, 2, MidpointRounding.AwayFromZero)：负数半值远离零。 */
function round2(value: number): number {
  return Math.sign(value) * Math.round(Math.abs(value) * 100) / 100
}

export function calculateAmount(
  qty: number | null,
  price: number | null,
  taxRatePercent: number | null,
  taxType: string | null,
  rebatePercent: number | null,
): AmountRow {
  const quantity = qty ?? 0
  const unitPrice = price ?? 0
  const taxRate = (taxRatePercent ?? 0) / 100
  const rebate = (rebatePercent ?? 100) / 100
  const raw = quantity * unitPrice * rebate
  const type = (taxType ?? '').trim().toUpperCase()
  if (type === 'I') {
    const amountTaxIncluded = round2(raw)
    const amountIncluded = round2(amountTaxIncluded / (1 + taxRate))
    return { amount: amountIncluded, taxSum: round2(amountTaxIncluded - amountIncluded), amountTax: amountTaxIncluded }
  }
  if (type === 'O') {
    const amountOutside = round2(raw)
    const taxSumOutside = round2(amountOutside * taxRate)
    return { amount: amountOutside, taxSum: taxSumOutside, amountTax: round2(amountOutside + taxSumOutside) }
  }
  const amountNone = round2(raw)
  return { amount: amountNone, taxSum: 0, amountTax: amountNone }
}

/** 按表单字段元数据从行/主表取值（TAX_RATE/TAX_TYPE 行内缺失时取主表，对齐服务端 GetDecimal）。 */
export function previewDetailAmount(
  fields: { key: string }[],
  row: Record<string, string>,
  masterValues: Record<string, string>,
): Partial<Record<string, string>> | null {
  const keys = new Set(fields.map((field) => field.key.toUpperCase()))
  if (!keys.has('AMOUNT') && !keys.has('AMOUNT_TAX') && !keys.has('TAX_SUM')) return null
  const qty = toNumber(row.QTY)
  const price = toNumber(row.PRICE)
  // 冲抵类行（无 QTY 且无 PRICE，如预收/预付按订单冲抵金额）不参与重算，保留录入值
  if (qty === null && price === null) return null
  const taxRate = toNumber(row.TAX_RATE) ?? toNumber(masterValues.TAX_RATE) ?? 0
  const taxType = (row.TAX_TYPE?.trim() || masterValues.TAX_TYPE?.trim() || '')
  const rebate = toNumber(row.REBATE) ?? 100
  const result = calculateAmount(qty, price, taxRate, taxType, rebate)
  const next: Partial<Record<string, string>> = {}
  if (keys.has('AMOUNT')) next.AMOUNT = String(result.amount)
  if (keys.has('TAX_SUM')) next.TAX_SUM = String(result.taxSum)
  if (keys.has('AMOUNT_TAX')) next.AMOUNT_TAX = String(result.amountTax)
  return next
}

/** 主表金额汇总预览（对齐服务端 RecalculateMasterAmountsAsync 的 SUM 语义，币别一致场景）。 */
export function previewMasterAmounts(
  masterFields: { key: string }[],
  detailRows: Record<string, string>[],
): Record<string, string> {
  const keys = new Set(masterFields.map((field) => field.key.toUpperCase()))
  const sum: Record<string, number> = {}
  for (const column of ['AMOUNT', 'TAX_SUM', 'AMOUNT_TAX']) {
    if (!keys.has(column)) continue
    sum[column] = detailRows.reduce((total, row) => total + (toNumber(row[column]) ?? 0), 0)
  }
  const next: Record<string, string> = {}
  for (const column of Object.keys(sum)) next[column] = String(round2(sum[column]))
  return next
}
