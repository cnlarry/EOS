import { describe, expect, it } from 'vitest'
import { DETAIL_CHOOSER_SOURCE_KEY, readDetailChooserSources, withDetailChooserSource } from './formEditorUtils'

/**
 * 明细行「选过的来源」随行携带：行对象是明细编辑的唯一载体，来源跟着行走，
 * 行被排序/删除/新增都不会串到别的行；保存时再汇总成 detailChooserSources 单独下发。
 */
describe('明细行来源记忆', () => {
  it('记录来源后可按字段读回', () => {
    const row = withDetailChooserSource({ PRO_NO: 'P1' }, 'PRO_NO', 2)

    expect(readDetailChooserSources(row)).toEqual({ PRO_NO: 2 })
    expect(row[DETAIL_CHOOSER_SOURCE_KEY]).toBeTruthy()
    // 业务字段原样保留
    expect(row.PRO_NO).toBe('P1')
  })

  it('同一行重复选择同一字段时后者覆盖、不同字段各自保留', () => {
    let row = withDetailChooserSource({}, 'PRO_NO', 1)
    row = withDetailChooserSource(row, 'PRO_NO', 3)
    row = withDetailChooserSource(row, 'MOULD_ID', 2)

    expect(readDetailChooserSources(row)).toEqual({ PRO_NO: 3, MOULD_ID: 2 })
  })

  it('来源序号缺失时不写入也不改动行', () => {
    const row = { PRO_NO: 'P1' }

    expect(withDetailChooserSource(row, 'PRO_NO', null)).toBe(row)
    expect(withDetailChooserSource(row, 'PRO_NO', undefined)).toBe(row)
  })

  it('无记录或内容非法时读回 null（不抛错）', () => {
    expect(readDetailChooserSources({})).toBeNull()
    expect(readDetailChooserSources({ [DETAIL_CHOOSER_SOURCE_KEY]: '' })).toBeNull()
    expect(readDetailChooserSources({ [DETAIL_CHOOSER_SOURCE_KEY]: '{oops' })).toBeNull()
    expect(readDetailChooserSources({ [DETAIL_CHOOSER_SOURCE_KEY]: '[1,2]' })).toBeNull()
    expect(readDetailChooserSources({ [DETAIL_CHOOSER_SOURCE_KEY]: '{"PRO_NO":"x"}' })).toBeNull()
  })
})
