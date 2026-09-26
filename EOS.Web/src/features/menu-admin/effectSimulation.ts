import { apiClient } from '../../services/api'

/**
 * 效果链预演（2301 配置面）：对一张真实单据在事务内跑一遍真实的生效链并回滚，
 * 把"按当前配置会发生什么"报告出来。报告形状与后端 `EffectSimulationReportDto` 一一对应。
 */

/** 可预演的事件闭集：保存后效果发生在主子表落库之后，预演它等于先伪造一次完整保存。 */
export const SIMULATION_EVENTS = ['APPROVE_EFFECT', 'DEAPPROVE'] as const
export type SimulationEvent = (typeof SIMULATION_EVENTS)[number]

export const SIMULATION_EVENT_LABELS: Record<string, string> = {
  APPROVE_EFFECT: '批核生效',
  DEAPPROVE: '解批',
}

/**
 * 为什么"保存后"效果暂不支持预演（界面必须一直说清楚，不能只留一句"暂不支持"）：
 * 保存后效果跑在**主子表已经落库之后**，预演它就得先伪造一次完整保存——单据载荷、
 * 明细行、必填与明细必填校验、自动单号、以及自动批核模块的后续批核链。伪造出来的
 * "单据不合法"会混进"配置有问题"，报告就成了误导。
 * 现状替代：这些行可以在工作台真实保存一次后用**批核预演**观察后续，或直接看审计。
 */
export const SAVE_SIMULATION_NOTE =
  '保存后效果暂不支持预演：它跑在主子表落库之后，预演等于先伪造一次完整保存（单号与必填校验都会掺进来）。改完 SAVE 期配置请在真实单据上保存一次后观察，或用批核预演看后续链。'

export interface EffectSimulationGate {
  passed: boolean
  code?: string | null
  message?: string | null
}

export interface EffectSimulationColumnChange {
  name: string
  before: string | null
  after: string | null
}

export interface EffectSimulationRowChange {
  identity: string
  columns: EffectSimulationColumnChange[]
}

export interface EffectSimulationOp {
  opSeq: number
  targetTable: string
  targetField: string
  opCode: string
  rowsAffected: number
  changes: EffectSimulationRowChange[]
}

export interface EffectSimulationStep {
  seq: number
  effectKey: string
  effectName?: string | null
  enabled: boolean
  failMode: string
  outcome: 'ran' | 'skipped' | 'failed'
  conditionMatched: boolean
  rowsAffected: number
  ops: EffectSimulationOp[]
  condition?: string | null
  skipReason?: string | null
  message?: string | null
}

export interface EffectSimulationCounts {
  total: number
  ran: number
  skipped: number
  failed: number
}

export interface EffectSimulationReport {
  moduleId: number
  event: string
  definitionVersion?: string | null
  recordKey: string[]
  durationMs: number
  rolledBack: boolean
  precondition: EffectSimulationGate
  validation: EffectSimulationGate
  effects: EffectSimulationStep[]
  counts: EffectSimulationCounts
  warnings: string[]
  /** 这次预演跑的配置来自哪一处：published（已发布快照）/ draft（未保存草稿）。 */
  configSource?: 'published' | 'draft' | string | null
}

/**
 * 预演请求：带 `draft` 时按**未保存草稿**跑（其余部分仍取已发布基线）——
 * 配置者据此回答"我这次改完会发生什么"；不带则按已发布配置跑。
 */
export interface SimulationDraft {
  actions: unknown[]
  validationRules: unknown[]
}

export function simulateEffectChain(
  moduleId: number,
  event: SimulationEvent,
  key: string[],
  draft?: SimulationDraft | null,
): Promise<EffectSimulationReport> {
  const body: Record<string, unknown> = { event, key }
  if (draft) body.draft = draft
  return apiClient.post<EffectSimulationReport>(
    `/admin/module-business-config/${moduleId}/simulate`,
    body,
  )
}
