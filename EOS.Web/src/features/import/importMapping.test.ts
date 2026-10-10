import { describe, expect, it } from 'vitest'
import { applySavedMapping, matchColumns, missingRequiredKeys, normalizeHeader, toMappingEntries } from './importMapping'
import type { ImportDefinitionInfo, ImportMappingSnapshot } from './types'

const definition: ImportDefinitionInfo = {
  moduleId: 1401,
  title: '客户基本资料',
  masterTable: 'CLIENT',
  requiredKeys: ['CLIENT_ID'],
  fields: [
    { key: 'CLIENT_ID', label: '客户代号', dataType: 'nchar', format: null, isRequired: true, isPrimaryKey: true, isAutoIncrement: false, maxLength: 20 },
    { key: 'CLIENT_NAME', label: '客户名称', dataType: 'nvarchar', format: null, isRequired: false, isPrimaryKey: false, isAutoIncrement: false, maxLength: 100 },
  ],
}

describe('importMapping', () => {
  it('按字段标签匹配，忽略大小写与空白差异', () => {
    expect(matchColumns([' 客户代号 ', '客户名称'], definition)).toEqual({ 0: 'CLIENT_ID', 1: 'CLIENT_NAME' })
  })

  it('标签对不上时按字段代号匹配', () => {
    expect(matchColumns(['CLIENT_id', 'client_name'], definition)).toEqual({ 0: 'CLIENT_ID', 1: 'CLIENT_NAME' })
  })

  it('对不上的列留空由人工选，不猜', () => {
    expect(matchColumns(['旧系统编号', '备注'], definition)).toEqual({})
  })

  it('重名列各自独立映射', () => {
    expect(matchColumns(['客户代号', '客户代号'], definition)).toEqual({ 0: 'CLIENT_ID', 1: 'CLIENT_ID' })
  })

  it('必须映射的键没映射上就报出来', () => {
    expect(missingRequiredKeys(definition, {})).toEqual(['CLIENT_ID'])
    expect(missingRequiredKeys(definition, { 0: 'CLIENT_ID' })).toEqual([])
  })

  it('归一化只去空白与大小写，不改字符本身', () => {
    expect(normalizeHeader(' A B ')).toBe('ab')
    expect(normalizeHeader('料号')).toBe('料号')
  })
})

describe('applySavedMapping', () => {
  const snapshot = (entries: ImportMappingSnapshot['entries']): ImportMappingSnapshot => ({
    moduleId: 1401,
    sourceName: '客户资料.xlsx',
    entries,
    updatedAt: '',
    updatedBy: '',
  })

  it('没记住过就等同于自动匹配', () => {
    expect(applySavedMapping(['客户代号'], null, definition)).toEqual(matchColumns(['客户代号'], definition))
    expect(applySavedMapping(['客户代号'], snapshot([]), definition)).toEqual(matchColumns(['客户代号'], definition))
  })

  it('按列名套用记住的映射（列序变了也不串位）', () => {
    const saved = snapshot([
      { column: '旧编码', field: 'CLIENT_ID' },
      { column: '旧名称', field: 'CLIENT_NAME' },
    ])
    // 这两个列名自动匹配不出来（对不上标签/字段代号），只能靠记住的映射
    expect(applySavedMapping(['旧名称', '旧编码'], saved, definition)).toEqual({ 0: 'CLIENT_NAME', 1: 'CLIENT_ID' })
  })

  it('记住的"不导入"会被尊重，不被自动匹配填回去', () => {
    const saved = snapshot([{ column: '客户代号', field: null }])
    expect(applySavedMapping(['客户代号'], saved, definition)).toEqual({})
  })

  it('记住的字段已不在当前定义里就忽略这一条', () => {
    const saved = snapshot([{ column: '旧编码', field: 'CLIENT_RETIRED' }])
    expect(applySavedMapping(['旧编码'], saved, definition)).toEqual({})
  })

  it('列名在新文件里没有同名列时保持自动匹配的结果', () => {
    const saved = snapshot([{ column: '别的列', field: 'CLIENT_NAME' }])
    expect(applySavedMapping(['客户代号'], saved, definition)).toEqual({ 0: 'CLIENT_ID' })
  })

  it('导出条目按列名记录，与列序无关', () => {
    expect(toMappingEntries(['客户代号', '备注'], { 0: 'CLIENT_ID' })).toEqual([
      { column: '客户代号', field: 'CLIENT_ID' },
      { column: '备注', field: null },
    ])
  })
})
