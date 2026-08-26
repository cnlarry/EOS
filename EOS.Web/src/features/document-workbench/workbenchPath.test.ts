import { describe, expect, it } from 'vitest'
import { parseWorkbenchKey, workbenchAction, workbenchCopy, workbenchEdit, workbenchList, workbenchModuleId, workbenchNew, workbenchView } from './workbenchPath'

describe('workbenchPath（/workbench 前缀 + 主键路径段）', () => {
  it('列表/新增', () => {
    expect(workbenchList(1405)).toBe('/workbench/1405')
    expect(workbenchNew(1405)).toBe('/workbench/1405/new')
  })

  it('单键/复合键浏览按主键序路径段', () => {
    expect(workbenchView(1401, ['A4074CL001'])).toBe('/workbench/1401/view/A4074CL001')
    expect(workbenchView(1405, ['CHTZ', 'DD26080167'])).toBe('/workbench/1405/view/CHTZ/DD26080167')
  })

  it('跨模块关联浏览带 from（query）', () => {
    expect(workbenchView(1401, ['A4074CL001'], 1405)).toBe('/workbench/1401/view/A4074CL001?from=1405')
  })

  it('编辑按主键序路径段', () => {
    expect(workbenchEdit(1405, ['CHTZ', 'DD26080167'])).toBe('/workbench/1405/edit/CHTZ/DD26080167')
  })

  it('定长字符主键（nchar 尾填充空格）路径段先修剪，避免 URL 一长串 %20', () => {
    expect(workbenchView(110103, ['RMB       '])).toBe('/workbench/110103/view/RMB')
    expect(workbenchEdit(1405, ['CHTZ', 'DD26080167 '])).toBe('/workbench/1405/edit/CHTZ/DD26080167')
  })

  it('复制携带修剪后的 copyFrom（encoded JSON）', () => {
    expect(workbenchCopy(1405, ['DD26080167 '])).toBe('/workbench/1405/copy?copyFrom=%5B%22DD26080167%22%5D')
  })

  it('splat 解析主键值数组', () => {
    expect(parseWorkbenchKey('CHTZ/DD26080167')).toEqual(['CHTZ', 'DD26080167'])
    expect(parseWorkbenchKey('A4074CL001')).toEqual(['A4074CL001'])
    expect(parseWorkbenchKey('')).toBeNull()
    expect(parseWorkbenchKey(undefined)).toBeNull()
  })

  it('动作与模块提取', () => {
    expect(workbenchAction('/workbench/1405')).toBeNull()
    expect(workbenchAction('/workbench/1405/new')).toBe('new')
    expect(workbenchAction('/workbench/1405/view/CHTZ/DD26080167')).toBe('view')
    expect(workbenchModuleId('/workbench/1401/view/A4074CL001')).toBe('1401')
    expect(workbenchModuleId('/admin/users')).toBeNull()
  })
})