/**
 * 单据生命周期字段的分组（运行态底部固定区与设计器预览共用）。
 *
 * 生命周期字段是**记录元信息**，不是版式里的业务字段：它们只出现在浏览态的固定区，
 * 按语义分成 建立 / 修改 / 批核 / 结案 四组。批核/结案组是否出现由调用方按**能力**传入
 * （无结案流程的模块只有三组）。
 *
 * 这里只按字段代号前缀分组，不硬编码 10 个字段的完整清单——唯一真源仍是服务端
 * `WorkflowStates.LifecycleColumns`，前端只消费下发下来的 key/label。
 */

export type LifecycleGroupKey = 'create' | 'modify' | 'approve' | 'close'

export interface LifecycleFieldLike {
  key: string
  label: string
  dataType: string
  displayFormat?: string | null
}

export interface LifecycleGroup<T extends LifecycleFieldLike> {
  key: LifecycleGroupKey
  label: string
  /** 经办人列（CREATE_PERSON / LAST_UPDATE_BY / CONFIRM_PERSON / FINISHED_PERSON） */
  person: T | null
  /** 经办日期列（*_DATE） */
  date: T | null
}

const ORDER: LifecycleGroupKey[] = ['create', 'modify', 'approve', 'close']

const LABELS: Record<LifecycleGroupKey, string> = {
  create: '建立',
  modify: '修改',
  approve: '批核',
  close: '结案',
}

function groupOf(key: string): LifecycleGroupKey | null {
  const upper = key.toUpperCase()
  if (upper.startsWith('CREATE_')) return 'create'
  if (upper.startsWith('LAST_UPDATE_')) return 'modify'
  if (upper.startsWith('CONFIRM_')) return 'approve'
  if (upper.startsWith('FINISHED_')) return 'close'
  return null
}

function roleOf(key: string): 'person' | 'date' | null {
  const upper = key.toUpperCase()
  if (upper.endsWith('_DATE')) return 'date'
  if (upper.endsWith('_PERSON') || upper.endsWith('_BY')) return 'person'
  return null
}

/**
 * 把生命周期字段按语义分组。
 *
 * - 状态位（`CONFIRM_TAG` / `FINISHED_TAG`）不进入固定区：它们由工具栏状态徽标承载；
 * - `includeApprove` / `includeClose` 默认 true；无结案能力的模块传 `includeClose: false`，
 *   结果是三组，调用方的等分栅格按 groups.length 排；
 * - 某组只有状态位、没有经办人/日期时不会产生空组。
 */
export function buildLifecycleGroups<T extends LifecycleFieldLike>(
  fields: readonly T[] | null | undefined,
  options: { includeApprove?: boolean; includeClose?: boolean } = {},
): LifecycleGroup<T>[] {
  const includeApprove = options.includeApprove ?? true
  const includeClose = options.includeClose ?? true
  const byGroup = new Map<LifecycleGroupKey, LifecycleGroup<T>>()

  for (const field of fields ?? []) {
    const groupKey = groupOf(field.key)
    if (!groupKey) continue
    if (groupKey === 'approve' && !includeApprove) continue
    if (groupKey === 'close' && !includeClose) continue
    const role = roleOf(field.key)
    if (!role) continue

    const group = byGroup.get(groupKey) ?? {
      key: groupKey,
      label: LABELS[groupKey],
      person: null,
      date: null,
    }
    if (role === 'person' && group.person === null) group.person = field
    if (role === 'date' && group.date === null) group.date = field
    byGroup.set(groupKey, group)
  }

  return ORDER.flatMap(key => {
    const group = byGroup.get(key)
    return group ? [group] : []
  })
}
