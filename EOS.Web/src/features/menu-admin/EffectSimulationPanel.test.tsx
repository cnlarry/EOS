import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { apiClientMock } from '../../test/apiMock'
import { renderWithProviders } from '../../test/renderWithProviders'
import type { BusinessAction } from './BusinessActionsPanel'
import { EffectSimulationPanel } from './EffectSimulationPanel'
import { makeNameLookup } from './businessActionText'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const actions: BusinessAction[] = [
  {
    seq: 1,
    eventCode: 'APPROVE_EFFECT',
    effectKey: 'field-accumulate',
    effectName: '收料量回写采购单',
    enabled: true,
    failMode: 'BLOCK',
    reverse: JSON.stringify({ kind: 'auto-reverse' }),
    ops: [
      {
        opSeq: 1,
        targetTable: 'PUR_PURCHASE_D',
        targetField: 'RECEIVE_QTY',
        opCode: 'ACCUM',
        sourceScope: 'DETAIL',
        sourceField: 'QTY',
        sourceAgg: 'SUM',
      },
    ],
  },
]

const names = makeNameLookup(
  {
    tables: { PUR_PURCHASE_D: '采购单明细', PUR_PURCHASE_M: '采购单' },
    fields: { 'PUR_PURCHASE_D.RECEIVE_QTY': '已收数量' },
  },
  'PUR_RECEIVE_M',
  'PUR_RECEIVE_D',
)

const chooserResult = {
  columns: [
    { key: 'PURCHASE_TYPE', label: '单别', dataType: 'nvarchar' },
    { key: 'PURCHASE_NO', label: '单号', dataType: 'nvarchar' },
  ],
  rows: [{ PURCHASE_TYPE: 'PO', PURCHASE_NO: 'PO2026001' }],
  total: 1,
  defaultKeys: ['PURCHASE_TYPE', 'PURCHASE_NO'],
}

function report(overrides: Record<string, unknown> = {}) {
  return {
    moduleId: 1607,
    event: 'APPROVE_EFFECT',
    definitionVersion: 'module-1607-v7',
    recordKey: ['PO', 'PO2026001'],
    durationMs: 42,
    rolledBack: true,
    precondition: { passed: true, code: null, message: null },
    validation: { passed: true, message: null },
    effects: [
      {
        seq: 1,
        effectKey: 'field-accumulate',
        effectName: '收料量回写采购单',
        enabled: true,
        failMode: 'BLOCK',
        outcome: 'ran',
        conditionMatched: true,
        rowsAffected: 2,
        condition: null,
        skipReason: null,
        message: null,
        ops: [
          {
            opSeq: 1,
            targetTable: 'PUR_PURCHASE_D',
            targetField: 'RECEIVE_QTY',
            opCode: 'ACCUM',
            rowsAffected: 2,
            changes: [
              { identity: 'PURCHASE_NO=PO2026001', columns: [{ name: 'RECEIVE_QTY', before: '120', after: '150' }] },
            ],
          },
        ],
      },
      {
        seq: 2,
        effectKey: 'completion-close',
        effectName: '完成判定/自动结案',
        enabled: true,
        failMode: 'BLOCK',
        outcome: 'skipped',
        conditionMatched: false,
        rowsAffected: 0,
        condition: JSON.stringify({ type: 'field-compare', left: { scope: 'DETAIL', field: 'FINISHED_TAG' }, op: 'NEQ', right: { constant: '1' } }),
        skipReason: '条件未命中（该动作的条件对本单不成立）。',
        message: null,
        ops: [],
      },
    ],
    counts: { total: 2, ran: 1, skipped: 1, failed: 0 },
    warnings: ['服务型效果只报告影响行数，不含字段级差异'],
    ...overrides,
  }
}

function renderPanel() {
  return renderWithProviders(
    <EffectSimulationPanel
      moduleId={1607}
      moduleTitle="收料单"
      actions={actions}
      names={names}
      reverseKindLabels={{ 'auto-reverse': '按公式行自动反向' }}
      onClose={() => undefined}
    />,
  )
}

async function pickRecord() {
  fireEvent.click(screen.getByRole('button', { name: '选择单据' }))
  const row = await screen.findByText('PO2026001')
  fireEvent.click(row)
  fireEvent.click(screen.getByRole('button', { name: '确认' }))
}

describe('EffectSimulationPanel', () => {
  beforeEach(() => {
    apiClientMock.post.mockReset()
    apiClientMock.post.mockImplementation(async (url: string) => {
      if (url === '/chooser/query') return chooserResult
      if (url.endsWith('/simulate')) return report()
      return {}
    })
  })

  it('未选单据时不能预演，且固定声明"不改数据"', () => {
    renderPanel()
    expect(screen.getByText(/预演不改数据/)).toBeInTheDocument()
    expect(screen.getByText(/事务内回滚，库内数据零变化/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /预演（不改数据）/ })).toBeDisabled()
  })

  it('选中单据后按主键列构造请求，并把步骤、旧值→新值与人话渲染出来', async () => {
    renderPanel()
    await pickRecord()
    fireEvent.click(screen.getByRole('button', { name: /预演（不改数据）/ }))

    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/admin/module-business-config/1607/simulate',
      { event: 'APPROVE_EFFECT', key: ['PO', 'PO2026001'] },
    ))

    expect(await screen.findByText('已回滚')).toBeInTheDocument()
    // 步骤徽标：执行 / 跳过
    expect(screen.getByText('执行')).toBeInTheDocument()
    expect(screen.getByText('跳过')).toBeInTheDocument()
    // 跳过原因 + 条件人话（复用 businessActionText 的条件渲染）
    expect(screen.getByText(/条件未命中/)).toBeInTheDocument()
    // 旧值 → 新值
    expect(screen.getByText('PURCHASE_NO=PO2026001')).toBeInTheDocument()
    expect(screen.getByText('120')).toBeInTheDocument()
    expect(screen.getByText('150')).toBeInTheDocument()
    // 报告里的告警如实透出
    expect(screen.getByText(/服务型效果只报告影响行数/)).toBeInTheDocument()
  })

  it('校验闸拦截时只给理由、不渲染任何步骤', async () => {
    apiClientMock.post.mockImplementation(async (url: string) => {
      if (url === '/chooser/query') return chooserResult
      return report({
        validation: { passed: false, message: '收料数量超过采购量。' },
        effects: [],
        counts: { total: 0, ran: 0, skipped: 0, failed: 0 },
      })
    })
    renderPanel()
    await pickRecord()
    fireEvent.click(screen.getByRole('button', { name: /预演（不改数据）/ }))

    const alert = await screen.findByText(/校验闸拦截/)
    expect(alert).toBeInTheDocument()
    expect(within(alert.parentElement as HTMLElement).getByText(/收料数量超过采购量。/)).toBeInTheDocument()
    expect(screen.queryByText('执行')).not.toBeInTheDocument()
  })

  it('前置守卫未通过时把错误码与真实路径同口径说明透出', async () => {
    apiClientMock.post.mockImplementation(async (url: string) => {
      if (url === '/chooser/query') return chooserResult
      return report({
        precondition: { passed: false, code: 'WORKFLOW_STATE_CONFLICT', message: '记录不存在或未批核，无法解批。' },
        effects: [],
        counts: { total: 0, ran: 0, skipped: 0, failed: 0 },
      })
    })
    renderPanel()
    await pickRecord()
    fireEvent.change(screen.getByLabelText('事件'), { target: { value: 'DEAPPROVE' } })
    fireEvent.click(screen.getByRole('button', { name: /预演（不改数据）/ }))

    const alert = await screen.findByText(/前置守卫未通过/)
    expect(alert).toBeInTheDocument()
    expect(within(alert).getByText(/WORKFLOW_STATE_CONFLICT/)).toBeInTheDocument()
  })
})
