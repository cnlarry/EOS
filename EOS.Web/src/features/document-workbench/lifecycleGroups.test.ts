import { describe, expect, it } from 'vitest'
import { buildLifecycleGroups, type LifecycleFieldLike } from './lifecycleGroups'

function field(key: string, label = key): LifecycleFieldLike {
  return { key, label, dataType: key.endsWith('_TIME') ? 'datetime' : 'nvarchar' }
}

const allLifecycleFields: LifecycleFieldLike[] = [
  field('CREATED_BY', '建立人'), field('CREATED_TIME', '建立日期'),
  field('MODIFIED_BY', '修改人'), field('MODIFIED_TIME', '修改日期'),
  field('APPROVED', '批核状态'), field('APPROVED_BY', '批核人'), field('APPROVED_TIME', '批核日期'),
  field('FINISHED', '结案状态'), field('FINISHED_BY', '结案人'), field('FINISHED_TIME', '结案日期'),
]

describe('buildLifecycleGroups', () => {
  it('按建立/修改/批核/结案分组，状态位不进入固定区', () => {
    const groups = buildLifecycleGroups(allLifecycleFields)
    expect(groups.map(group => group.key)).toEqual(['create', 'modify', 'approve', 'close'])
    expect(groups.map(group => group.label)).toEqual(['建立', '修改', '批核', '结案'])
    expect(groups[0].person?.key).toBe('CREATED_BY')
    expect(groups[0].date?.key).toBe('CREATED_TIME')
    expect(groups[1].person?.key).toBe('MODIFIED_BY')
    expect(groups[2].person?.key).toBe('APPROVED_BY')
    expect(groups[3].date?.key).toBe('FINISHED_TIME')
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
      field('APPROVED', '批核状态'),
      field('FINISHED', '结案状态'),
    ])).toEqual([])
  })

  it('某组只有日期时 person 为 null，不丢组', () => {
    const groups = buildLifecycleGroups([field('CREATED_TIME', '建立日期')])
    expect(groups).toHaveLength(1)
    expect(groups[0].person).toBeNull()
    expect(groups[0].date?.key).toBe('CREATED_TIME')
  })

  it('输入乱序仍按固定语义顺序输出', () => {
    const groups = buildLifecycleGroups([
      field('FINISHED_BY'), field('APPROVED_TIME'), field('CREATED_BY'), field('MODIFIED_BY'),
    ])
    expect(groups.map(group => group.key)).toEqual(['create', 'modify', 'approve', 'close'])
  })
})
