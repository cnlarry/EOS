import { describe, expect, it } from 'vitest'
import { rowsToTsv } from './tableClipboard'

describe('rowsToTsv', () => {
  it('生成带表头的 TSV', () => {
    expect(rowsToTsv(['编号', '名称'], [['A1', '张三'], ['A2', '李四']])).toBe(
      '编号\t名称\r\nA1\t张三\r\nA2\t李四',
    )
  })

  it('包含制表符/引号时加引号转义', () => {
    expect(rowsToTsv(['备注'], [['a\tb']])).toBe('备注\r\n"a\tb"')
    expect(rowsToTsv(['备注'], [['说"好"']])).toBe('备注\r\n"说""好"""')
  })
})
