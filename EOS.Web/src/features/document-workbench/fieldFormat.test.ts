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
    expect(formatFieldValue(1234.5, 'float', null)).toBe('1234.5')
  })

  it('复刻旧系统自定义数字格式（0.## 等库内实际格式）', () => {
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
