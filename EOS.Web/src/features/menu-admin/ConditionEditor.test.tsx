import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { apiClientMock } from '../../test/apiMock'
import { renderWithProviders } from '../../test/renderWithProviders'
import { ConditionEditor } from './ConditionEditor'
import { makeNameLookup } from './businessActionText'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const names = makeNameLookup(
  { tables: { COP_SEND_M: '送货单' }, fields: { 'COP_SEND_M.SEND_TAG': '已送出' } },
  'COP_SEND_M',
  'COP_SEND_D',
)

const systemSettings = {
  ownerModule: 110111,
  scope: 'system',
  groups: [
    {
      groupCode: 'UI',
      groupLabel: '界面与控制',
      parameters: [
        { key: 'SEND_ORDER_TAG', description: '送货冲减订单', groupLabel: '界面与控制' },
        { key: 'PRO_MRP', description: '制令参与 MRP', groupLabel: '界面与控制' },
      ],
    },
  ],
}

function renderEditor(value: string | null, onChange = vi.fn()) {
  renderWithProviders(
    <ConditionEditor
      value={value}
      names={names}
      targetTable="COP_ORDER_D"
      masterTable="COP_SEND_M"
      detailTable="COP_SEND_D"
      onChange={onChange}
    />,
  )
  return onChange
}

describe('ConditionEditor', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation(async (url: string) => {
      if (url.endsWith('/settings/system')) return systemSettings
      return {}
    })
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('系统开关判据渲染成候选下拉（键来自已登记系统参数）并给出人话读法', async () => {
    renderEditor(JSON.stringify({ logic: 'AND', items: [{ type: 'switch', key: 'SEND_ORDER_TAG', value: true }] }))

    const input = await screen.findByDisplayValue('SEND_ORDER_TAG')
    expect(input).toHaveAttribute('list')
    // 候选来自 /settings/system，带说明文案。
    await waitFor(() => {
      expect(document.querySelectorAll('datalist option').length).toBe(2)
      expect(screen.getByText('送货冲减订单 · 界面与控制')).toBeInTheDocument()
    })
    expect(screen.getByText(/条件读作：系统开关 SEND_ORDER_TAG = true/)).toBeInTheDocument()
    expect(input.className).not.toContain('is-invalid')
  })

  it('未登记的系统参数键标红提示（执行期会被拒绝）', async () => {
    renderEditor(JSON.stringify({ logic: 'AND', items: [{ type: 'switch', key: 'NOT_A_PARAM', value: true }] }))

    const input = await screen.findByDisplayValue('NOT_A_PARAM')
    await waitFor(() => expect(input.className).toContain('is-invalid'))
  })

  it('字段与值比较按「范围 + 字段 + 值」三段渲染并回写', async () => {
    const onChange = renderEditor(
      JSON.stringify({ logic: 'AND', items: [{ type: 'value-eq', field: { scope: 'MASTER', field: 'SEND_TAG' }, value: 0 }] }),
    )

    expect(await screen.findByText(/条件读作：本单主表\.已送出\(SEND_TAG\) = 0/)).toBeInTheDocument()
    fireEvent.change(screen.getByDisplayValue('0'), { target: { value: '1' } })
    await waitFor(() => expect(onChange).toHaveBeenCalled())
    expect(JSON.parse(onChange.mock.calls.at(-1)![0]).items[0].value).toBe(1)
  })

  it('TARGET 域按目标表解析出中文字段名', async () => {
    renderWithProviders(
      <ConditionEditor
        value={JSON.stringify({ logic: 'AND', items: [{ type: 'field-compare', left: { scope: 'TARGET', field: 'SEND_TAG' }, op: 'GE', right: { value: 1 } }] })}
        names={makeNameLookup(
          { fields: { 'COP_ORDER_D.SEND_TAG': '已送出数量' } },
          'COP_SEND_M',
          'COP_SEND_D',
        )}
        targetTable="COP_ORDER_D"
        masterTable="COP_SEND_M"
        detailTable="COP_SEND_D"
        onChange={vi.fn()}
      />,
    )

    expect(await screen.findByText(/条件读作：目标行\.已送出数量\(SEND_TAG\) >= 1/)).toBeInTheDocument()
  })

  it('添加判据追加一条系统开关行；多条时逻辑可切 AND/OR', async () => {
    const onChange = renderEditor(null)

    fireEvent.click(screen.getByRole('button', { name: '添加判据' }))
    await waitFor(() => expect(onChange).toHaveBeenCalled())
    expect(JSON.parse(onChange.mock.calls.at(-1)![0])).toEqual({
      logic: 'AND',
      items: [{ type: 'switch', key: '', value: true }],
    })

    const onLogicChange = renderEditor(JSON.stringify({
      logic: 'AND',
      items: [
        { type: 'switch', key: 'SEND_ORDER_TAG', value: true },
        { type: 'switch', key: 'PRO_MRP', value: true },
      ],
    }))
    fireEvent.change(screen.getAllByDisplayValue('全部满足（AND）')[0], { target: { value: 'OR' } })
    await waitFor(() => expect(onLogicChange).toHaveBeenCalled())
    const payload = JSON.parse(onLogicChange.mock.calls.at(-1)![0])
    expect(payload.logic).toBe('OR')
    expect(payload.items).toHaveLength(2)
  })

  it('not-exists 关联子查询结构化：子查询表 + 关联键 + 内层条件，可回写', async () => {
    const onChange = renderEditor(JSON.stringify({
      logic: 'AND',
      items: [{
        type: 'not-exists',
        targetTable: 'PUR_PURCHASE_D',
        negate: true,
        match: [{ target: 'PURCHASE_NO', source: { scope: 'DETAIL', field: 'PURCHASE_NO' } }],
        condition: { type: 'value-eq', field: { scope: 'TARGET', field: 'FINISHED_TAG' }, value: 0 },
      }],
    }))

    // 关联键两侧分别是"本单目标行列"与"子查询表列"，且带子查询语义选择。
    expect(await screen.findByText('关联子查询：子查询表')).toBeInTheDocument()
    expect(screen.getByDisplayValue('PUR_PURCHASE_D')).toBeInTheDocument()
    expect(screen.getByDisplayValue('存在（EXISTS）')).toBeInTheDocument()
    // 关联键两侧：本单目标行列 / 子查询表列（两列同名，按渲染顺序取第 2 个为子查询侧）。
    const keyInputs = screen.getAllByDisplayValue('PURCHASE_NO')
    expect(keyInputs).toHaveLength(2)
    fireEvent.change(keyInputs[1], { target: { value: 'SERIAL_NO' } })
    await waitFor(() => expect(onChange).toHaveBeenCalled())
    const written = JSON.parse(onChange.mock.calls.at(-1)![0])
    // 子查询关联键的 source.scope 编译期不参与判定，编辑时原样保留，避免顺手改写既有配置。
    expect(written.items[0].match[0]).toEqual({ target: 'PURCHASE_NO', source: { scope: 'DETAIL', field: 'SERIAL_NO' } })
    expect(written.items[0].targetTable).toBe('PUR_PURCHASE_D')
    expect(written.items[0].negate).toBe(true)
  })

  it('not-exists 内层的无 type 比较结构也可结构化编辑（两侧锁在子查询表）', async () => {
    const onChange = renderEditor(JSON.stringify({
      logic: 'AND',
      items: [{
        type: 'not-exists',
        targetTable: 'PUR_PURCHASE_D',
        match: [{ target: 'PURCHASE_NO', source: { field: 'PURCHASE_NO' } }],
        condition: { left: { scope: 'TARGET', field: 'FINISHED_TAG' }, op: 'EQ', right: { value: 0 } },
      }],
    }))

    expect(await screen.findByText(/且子查询行满足/)).toBeInTheDocument()
    // 内层比较式：左字段 + 比较符 + 右侧常量勾选。
    expect(screen.getByDisplayValue('FINISHED_TAG')).toBeInTheDocument()
    expect(screen.getByDisplayValue('=')).toBeInTheDocument()
    expect(screen.getByRole('checkbox', { name: '右侧比常量' })).toBeChecked()

    fireEvent.change(screen.getByDisplayValue('FINISHED_TAG'), { target: { value: 'END_TAG' } })
    await waitFor(() => expect(onChange).toHaveBeenCalled())
    const written = JSON.parse(onChange.mock.calls.at(-1)![0])
    expect(written.items[0].condition.left).toEqual({ scope: 'TARGET', field: 'END_TAG' })
    expect(written.items[0].condition.right).toEqual({ value: 0 })
  })

  it('非法 JSON 自动落到专家模式且不丢原文', () => {
    renderEditor('{"logic":"AND","items":[')

    expect(screen.getByText(/已切换到专家模式/)).toBeInTheDocument()
    expect(screen.getByDisplayValue('{"logic":"AND","items":[')).toBeInTheDocument()
  })
})
