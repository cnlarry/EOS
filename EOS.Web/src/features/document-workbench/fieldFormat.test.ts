import { describe, expect, it } from 'vitest'
import { alignClass, formatFieldValue } from './fieldFormat'

describe('formatFieldValue', () => {
  it('空值返回空字符串', () => {
    expect(formatFieldValue(null, 'nvarchar', null)).toBe('')
    expect(formatFieldValue(undefined, 'datetime', null)).toBe('')
  })

  it('bit 显示是/否', () => {
    expect(formatFieldValue(true, 'bit', null)).toBe('是')
    expect(formatFieldValue(0, 'bit', null)).toBe('否')
  })

  it('日期按 DISPLAY_FORMAT 格式化并补零', () => {
    expect(formatFieldValue('2018-5-17', 'datetime', 'yyyy-MM-dd')).toBe('2018-05-17')
    expect(formatFieldValue('2018-05-17T09:05:03', 'datetime', 'yyyy-MM-dd HH:mm:ss')).toBe('2018-05-17 09:05:03')
  })

  it('无格式时沿用原有本地化显示', () => {
    const value = formatFieldValue('2018-5-17', 'datetime', null)
    expect(value).toMatch(/2018/)
  })

  it('非法日期不崩溃并回退原始字符串', () => {
    expect(formatFieldValue('0000-00-00', 'datetime', 'yyyy-MM-dd')).toBe('0000-00-00')
  })

  it('数字支持千分位与小数位格式', () => {
    expect(formatFieldValue(1234.5, 'float', '#,##0.00')).toBe('1,234.50')
    // 未配置格式时补类型默认（ADR-037 批 2）：小数最多 6 位、带千分位，不再裸输出
    expect(formatFieldValue(1234.5, 'float', null, { locale: 'en-US' })).toBe('1,234.5')
  })

  it('整数类型默认不分组：序号/项次/编号是标识符，不是计量值', () => {
    expect(formatFieldValue(1234, 'int', null, { locale: 'en-US' })).toBe('1234')
    expect(formatFieldValue(1024, 'smallint', null, { locale: 'en-US' })).toBe('1024')
    // 显式配置千分位仍然生效（三级回落第一级优先）
    expect(formatFieldValue(1234, 'int', '#,##0', { locale: 'en-US' })).toBe('1,234')
  })

  it('金额语义默认固定两位小数，尾零必须显示', () => {
    const amount = { locale: 'en-US', numericRole: 'AMOUNT' }
    expect(formatFieldValue(800, 'decimal', null, amount)).toBe('800.00')
    expect(formatFieldValue(800.1, 'decimal', null, amount)).toBe('800.10')
    expect(formatFieldValue(1234.567, 'decimal', null, amount)).toBe('1,234.57')
    // 非金额的小数仍按最多 6 位去尾零
    expect(formatFieldValue(800, 'decimal', null, { locale: 'en-US', numericRole: 'QTY' })).toBe('800')
    // 显式格式优先于语义默认
    expect(formatFieldValue(800, 'decimal', '0', amount)).toBe('800')
  })

  it('locale 决定分组符与小数点，不再硬编码 zh-CN', () => {
    expect(formatFieldValue(1234.5, 'float', null, { locale: 'de-DE' })).toBe('1.234,5')
    expect(formatFieldValue(1234.5, 'float', 'N2', { locale: 'de-DE' })).toBe('1.234,50')
    expect(formatFieldValue(1234.5, 'float', '#,##0.00', { locale: 'de-DE' })).toBe('1.234,50')
  })

  it('货币符号取单据币别，不再硬编码 CNY', () => {
    expect(formatFieldValue(1234.5, 'decimal', 'C', { locale: 'en-US', currency: 'USD' })).toBe('$1,234.50')
    expect(formatFieldValue(1234.5, 'decimal', 'C', { locale: 'en-US', currency: 'JPY' })).toBe('¥1,235')
  })

  it('系统自定义数字格式（0.## 等库内实际格式）', () => {
    expect(formatFieldValue(12, 'float', '0.##')).toBe('12')
    expect(formatFieldValue(12.5, 'float', '0.##')).toBe('12.5')
    expect(formatFieldValue(1234.5678, 'float', '0.##')).toBe('1234.57')
    expect(formatFieldValue(1.2, 'float', '0.0000')).toBe('1.2000')
    expect(formatFieldValue(12.6, 'float', '0')).toBe('13')
    expect(formatFieldValue(0, 'float', '#')).toBe('')
    expect(formatFieldValue(1234, 'float', '#')).toBe('1234')
    expect(formatFieldValue(0.5, 'float', '#.##')).toBe('.5')
    expect(formatFieldValue(1234567.8, 'float', '#,##0.00')).toBe('1,234,567.80')
    expect(formatFieldValue(-12.5, 'float', '0.##')).toBe('-12.5')
  })

  it('支持 .NET 标准数字格式（N/F）', () => {
    expect(formatFieldValue(1234.5, 'float', 'N2')).toBe('1,234.50')
    expect(formatFieldValue(1.25, 'float', 'F1')).toBe('1.3')
  })

  it('负数会计括号：分号第二段决定负数写法', () => {
    // 与 .NET 的 ToString("#,##0.00;(#,##0.00)") 一致，打印侧同源
    expect(formatFieldValue(-1234.5, 'decimal', '#,##0.00;(#,##0.00)')).toBe('(1,234.50)')
    expect(formatFieldValue(1234.5, 'decimal', '#,##0.00;(#,##0.00)')).toBe('1,234.50')
    // 未写负号段时仍用负号
    expect(formatFieldValue(-1234.5, 'decimal', '#,##0.00')).toBe('-1,234.50')
  })

  it('文本类型字段也可按格式特征格式化（库内 nvarchar 的 0.##/yyyy-MM-dd）', () => {
    expect(formatFieldValue('1234.5678', 'nvarchar', '0.##')).toBe('1234.57')
    expect(formatFieldValue('2026-08-11 00:00:00', 'nvarchar', 'yyyy-MM-dd')).toBe('2026-08-11')
  })

  it('对齐方式映射为 Tabler 类名', () => {
    expect(alignClass('left')).toBe('text-start')
    expect(alignClass('center')).toBe('text-center')
    expect(alignClass('right')).toBe('text-end')
    expect(alignClass('')).toBe('text-start')
    expect(alignClass(null)).toBe('text-start')
    expect(alignClass('', 'float')).toBe('text-end')
    expect(alignClass(null, 'decimal')).toBe('text-end')
    expect(alignClass('', 'nvarchar')).toBe('text-start')
    expect(alignClass('left', 'float')).toBe('text-start')
  })
})
