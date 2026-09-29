import { describe, expect, it } from 'vitest'
import { extractPageContext } from './pageContext'

describe('extractPageContext', () => {
  it('解析工作台列表与单据页', () => {
    expect(extractPageContext('/workbench/1606')).toEqual({ moduleId: 1606, pageType: 'list' })
    expect(extractPageContext('/workbench/1606/new')).toEqual({ moduleId: 1606, pageType: 'new' })
    expect(extractPageContext('/workbench/1606/copy')).toEqual({ moduleId: 1606, pageType: 'copy' })
    expect(extractPageContext('/workbench/1606/edit/DD2608001')).toEqual({
      moduleId: 1606,
      pageType: 'edit',
      docNo: 'DD2608001',
    })
    expect(extractPageContext('/workbench/1606/view/DD2608001')).toEqual({
      moduleId: 1606,
      pageType: 'view',
      docNo: 'DD2608001',
    })
  })

  it('复合主键按期序拼接展示', () => {
    expect(extractPageContext('/workbench/1409/view/PO2026/001')?.docNo).toBe('PO2026/001')
  })

  it('解析配置页处境与配置目标', () => {
    expect(extractPageContext('/admin/tables')).toEqual({
      pageType: 'config-fields',
      configTarget: { surface: 'fields' },
    })
    expect(extractPageContext('/admin/tables/PO_M/fields')).toEqual({
      pageType: 'config-fields',
      configTarget: { surface: 'fields', tableId: 'PO_M' },
    })
    expect(extractPageContext('/admin/fields/PO_M/CUST_ID')).toEqual({
      pageType: 'config-fields',
      configTarget: { surface: 'fields', tableId: 'PO_M', fieldId: 'CUST_ID' },
    })
    expect(extractPageContext('/admin/menus')).toEqual({
      pageType: 'config-buttons',
      configTarget: { surface: 'buttons' },
    })
  })

  it('非业务页面不产生处境', () => {
    expect(extractPageContext('/dashboard')).toBeNull()
    expect(extractPageContext('/report-center')).toBeNull()
  })
})
