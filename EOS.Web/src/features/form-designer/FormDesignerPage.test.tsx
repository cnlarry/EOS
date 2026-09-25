import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { apiClientMock } from '../../test/apiMock'
import FormDesignerPage from './FormDesignerPage'
import type { DesignRow, DesignState } from './types'

vi.mock('../../services/api', async () => ({
  apiClient: (await import('../../test/apiMock')).apiClientMock,
}))

function row(key: string, overrides: Partial<DesignRow> = {}): DesignRow {
  return {
    key,
    label: key,
    dataType: 'nvarchar',
    tabNo: 1,
    orderNo: 1,
    span: 1,
    rowSpan: 1,
    newLine: false,
    sectionId: null,
    cellGroup: null,
    cellRole: 0,
    hidden: false,
    locked: false,
    lockReason: null,
    userVisible: true,
    required: false,
    isPrimaryKey: false,
    hasChooser: false,
    isVirtual: false,
    ...overrides,
  }
}

const designState: DesignState = {
  moduleId: 1405,
  title: '客户订单',
  masterTable: 'COP_ORDER_M',
  detailTable: 'COP_ORDER_D',
  columns: 2,
  tabs: [
    { no: 1, title: '' },
    { no: 2, title: '明细信息' },
  ],
  master: {
    table: 'COP_ORDER_M',
    layout: [
      row('ORDER_NO', { label: '单号', orderNo: 1, isPrimaryKey: true, locked: true, lockReason: '主键列，始终显示', required: true }),
      row('CLIENT_ID', { label: '客户', orderNo: 2, hasChooser: true }),
      row('REMARK', { label: '备注', orderNo: 3 }),
      row('OLD_TAB_FIELD', { label: '旧页签字段', orderNo: 4, tabNo: 2 }),
    ],
    pool: [
      {
        key: 'CLIENT_NAME',
        label: '客户名称',
        dataType: 'nvarchar',
        userVisible: true,
        required: false,
        isPrimaryKey: false,
        hasChooser: false,
        isVirtual: false,
        locked: false,
        lockReason: null,
      },
    ],
  },
  detail: {
    table: 'COP_ORDER_D',
    layout: [
      row('PRO_NO', { label: '产品编号', orderNo: 1, isPrimaryKey: true, locked: true, required: true }),
      row('QTY', { label: '数量', orderNo: 2 }),
    ],
    pool: [],
  },
  baseUpdatedAt: '2026-09-25 05:00:00.000',
}

function renderPage() {
  return render(<FormDesignerPage moduleId={1405} onExit={() => undefined} />)
}

describe('FormDesignerPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockResolvedValue(designState)
    apiClientMock.put.mockResolvedValue({
      status: 'saved',
      message: '已保存并生效（所有人可见）。',
      definitionVersion: 'module-1405-v9',
      state: designState,
    })
    apiClientMock.post.mockResolvedValue({
      status: 'saved',
      message: '已重置为默认版式并生效。',
      definitionVersion: 'module-1405-v10',
      state: designState,
    })
  })

  afterEach(() => {
    vi.clearAllMocks()
    vi.unstubAllGlobals()
  })

  it('加载后渲染画布、字段池与明细列', async () => {
    renderPage()
    expect(await screen.findByText('客户订单 · COP_ORDER_M / COP_ORDER_D · 2 列')).toBeInTheDocument()
    expect(screen.getByText('ORDER_NO')).toBeInTheDocument()
    expect(screen.getByText('客户名称')).toBeInTheDocument()
    expect(screen.getByText(/明细列（COP_ORDER_D）/)).toBeInTheDocument()
  })

  it('隐藏字段后保存，提交的是整份版式且带幂等键', async () => {
    renderPage()
    await screen.findByText('ORDER_NO')
    // REMARK 未锁定 → 允许从表单移除
    const remarkCell = screen.getByText('REMARK').closest('.erp-designer-field') as HTMLElement
    fireEvent.click(remarkCell.querySelector('button[title="从表单移除（可再恢复）"]') as HTMLElement)
    fireEvent.click(screen.getByRole('button', { name: '保存' }))

    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledTimes(1))
    const [path, payload] = apiClientMock.put.mock.calls[0] as [string, {
      baseUpdatedAt: string | null
      idempotencyKey: string
      tabs: { no: number; title: string }[]
      master: { key: string; hidden: boolean }[]
      detail: { key: string }[]
    }]
    expect(path).toBe('/admin/form-layout/1405')
    expect(payload.baseUpdatedAt).toBe('2026-09-25 05:00:00.000')
    expect(payload.idempotencyKey.length).toBeGreaterThan(0)
    expect(payload.tabs.map(tab => tab.no)).toEqual([1, 2])
    expect(payload.master.map(item => item.key)).toEqual(['ORDER_NO', 'CLIENT_ID', 'REMARK', 'OLD_TAB_FIELD'])
    expect(payload.master.find(item => item.key === 'REMARK')?.hidden).toBe(true)
    expect(payload.detail.map(item => item.key)).toEqual(['PRO_NO', 'QTY'])
  })

  it('锁定字段没有移除按钮', async () => {
    renderPage()
    await screen.findByText('ORDER_NO')
    const lockedCell = screen.getByText('ORDER_NO').closest('.erp-designer-field') as HTMLElement
    const removeButton = lockedCell.querySelector('button[title="主键列，始终显示"]') as HTMLButtonElement
    expect(removeButton).toBeTruthy()
    expect(removeButton.disabled).toBe(true)
  })

  it('新增页签后保存请求包含新页签', async () => {
    renderPage()
    await screen.findByText('ORDER_NO')
    fireEvent.click(screen.getByTitle('新增页签'))
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledTimes(1))
    const payload = apiClientMock.put.mock.calls[0][1] as { tabs: { no: number; title: string }[] }
    expect(payload.tabs.map(tab => tab.no)).toEqual([1, 2, 3])
  })

  it('撤销回到改动前（Ctrl+Z）', async () => {
    renderPage()
    await screen.findByText('ORDER_NO')
    const remarkCell = screen.getByText('REMARK').closest('.erp-designer-field') as HTMLElement
    fireEvent.click(remarkCell.querySelector('button[title="从表单移除（可再恢复）"]') as HTMLElement)
    expect(screen.getByText('已隐藏')).toBeInTheDocument()
    fireEvent.keyDown(window, { key: 'z', ctrlKey: true })
    await waitFor(() => expect(screen.queryByText('已隐藏')).toBeNull())
  })
})
