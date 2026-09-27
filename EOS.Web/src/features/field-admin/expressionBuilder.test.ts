import { describe, expect, it } from 'vitest'
import {
  buildExpression,
  emptyModel,
  modelFromStructure,
  modelIssue,
  resolvePhysicalTable,
  type TableRelation,
} from './expressionBuilder'

describe('buildExpression', () => {
  it('虚拟表达式按引用拼「表.列」，缺列不生成', () => {
    expect(buildExpression('virtual_exp', { ...emptyModel('virtual_exp', 'T1'), table: 'CLIENT', column: 'CLIENT_NAME' }))
      .toBe('CLIENT.CLIENT_NAME')
    expect(buildExpression('virtual_exp', { ...emptyModel('virtual_exp', 'T1'), table: 'T1', column: '' })).toBeNull()
  })

  it('转换函数取注册表函数名，未选不生成', () => {
    expect(buildExpression('convert_function', { ...emptyModel('convert_function', 'T1'), function: 'f_get_emp_name_by_id' }))
      .toBe('f_get_emp_name_by_id')
    expect(buildExpression('convert_function', emptyModel('convert_function', 'T1'))).toBeNull()
  })
})

describe('modelFromStructure', () => {
  it('构建器可编辑形态回读为模型', () => {
    const reference = modelFromStructure('virtual_exp', { kind: 'virtual_exp', mode: 'reference', table: 'CLIENT_J', column: 'CLIENT_NAME' }, 'ORDER_M')
    expect(reference).toMatchObject({ mode: 'reference', table: 'CLIENT_J', column: 'CLIENT_NAME' })

    const registry = modelFromStructure('convert_function', { kind: 'convert_function', mode: 'registry', function: 'f_get_emp_name_by_id' }, 'ORDER_M')
    expect(registry).toMatchObject({ mode: 'registry', function: 'f_get_emp_name_by_id' })
  })

  it('构建器不覆盖的形态返回 null（算术/未通过受控语法）', () => {
    expect(modelFromStructure('virtual_exp', { kind: 'virtual_exp', mode: 'arithmetic' }, 'ORDER_M')).toBeNull()
    expect(modelFromStructure('virtual_exp', { kind: 'virtual_exp', mode: 'raw' }, 'ORDER_M')).toBeNull()
    expect(modelFromStructure('convert_function', { kind: 'convert_function', mode: 'raw' }, 'ORDER_M')).toBeNull()
  })
})

describe('modelIssue', () => {
  it('完整模型无提示，缺项给出可操作提示', () => {
    expect(modelIssue('virtual_exp', { ...emptyModel('virtual_exp', 'T1'), table: 'T1', column: 'CODE' })).toBeNull()
    expect(modelIssue('virtual_exp', emptyModel('virtual_exp', 'T1'))).toBe('请选择引用列。')
    expect(modelIssue('convert_function', emptyModel('convert_function', 'T1'))).toBe('请选择注册表内的转换函数。')
  })
})

describe('resolvePhysicalTable', () => {
  const relations: TableRelation[] = [{ table: 'CLIENT', alias: 'CLIENT_J', conditions: [] }]

  it('本表与别名分别解析到物理表', () => {
    expect(resolvePhysicalTable('ORDER_M', relations, 'ORDER_M')).toBe('ORDER_M')
    expect(resolvePhysicalTable('ORDER_M', relations, 'CLIENT_J')).toBe('CLIENT')
    expect(resolvePhysicalTable('ORDER_M', relations, 'CLIENT')).toBe('CLIENT')
  })

  it('白名单外的来源解析不到物理表', () => {
    expect(resolvePhysicalTable('ORDER_M', relations, 'OTHER_J')).toBe('')
  })
})
