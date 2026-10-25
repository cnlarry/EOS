import { describe, expect, it } from 'vitest'
import { buildLifecycleGroups, type LifecycleFieldLike } from './lifecycleGroups'

function field(key: string, label = key): LifecycleFieldLike {
  return { key, label, dataType: key.endsWith('_DATE') ? 'datetime' : 'nvarchar' }
}

const allLifecycleFields: LifecycleFieldLike[] = [
  field('CREATE_PERSON', '建立人'), field('CREATE_DATE', '建立日期'),
  field('LAST_UPDATE_BY', '修改人'), field('LAST_UPDATE_DATE', '修改日期'),
  field('CONFIRM_TAG', '批核状态'), field('CONFIRM_PERSON', '批核人'), field('CONFIRM_DATE', '批核日期'),
  field('FINISHED_TAG', '结案状态'), field('FINISHED_PERSON', '结案人'), field('FINISHED_DATE', '结案日期'),
]

describe('buildLifecycleGroups', () => {
  it('按建立/修改/批核/结案分组，状态位不进入固定区', () => {
    const groups = buildLifecycleGroups(allLifecycleFields)
    expect(groups.map(group => group.key)).toEqual(['create', 'modify', 'approve', 'close'])
    expect(groups.map(group => group.label)).toEqual(['建立', '修改', '批核', '结案'])
    expect(groups[0].person?.key).toBe('CREATE_PERSON')
    expect(groups[0].date?.key).toBe('CREATE_DATE')
    expect(groups[1].person?.key).toBe('LAST_UPDATE_BY')
    expect(groups[2].person?.key).toBe('CONFIRM_PERSON')
    expect(groups[3].date?.key).toBe('FINISHED_DATE')
  })

  it('无结案能力时只有三组', () => {
    const groups = buildLifecycleGroups(allLifecycleFields, { includeClose: false })
    expect(groups.map(group => group.key)).toEqual(['create', 'modify', 'approve'])
  })

  it('无批核能力时跳过批核组但保留结案组', () => {
    const groups = buildLifecycleGroups(allLifecycleFields, { includeApprove: false })
    expect(groups.map(group => group.key)).toEqual(['create', 'modify', 'close'])
  })

  it('只有状态位时不会产生空组', () => {
    expect(buildLifecycleGroups([
      field('CONFIRM_TAG', '批核状态'),
      field('FINISHED_TAG', '结案状态'),
    ])).toEqual([])
  })

  it('某组只有日期时 person 为 null，不丢组', () => {
    const groups = buildLifecycleGroups([field('CREATE_DATE', '建立日期')])
    expect(groups).toHaveLength(1)
    expect(groups[0].person).toBeNull()
    expect(groups[0].date?.key).toBe('CREATE_DATE')
  })

  it('输入乱序仍按固定语义顺序输出', () => {
    const groups = buildLifecycleGroups([
      field('FINISHED_PERSON'), field('CONFIRM_DATE'), field('CREATE_PERSON'), field('LAST_UPDATE_BY'),
    ])
    expect(groups.map(group => group.key)).toEqual(['create', 'modify', 'approve', 'close'])
  })
})
