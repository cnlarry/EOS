import { describe, expect, it } from 'vitest'
import { calculateAmount, previewDetailAmount, previewMasterAmounts } from './amountCalculator'

describe('amountCalculator（与 C# AmountCalculatorTests 逐条对拍）', () => {
  it('O 外含税：金额=10×100=1000，税额=1000×13%=130，价税合计=1130', () => {
    expect(calculateAmount(10, 100, 13, 'O', 100)).toEqual({ amount: 1000, taxSum: 130, amountTax: 1130 })
  })

  it('I 内含税：价税合计=1000，金额=1000/1.13，税额=差额', () => {
    expect(calculateAmount(10, 100, 13, 'I', 100)).toEqual({ amount: 884.96, taxSum: 115.04, amountTax: 1000 })
  })

  it('N 不含税：税额 0', () => {
    expect(calculateAmount(10, 100, 13, 'N', 100)).toEqual({ amount: 1000, taxSum: 0, amountTax: 1000 })
  })

  it('折扣 90%：O 型金额=900', () => {
    expect(calculateAmount(10, 100, 0, 'O', 90)).toEqual({ amount: 900, taxSum: 0, amountTax: 900 })
  })

  it('未填税型按不含税处理', () => {
    expect(calculateAmount(5, 20, null, null, null)).toEqual({ amount: 100, taxSum: 0, amountTax: 100 })
  })

  it('空数量金额为 0', () => {
    expect(calculateAmount(null, 100, 13, 'O', 100)).toEqual({ amount: 0, taxSum: 0, amountTax: 0 })
  })
})

describe('previewDetailAmount', () => {
  const fields = ['QTY', 'PRICE', 'TAX_RATE', 'TAX_TYPE', 'REBATE', 'AMOUNT', 'TAX_SUM', 'AMOUNT_TAX'].map((key) => ({ key }))

  it('行内 TAX_RATE/TAX_TYPE 优先，缺失回退主表', () => {
    const row = { QTY: '10', PRICE: '100', TAX_RATE: '', TAX_TYPE: '', REBATE: '100' }
    const master = { TAX_RATE: '13', TAX_TYPE: 'O' }
    expect(previewDetailAmount(fields, row, master)).toEqual({ AMOUNT: '1000', TAX_SUM: '130', AMOUNT_TAX: '1130' })
  })

  it('无 QTY 且无 PRICE 的冲抵行保留录入值（不重算）', () => {
    const row = { AMOUNT: '500' }
    expect(previewDetailAmount(fields, row, {})).toBeNull()
  })
})

describe('previewMasterAmounts', () => {
  it('明细 SUM 汇总主表金额（round2）', () => {
    const masterFields = ['AMOUNT', 'TAX_SUM', 'AMOUNT_TAX'].map((key) => ({ key }))
    const rows = [
      { AMOUNT: '1000', TAX_SUM: '130', AMOUNT_TAX: '1130' },
      { AMOUNT: '900', TAX_SUM: '0', AMOUNT_TAX: '900' },
    ]
    expect(previewMasterAmounts(masterFields, rows)).toEqual({ AMOUNT: '1900', TAX_SUM: '130', AMOUNT_TAX: '2030' })
  })
})
