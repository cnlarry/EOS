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
    columns: 4,
    buttons: null,
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

  it('白名单模式：只返回 FORM_BUTTONS 配置的动作，且顺序跟随配置', () => {
    const f = form({ buttons: [{ action: 'endcase' }, { action: 'print' }, { action: 'approve' }] })
    const items = buildViewToolbarItems(f, state(), handlers())
    // 函数忠实于 FORM_BUTTONS 顺序（组件再按固定顺序 filter 插入）
    expect(items.map(item => item.action)).toEqual(['endcase', 'print', 'approve'])
  })

  it('白名单模式：权限不足的动作被过滤', () => {
    const f = form({ buttons: [{ action: 'approve' }, { action: 'print' }], canApprove: false })
    const items = buildViewToolbarItems(f, state(), handlers())
    expect(items.map(item => item.action)).toEqual(['print'])
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
})
