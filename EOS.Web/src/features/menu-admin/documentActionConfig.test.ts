import { describe, expect, it } from 'vitest'
import { parseDocumentActionParams, serializeDocumentActionParams } from './documentActionConfig'

const declaration = (fields: unknown) => JSON.stringify({ fields })

describe('documentActionConfig 入参声明编解码', () => {
  it('解析服务端认得的声明结构', () => {
    const fields = parseDocumentActionParams(declaration([
      { key: 'relocateTo', label: '目标库位', type: 'string', required: true, maxLength: 30 },
      { key: 'force', label: '强制', type: 'bool' },
    ]))

    expect(fields).toEqual([
      { key: 'relocateTo', label: '目标库位', type: 'string', required: true, maxLength: 30 },
      { key: 'force', label: '强制', type: 'bool', required: false, maxLength: null },
    ])
  })

  it('非法或不完整的内容一律视为"没有参数"，不在界面上编出半成品', () => {
    expect(parseDocumentActionParams(null)).toEqual([])
    expect(parseDocumentActionParams('')).toEqual([])
    expect(parseDocumentActionParams('{')).toEqual([])
    expect(parseDocumentActionParams(JSON.stringify({ targetTable: 'X' }))).toEqual([])
    // 缺 key 的项直接丢弃；未登记的类型回落到文本。
    const fields = parseDocumentActionParams(declaration([
      { label: '没有键' },
      { key: 'mode', label: '模式', type: 'money' },
    ]))
    expect(fields).toEqual([{ key: 'mode', label: '模式', type: 'string', required: false, maxLength: null }])
  })

  it('序列化只输出服务端认得的键，空清单等于没有声明', () => {
    expect(serializeDocumentActionParams([])).toBeNull()

    const json = serializeDocumentActionParams([
      { key: ' relocateTo ', label: '目标库位', type: 'string', required: true, maxLength: 30 },
      { key: 'force', label: '强制', type: 'bool', required: false, maxLength: 20 },
    ])

    expect(JSON.parse(json!)).toEqual({
      fields: [
        { key: 'relocateTo', label: '目标库位', type: 'string', required: true, maxLength: 30 },
        // 非文本类型不带 maxLength（长度只对文本有意义）。
        { key: 'force', label: '强制', type: 'bool' },
      ],
    })
  })
})
