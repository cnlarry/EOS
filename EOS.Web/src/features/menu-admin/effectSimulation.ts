import { apiClient } from '../../services/api'

/**
 * 效果链预演（2301 配置面）：对一张真实单据在事务内跑一遍真实的生效链并回滚，
 * 把"按当前配置会发生什么"报告出来。报告形状与 ADR-021 §3.2 冻结的契约一致。
 */

/** 本批次支持的事件闭集（保存后效果要预演就得先伪造一次完整保存，列入后续批次）。 */
export const SIMULATION_EVENTS = ['APPROVE_EFFECT', 'DEAPPROVE'] as const
export type SimulationEvent = (typeof SIMULATION_EVENTS)[number]

export const SIMULATION_EVENT_LABELS: Record<string, string> = {
  APPROVE_EFFECT: '批核生效',
  DEAPPROVE: '解批',
}

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
}

export function simulateEffectChain(
  moduleId: number,
  event: SimulationEvent,
  key: string[],
): Promise<EffectSimulationReport> {
  return apiClient.post<EffectSimulationReport>(
    `/admin/module-business-config/${moduleId}/simulate`,
    { event, key },
  )
}
