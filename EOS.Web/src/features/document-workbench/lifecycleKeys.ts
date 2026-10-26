/**
 * 单据生命周期列的字段代号（前端唯一集中处）。
 *
 * 与服务端 `EOS.API/Data/WorkflowStates.cs` 的 `LifecycleColumns` 同源——那边是唯一真源，
 * 这里只是把这些代号集中放一处，避免各页面散写字面量后又各自漂移。
 * 四组语义：建立 / 修改 / 批核 / 结案，每组「经办人 + 时间」，批核与结案另有状态位。
 */
export const LIFECYCLE_KEYS = {
  createdBy: 'CREATED_BY',
  createdTime: 'CREATED_TIME',
  modifiedBy: 'MODIFIED_BY',
  modifiedTime: 'MODIFIED_TIME',
  approved: 'APPROVED',
  approvedBy: 'APPROVED_BY',
  approvedTime: 'APPROVED_TIME',
  finished: 'FINISHED',
  finishedBy: 'FINISHED_BY',
  finishedTime: 'FINISHED_TIME',
} as const

/** 四组的字段代号前缀（分组按前缀识别，不硬编码完整清单）。 */
export const LIFECYCLE_PREFIXES = {
  created: 'CREATED_',
  modified: 'MODIFIED_',
  approved: 'APPROVED_',
  finished: 'FINISHED_',
} as const
