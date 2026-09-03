import { describe, expect, it } from 'vitest'
import { fromDateTimeControlValue, toDateTimeControlValue } from './dateTimeValue'

describe('dateTimeValue', () => {
  it('datetime 库内空格分隔串 → datetime-local 控件值（保留时间分量）', () => {
    expect(toDateTimeControlValue('datetime', '2026-08-23 14:30:00')).toBe('2026-08-23T14:30:00')
  })

  it('smalldatetime 无秒串 → 控件值补齐秒', () => {
    expect(toDateTimeControlValue('smalldatetime', '2026-08-23 14:30')).toBe('2026-08-23T14:30:00')
  })

  it('date 类型只取日期部分', () => {
    expect(toDateTimeControlValue('date', '2026-08-23 14:30:00')).toBe('2026-08-23')
    expect(toDateTimeControlValue('date', '2026-08-23')).toBe('2026-08-23')
  })

  it('ISO T 分隔与毫秒串可解析回显', () => {
    expect(toDateTimeControlValue('datetime', '2026-08-23T14:30:05.123')).toBe('2026-08-23T14:30:05')
  })

  it('空值返回空；不可解析串按契约回退（date 原样、datetime 空）', () => {
    expect(toDateTimeControlValue('datetime', '')).toBe('')
    expect(toDateTimeControlValue('date', 'not-a-date')).toBe('not-a-date')
    expect(toDateTimeControlValue('datetime', '垃圾')).toBe('')
  })

  it('控件值 → 提交载荷：秒缺省补 00', () => {
    expect(fromDateTimeControlValue('2026-08-23T14:30')).toBe('2026-08-23T14:30:00')
    expect(fromDateTimeControlValue('2026-08-23T14:30:07')).toBe('2026-08-23T14:30:07')
  })

  it('date 控件值提交原样', () => {
    expect(fromDateTimeControlValue('2026-08-23')).toBe('2026-08-23')
  })

  it('空串提交为空串', () => {
    expect(fromDateTimeControlValue('')).toBe('')
  })
})
