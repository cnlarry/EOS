import { describe, expect, it } from 'vitest'

import {
  INERT_EVENT_NOTE,
  UNUSED_EVENT_NOTE,
  eventAvailability,
  recipeCoveredKeys,
  recipeToActionShape,
  reversePresetJson,
  type EffectRecipe,
} from './effectRecipes'

const moveStock: EffectRecipe = {
  key: 'move-stock',
  name: '写库存（出入库移动）',
  summary: '按明细行生成库存移动',
  eventCodes: ['APPROVE_EFFECT'],
  effectKeys: ['inventory-move', 'half-stock-move'],
  formulaMode: false,
  requiresRelation: true,
  reversePreset: 'reverse-flow',
  paramsHint: 'direction 必填',
  note: null,
}

const writeUpstream: EffectRecipe = {
  key: 'write-upstream-qty',
  name: '回写上游单数量',
  summary: '按定位键累加回写',
  eventCodes: ['APPROVE_EFFECT'],
  effectKeys: ['field-accumulate'],
  formulaMode: true,
  requiresRelation: true,
  reversePreset: 'auto-reverse',
  paramsHint: '参数区留空',
  note: null,
}

describe('配方 → 动作字段（等价性）', () => {
  it('配方路径与键级路径逐字段一致', () => {
    // 键级路径：手工写死的那一份（值照配方目录抄，不引用配方对象）。
    const byHand = {
      seq: 3,
      eventCode: 'APPROVE_EFFECT',
      effectKey: 'inventory-move',
      reverse: '{"kind":"reverse-flow"}',
    }

    expect(recipeToActionShape(moveStock, 3)).toEqual(byHand)
  })

  it('反向预设的落库形态与手工配置写的 JSON 逐字相同', () => {
    expect(reversePresetJson(writeUpstream)).toBe('{"kind":"auto-reverse"}')
    expect(reversePresetJson(moveStock)).toBe('{"kind":"reverse-flow"}')
  })

  it('配方不造参数、不造公式行——它只预填事件/效果键/反向', () => {
    const shape = recipeToActionShape(writeUpstream, 1)
    expect(Object.keys(shape).sort()).toEqual(['effectKey', 'eventCode', 'reverse', 'seq'])
  })

  it('效果键取配方第一个键，事件取第一个事件', () => {
    expect(recipeToActionShape(moveStock, 7).effectKey).toBe('inventory-move')
    expect(recipeToActionShape(moveStock, 7).eventCode).toBe('APPROVE_EFFECT')
  })

  it('覆盖键集合按小写归一，供界面判断某键有无配方', () => {
    const covered = recipeCoveredKeys([moveStock, writeUpstream])
    expect(covered.has('inventory-move')).toBe(true)
    expect(covered.has('half-stock-move')).toBe(true)
    expect(covered.has('not-a-key')).toBe(false)
    expect(recipeCoveredKeys(null).size).toBe(0)
  })
})

describe('事件可用性（入门路径口径）', () => {
  it('库内已有行的惰性事件：保持可选，但必须标注', () => {
    // ENDCASE 的实测形态：接不到效果链，但库里有一行真实配置（1502 结案释放）。
    const availability = eventAvailability('ENDCASE', ['ENDCASE', 'UNENDCASE'], { ENDCASE: 1, SAVE: 47 })
    expect(availability.selectable).toBe(true)
    expect(availability.note).toBe(INERT_EVENT_NOTE)
  })

  it('库内 0 行的惰性事件：不可选并说明原因', () => {
    const availability = eventAvailability('UNENDCASE', ['ENDCASE', 'UNENDCASE'], { ENDCASE: 1, SAVE: 47 })
    expect(availability.selectable).toBe(false)
    expect(availability.note).toBe(UNUSED_EVENT_NOTE)
  })

  it('会跑的事件不受影响', () => {
    const availability = eventAvailability('SAVE', ['ENDCASE', 'UNENDCASE'], { SAVE: 47 })
    expect(availability).toEqual({ selectable: true, note: null })
  })

  it('服务端没下发使用次数时只标注、不禁用（没有事实就不下结论）', () => {
    const availability = eventAvailability('ENDCASE', ['ENDCASE'], null)
    expect(availability.selectable).toBe(true)
    expect(availability.note).toBe(INERT_EVENT_NOTE)
  })

  it('旧版服务端（连 inertEvents 都没有）不改变任何事件的可选性', () => {
    expect(eventAvailability('ENDCASE', null, null)).toEqual({ selectable: true, note: null })
  })
})
