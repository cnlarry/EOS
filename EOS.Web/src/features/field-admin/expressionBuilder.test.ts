import { describe, expect, it } from 'vitest'
import {
  DATASOURCE_MAX_COLUMNS,
  buildExpression,
  emptyModel,
  isNumericType,
  modelFromStructure,
  modelIssue,
  resolvePhysicalTable,
  type ExpressionModel,
  type ExpressionStructure,
  type TableRelation,
} from './expressionBuilder'

function dataSourceModel(overrides: Partial<ExpressionModel['dataSource']> = {}): ExpressionModel {
  return {
    ...emptyModel('datasource_sql', 'ORDER_M'),
    dataSource: {
      table: 'SYSDG',
      columns: ['G_IDX', 'G_DESC'],
      whereColumn: '',
      whereValue: '',
      whereValueIsString: true,
      orderColumn: '',
      orderDirection: 'ASC',
      ...overrides,
    },
  }
}

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

  it('数据源 SQL 生成受限 SELECT：列清单 + 单条过滤 + 单列排序', () => {
    expect(buildExpression('datasource_sql', dataSourceModel()))
      .toBe('SELECT G_IDX,G_DESC FROM SYSDG')
    expect(buildExpression('datasource_sql', dataSourceModel({ whereColumn: 'G_IDX', whereValue: 'A1', orderColumn: 'G_IDX', orderDirection: 'DESC' })))
      .toBe("SELECT G_IDX,G_DESC FROM SYSDG WHERE G_IDX = 'A1' ORDER BY G_IDX DESC")
  })

  it('数据源 SQL 字符串常量转义单引号，数值列只接受数字字面量', () => {
    expect(buildExpression('datasource_sql', dataSourceModel({ whereColumn: 'G_DESC', whereValue: "O'K" })))
      .toBe("SELECT G_IDX,G_DESC FROM SYSDG WHERE G_DESC = 'O''K'")
    const numeric = dataSourceModel({ whereColumn: 'SORT_NO', whereValue: '1', whereValueIsString: false })
    expect(buildExpression('datasource_sql', numeric)).toBe('SELECT G_IDX,G_DESC FROM SYSDG WHERE SORT_NO = 1')
    // 数值列收到非数字字面量：不生成（服务端只接受带引号字符串或数值）
    expect(buildExpression('datasource_sql', { ...numeric, dataSource: { ...numeric.dataSource, whereValue: 'abc' } })).toBeNull()
  })

  it('数据源 SQL 缺表或缺列不生成', () => {
    expect(buildExpression('datasource_sql', dataSourceModel({ table: '' }))).toBeNull()
    expect(buildExpression('datasource_sql', dataSourceModel({ columns: [] }))).toBeNull()
  })

  it('数值类型判定覆盖整数/小数/金额族', () => {
    expect(isNumericType('decimal')).toBe(true)
    expect(isNumericType(' INT ')).toBe(true)
    expect(isNumericType('nvarchar')).toBe(false)
  })
})

describe('modelFromStructure', () => {
  it('构建器可编辑形态回读为模型', () => {
    const reference = modelFromStructure('virtual_exp', { kind: 'virtual_exp', mode: 'reference', table: 'CLIENT_J', column: 'CLIENT_NAME' }, 'ORDER_M')
    expect(reference).toMatchObject({ mode: 'reference', table: 'CLIENT_J', column: 'CLIENT_NAME' })

    const registry = modelFromStructure('convert_function', { kind: 'convert_function', mode: 'registry', function: 'f_get_emp_name_by_id' }, 'ORDER_M')
    expect(registry).toMatchObject({ mode: 'registry', function: 'f_get_emp_name_by_id' })

    const structure: ExpressionStructure = {
      kind: 'datasource_sql',
      mode: 'tableSql',
      dataSource: {
        table: 'SYSDG',
        columns: ['G_IDX'],
        whereColumn: 'G_IDX',
        whereValue: 'A1',
        whereValueIsString: true,
        orderColumn: 'G_IDX',
        orderDirection: 'DESC',
      },
    }
    expect(modelFromStructure('datasource_sql', structure, 'ORDER_M')?.dataSource)
      .toMatchObject({ table: 'SYSDG', columns: ['G_IDX'], whereColumn: 'G_IDX', whereValue: 'A1', orderDirection: 'DESC' })
  })

  it('构建器不覆盖的形态返回 null（算术/字面量 UNION/未通过语法）', () => {
    expect(modelFromStructure('virtual_exp', { kind: 'virtual_exp', mode: 'arithmetic' }, 'ORDER_M')).toBeNull()
    expect(modelFromStructure('datasource_sql', { kind: 'datasource_sql', mode: 'literalUnion' }, 'ORDER_M')).toBeNull()
    expect(modelFromStructure('convert_function', { kind: 'convert_function', mode: 'raw' }, 'ORDER_M')).toBeNull()
  })
})

describe('modelIssue', () => {
  it('完整模型无提示，缺项给出可操作提示', () => {
    expect(modelIssue('virtual_exp', { ...emptyModel('virtual_exp', 'T1'), table: 'T1', column: 'CODE' })).toBeNull()
    expect(modelIssue('virtual_exp', emptyModel('virtual_exp', 'T1'))).toBe('请选择引用列。')
    expect(modelIssue('convert_function', emptyModel('convert_function', 'T1'))).toBe('请选择注册表内的转换函数。')
    expect(modelIssue('datasource_sql', emptyModel('datasource_sql', 'T1'))).toBe('请选择来源表。')
    expect(modelIssue('datasource_sql', dataSourceModel({ columns: [] }))).toBe('请至少选择一列。')
    expect(modelIssue('datasource_sql', dataSourceModel({ whereColumn: 'G_DESC', whereValue: '' }))).toContain('请填写过滤值')
    expect(modelIssue('datasource_sql', dataSourceModel({ whereColumn: 'SORT_NO', whereValue: 'x', whereValueIsString: false })))
      .toContain('必须是数字字面量')
  })

  it('列数超上限提示（服务端同样拒绝）', () => {
    const columns = Array.from({ length: DATASOURCE_MAX_COLUMNS + 1 }, (_, index) => `C${index}`)
    expect(modelIssue('datasource_sql', dataSourceModel({ columns }))).toContain('最多选择')
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
