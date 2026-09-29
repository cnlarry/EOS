import { beforeEach, describe, expect, it } from 'vitest'
import {
  SITUATION_LIMITS,
  buildChatSituation,
  clearServerNotice,
  currentSituation,
  reportDirtyFields,
  reportListFilters,
  reportSelection,
  reportServerNotice,
  resetSituationSource,
  setConfigTarget,
} from './situationSource'

describe('situationSource', () => {
  beforeEach(() => resetSituationSource())

  it('没有上报时只带路由处境', () => {
    expect(buildChatSituation('/workbench/1606/edit/DD2608001')).toEqual({
      moduleId: 1606,
      pageType: 'edit',
      docNo: 'DD2608001',
    })
  })

  it('列表筛选、选中行、脏字段与拒绝随界面动作进入下一次请求', () => {
    reportListFilters([{ field: 'STATUS', operator: 'eq', value: '未审核' }])
    reportSelection(['DD2608001', 'DD2608002'])
    reportDirtyFields([{ field: 'REMARK', old: '', new: '加急' }])
    reportServerNotice('VALIDATION_FAILED', '供应商未填')

    const situation = buildChatSituation('/workbench/1606')
    expect(situation.filters).toEqual([{ field: 'STATUS', operator: 'eq', value: '未审核' }])
    expect(situation.selection).toEqual(['DD2608001', 'DD2608002'])
    expect(situation.formDirty).toEqual([{ field: 'REMARK', old: '', new: '加急' }])
    expect(situation.lastNotice).toEqual({ code: 'VALIDATION_FAILED', summary: '供应商未填' })
  })

  it('界面动作变化后请求体随之变化（处境连续，不需用户复述）', () => {
    reportSelection(['A'])
    expect(buildChatSituation('/workbench/1606').selection).toEqual(['A'])

    reportSelection(['B'])
    expect(buildChatSituation('/workbench/1606').selection).toEqual(['B'])

    reportDirtyFields([])
    expect(buildChatSituation('/workbench/1606').formDirty).toBeUndefined()
  })

  it('保存成功后清掉最近拒绝，不让旧拒绝一直被当成当前状态', () => {
    reportServerNotice('VALIDATION_FAILED', '供应商未填')
    expect(buildChatSituation('/workbench/1606').lastNotice).toBeDefined()

    clearServerNotice()
    expect(buildChatSituation('/workbench/1606').lastNotice).toBeUndefined()
  })

  it('条数超过前端上限时截断（服务端仍会再截断）', () => {
    reportListFilters(Array.from({ length: SITUATION_LIMITS.filters + 5 }, (_, index) => ({
      field: `F${index}`,
      operator: 'eq',
      value: String(index),
    })))
    expect(currentSituation().filters).toHaveLength(SITUATION_LIMITS.filters)

    reportSelection(Array.from({ length: SITUATION_LIMITS.selection + 5 }, (_, index) => `K${index}`))
    expect(currentSituation().selection).toHaveLength(SITUATION_LIMITS.selection)

    reportDirtyFields(Array.from({ length: SITUATION_LIMITS.formDirty + 5 }, (_, index) => ({
      field: `F${index}`,
      old: '',
      new: '1',
    })))
    expect(currentSituation().formDirty).toHaveLength(SITUATION_LIMITS.formDirty)
  })

  it('配置页处境：路由推导字段面，页面上报的按钮/效果面优先', () => {
    expect(buildChatSituation('/admin/fields/PO_M/CUST_ID')).toEqual({
      pageType: 'config-fields',
      configTarget: { surface: 'fields', tableId: 'PO_M', fieldId: 'CUST_ID' },
    })

    setConfigTarget({ surface: 'effect', effectKey: 'field-accumulate' })
    expect(buildChatSituation('/admin/menus').configTarget).toEqual({
      surface: 'effect',
      effectKey: 'field-accumulate',
    })

    setConfigTarget(null)
    expect(buildChatSituation('/admin/menus').configTarget).toEqual({ surface: 'buttons' })
  })

  it('resetSituationSource 清空全部采集', () => {
    reportSelection(['A'])
    reportServerNotice('NOT_FOUND', '不存在')
    resetSituationSource()
    expect(currentSituation().selection).toEqual([])
    expect(currentSituation().lastNotice).toBeUndefined()
  })
})
