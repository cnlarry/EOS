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

  it('对齐方式映射为 Tabler 类名', () => {
    expect(alignClass('left')).toBe('text-start')
    expect(alignClass('center')).toBe('text-center')
    expect(alignClass('right')).toBe('text-end')
    expect(alignClass('')).toBe('text-start')
    expect(alignClass(null)).toBe('text-start')
  })
})
