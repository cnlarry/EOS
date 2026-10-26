import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import type { FormDefinition, FormFieldDefinition } from './formDefinition'
import { LifecycleInfoBlock, LifecycleStatusBar } from './LifecycleInfo'

function field(key: string, label: string, overrides: Partial<FormFieldDefinition> = {}): FormFieldDefinition {
  return {
    key,
    label,
    dataType: key.endsWith('_TIME') ? 'datetime' : 'nvarchar',
    displayLength: 60,
    displayFormat: key.endsWith('_TIME') ? 'yyyy-MM-dd HH:mm:ss' : null,
    isRequired: false,
    verifyIndex: null,
    regex: null,
    defaultValue: null,
    isReadonly: true,
    isVisible: true,
    onlyChoose: false,
    chooseMultiple: false,
    choosePage: null,
    choosers: [],
    isPrimaryKey: false,
    isAutoIncrement: false,
    isVirtual: false,
    isCost: false,
    isSecrecy: false,
    serverFilled: true,
    maxLength: null,
    tabNo: 1,
    formOrder: null,
    span: 1,
    newLine: false,
    cellGroup: null,
    cellRole: 0,
    options: [],
    displayOnly: false,
    canCopy: true,
    ...overrides,
  }
}

const lifecycleFields = [
  field('CREATED_BY', '建立人'), field('CREATED_TIME', '建立日期'),
  field('MODIFIED_BY', '修改人'), field('MODIFIED_TIME', '修改日期'),
  field('APPROVED', '批核状态'), field('APPROVED_BY', '批核人'), field('APPROVED_TIME', '批核日期'),
  field('FINISHED', '结案状态'), field('FINISHED_BY', '结案人'), field('FINISHED_TIME', '结案日期'),
]

function form(overrides: Partial<FormDefinition> = {}): FormDefinition {
  return {
    moduleId: 1606,
    title: '采购单',
    masterTable: 'PUR_PURCHASE_M',
    detailTable: null,
    hasAdd: true,
    hasEdit: true,
    mode: 'view',
    masterFields: [],
    detailFields: [],
    masterPkOrder: ['PURCHASE_TYPE', 'PURCHASE_NO'],
    detailNoFields: '',
    detailDfVerify: '',
    tabs: [],
    hasWorkflow: false,
    hasStatelessApprove: false,
    hasApproveCapability: true,
    hasFinishCapability: true,
    lifecycleFields,
    ifCopy: false,
    searchMaster: false,
    searchDetail: false,
    defaultValues: {},
    canDelete: false,
    canApprove: false,
    canDeapprove: false,
    canEndCase: false,
    canUnEndCase: false,
    canAddNew: false,
    canEdit: false,
    canFileView: false,
    canFileUpda: false,
    canFileEdit: false,
    canFileDele: false,
    canSetup: false,
    canFormDesign: false,
    ...overrides,
  }
}

const master = {
  APPROVED: true,
  FINISHED: false,
  CREATED_BY: '管理员',
  CREATED_TIME: '2026-10-14T11:16:43',
  MODIFIED_BY: '管理员',
  MODIFIED_TIME: '2026-10-14T11:16:43',
  APPROVED_BY: '王五',
  APPROVED_TIME: '2026-10-14T11:16:43',
  FINISHED_BY: null,
  FINISHED_TIME: null,
}

describe('LifecycleStatusBar', () => {
  it('有结案能力时显示批核 + 结案两枚状态', () => {
    const { container } = render(<LifecycleStatusBar form={form()} master={master} />)
    expect(container.querySelector('.erp-doc-status')).not.toBeNull()
    expect(screen.getByText('已批核')).toBeInTheDocument()
    expect(screen.getByText('未结案')).toBeInTheDocument()
  })

  it('无结案能力时只显示批核状态，不显示结案', () => {
    const noClose = form({
      hasFinishCapability: false,
      lifecycleFields: lifecycleFields.filter(field => !field.key.startsWith('FINISHED_')),
    })
    const { container } = render(
      <LifecycleStatusBar form={noClose} master={{ APPROVED: true }} />,
    )
    expect(screen.getByText('已批核')).toBeInTheDocument()
    expect(screen.queryByText('未结案')).not.toBeInTheDocument()
    expect(container.querySelectorAll('.erp-doc-status-badge')).toHaveLength(1)
  })

  it('在途流程显示审批中', () => {
    render(<LifecycleStatusBar form={form()} master={{ APPROVED: false, FINISHED: false }} flowInProgress />)
    expect(screen.getByText('审批中')).toBeInTheDocument()
  })

  it('记录未到达时不猜状态', () => {
    const { container } = render(<LifecycleStatusBar form={form()} master={undefined} />)
    expect(container.firstChild).toBeNull()
  })
})

describe('LifecycleInfoBlock', () => {
  it('四组等分，无可见“单据信息”标题，日期显示到分钟', () => {
    const { container } = render(<LifecycleInfoBlock form={form()} master={master} />)
    const block = container.querySelector<HTMLElement>('.erp-form-lifecycle')
    expect(block).not.toBeNull()
    expect(block!.style.getPropertyValue('--erp-lifecycle-groups')).toBe('4')
    expect(screen.queryByText('单据信息')).not.toBeInTheDocument()
    expect(screen.getByText('建立')).toBeInTheDocument()
    expect(screen.getByText('修改')).toBeInTheDocument()
    expect(screen.getByText('批核')).toBeInTheDocument()
    expect(screen.getByText('结案')).toBeInTheDocument()
    expect(screen.getByText('王五')).toBeInTheDocument()
    // 秒只在 title 里保留；正文显示到分钟
    expect(screen.getAllByText('2026-10-14 11:16').length).toBeGreaterThan(0)
  })

  it('无结案能力时三组等分，不渲染结案组', () => {
    const noClose = form({
      hasFinishCapability: false,
      lifecycleFields: lifecycleFields.filter(field => !field.key.startsWith('FINISHED_')),
    })
    const { container } = render(
      <LifecycleInfoBlock form={noClose} master={{ APPROVED: true, APPROVED_BY: '王五', APPROVED_TIME: '2026-10-14T11:16:43' }} />,
    )
    const block = container.querySelector<HTMLElement>('.erp-form-lifecycle')
    expect(block!.style.getPropertyValue('--erp-lifecycle-groups')).toBe('3')
    expect(screen.queryByText('结案')).not.toBeInTheDocument()
  })

  it('没有生命周期字段或记录未到达时不渲染空块', () => {
    const { container } = render(<LifecycleInfoBlock form={form({ lifecycleFields: [] })} master={master} />)
    expect(container.firstChild).toBeNull()
    const { container: pending } = render(<LifecycleInfoBlock form={form()} master={undefined} />)
    expect(pending.firstChild).toBeNull()
  })
})
