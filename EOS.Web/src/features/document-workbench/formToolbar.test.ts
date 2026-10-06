import { describe, expect, it, vi } from 'vitest'
import type { FormDefinition } from './formDefinition'
import { buildViewToolbarItems, type ViewToolbarHandlers, type ViewToolbarState } from './formToolbar'

function form(overrides: Partial<FormDefinition> = {}): FormDefinition {
  return {
    moduleId: 1209,
    title: '测试',
    masterTable: 'T',
    detailTable: null,
    hasAdd: true,
    hasEdit: true,
    mode: 'edit',
    ifCopy: true,
    searchMaster: false,
    searchDetail: false,
    masterFields: [],
    detailFields: [],
    masterPkOrder: ['ID'],
    detailNoFields: '',
    detailDfVerify: '',
    tabs: [],
    hasWorkflow: true,
    hasStatelessApprove: false,
    defaultValues: {},
    canDelete: true,
    canApprove: true,
    canDeapprove: true,
    canEndCase: true,
    canUnEndCase: true,
    canAddNew: true,
    canEdit: true,
    canFileView: false,
    canFileUpda: false,
    canFileEdit: false,
    canFileDele: false,
    canSetup: false,
  canFormDesign: false,
    ...overrides,
  }
}

function handlers(): ViewToolbarHandlers {
  return {
    openApprove: vi.fn(),
    deapprove: vi.fn(),
    endcase: vi.fn(),
    unendcase: vi.fn(),
    openPrint: vi.fn(),
    workflowPending: false,
    finishPending: false,
  }
}

function state(overrides: Partial<ViewToolbarState> = {}): ViewToolbarState {
  return {
    master: { CONFIRM_TAG: false, FINISHED_TAG: false },
    isConfirmed: false,
    isFinished: false,
    flowInProgress: false,
    keyParam: '1404',
    ...overrides,
  }
}

describe('buildViewToolbarItems', () => {
  it('回退集：未批核 + 可审批 → approve；已批核 → deapprove；未结案 → endcase；恒有 print', () => {
    const items = buildViewToolbarItems(form(), state(), handlers())
    expect(items.map(item => item.action)).toEqual(['approve', 'endcase', 'print'])
  })

  it('回退集：已批核且已结案 → deapprove + unendcase（无 approve/endcase）', () => {
    const items = buildViewToolbarItems(form(), state({ master: { CONFIRM_TAG: true, FINISHED_TAG: true }, isConfirmed: true, isFinished: true }), handlers())
    expect(items.map(item => item.action)).toEqual(['deapprove', 'unendcase', 'print'])
  })

  it('在途流程时批核隐藏（approve 不出现）', () => {
    const items = buildViewToolbarItems(form(), state({ flowInProgress: true }), handlers())
    expect(items.some(item => item.action === 'approve')).toBe(false)
  })

  it('已结案时 approve/deapprove 禁用（disabled）', () => {
    const approved = buildViewToolbarItems(form(), state({ isFinished: true, master: { CONFIRM_TAG: true, FINISHED_TAG: true } }), handlers())
    const deapprove = approved.find(item => item.action === 'deapprove')
    expect(deapprove?.disabled).toBe(true)
  })

  it('动作集只由能力与权限决定（FORM_BUTTONS 白名单已退役）', () => {
    // 权限全无：批核/解批与结案都不出现，只剩打印（单据级动作不再有任何"配置开关"可开）
    const noPermission = form({ canApprove: false, canDeapprove: false, canEndCase: false, canUnEndCase: false })
    expect(buildViewToolbarItems(noPermission, state(), handlers()).map(item => item.action))
      .toEqual(['print'])
  })

  it('无批核能力时不显示批核/解批（无死按钮）', () => {
    const f = form({ hasWorkflow: false, hasStatelessApprove: false })
    const items = buildViewToolbarItems(f, state(), handlers())
    expect(items.some(item => item.action === 'approve' || item.action === 'deapprove')).toBe(false)
    const confirmed = buildViewToolbarItems(f, state({ master: { CONFIRM_TAG: true, FINISHED_TAG: false } }), handlers())
    expect(confirmed.some(item => item.action === 'approve' || item.action === 'deapprove')).toBe(false)
  })

  it('无副作用自动批核显示批核/解批（与服务端同口径）', () => {
    const f = form({ hasWorkflow: false, hasStatelessApprove: true })
    expect(buildViewToolbarItems(f, state(), handlers()).map(item => item.action)).toContain('approve')
    const confirmed = buildViewToolbarItems(f, state({ master: { CONFIRM_TAG: true, FINISHED_TAG: false } }), handlers())
    expect(confirmed.map(item => item.action)).toContain('deapprove')
  })

  it('效果链接管批核（无过程、无流程）同样显示批核/解批', () => {
    // 服务端 hasApproveCapability 四者取并集：效果链已接管的模块既无 WorkflowSproc 也无流程，
    // 但批核/解批入口必须照常显示（退役遗留过程后按钮不得消失）。
    const f = form({ hasWorkflow: false, hasStatelessApprove: false, hasApproveCapability: true })
    expect(buildViewToolbarItems(f, state(), handlers()).map(item => item.action)).toContain('approve')
    const confirmed = buildViewToolbarItems(f, state({ master: { CONFIRM_TAG: true, FINISHED_TAG: false } }), handlers())
    expect(confirmed.map(item => item.action)).toContain('deapprove')
  })

  it('服务端明确无批核能力时不显示（hasApproveCapability=false 覆盖旧标志）', () => {
    const f = form({ hasWorkflow: true, hasStatelessApprove: true, hasApproveCapability: false })
    const items = buildViewToolbarItems(f, state(), handlers())
    expect(items.some(item => item.action === 'approve' || item.action === 'deapprove')).toBe(false)
  })
})
