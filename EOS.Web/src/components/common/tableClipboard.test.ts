import { describe, expect, it } from 'vitest'
import { gridToTsv, parseClipboardGrid, rowsToTsv } from './tableClipboard'

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

describe('gridToTsv', () => {
  it('不带表头的一整块', () => {
    expect(gridToTsv([['1', 'A'], ['2', 'B']])).toBe('1\tA\r\n2\tB')
  })

  it('空单元格保留位次', () => {
    expect(gridToTsv([['1', '', 'C']])).toBe('1\t\tC')
  })
})

describe('parseClipboardGrid', () => {
  it('按制表符与换行切分（Excel 复制出来的形态）', () => {
    expect(parseClipboardGrid('A1\tB1\r\nA2\tB2')).toEqual([['A1', 'B1'], ['A2', 'B2']])
  })

  it('引号包裹的制表符/换行/引号不被拆坏', () => {
    expect(parseClipboardGrid('"a\tb"\t"x\ny"\r\n"说""好"""\t1')).toEqual([
      ['a\tb', 'x\ny'],
      ['说"好"', '1'],
    ])
  })

  it('单列多行与末尾换行', () => {
    expect(parseClipboardGrid('1\n2\n')).toEqual([['1'], ['2']])
  })

  it('单值（没有分隔符）也是一格', () => {
    expect(parseClipboardGrid('100')).toEqual([['100']])
  })

  it('空文本返回空数组（不产生一行空值）', () => {
    expect(parseClipboardGrid('')).toEqual([])
  })
})
