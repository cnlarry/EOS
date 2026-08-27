import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { FieldAuditPage } from './FieldAuditPage'

const apiClientMock = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  postFile: vi.fn(),
}))

vi.mock('../../services/api', () => ({ apiClient: apiClientMock }))

const unmanaged = {
  kind: 'unmanaged',
  rows: [
    { T_ID: 'PRODUCT', F_ID: 'COL_A', F_TYPE: 'nvarchar' },
    { T_ID: 'CLIENT', F_ID: 'COL_B', F_TYPE: 'int' },
  ],
  total: 2,
  limited: false,
}

const orphan = {
  kind: 'orphan',
  rows: [
    { T_ID: 'GONE', F_ID: 'OLD', F_TYPE: 'nvarchar', F_DESC: '旧字段', IS_VIRTUAL: true },
  ],
  total: 1,
  limited: false,
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <FieldAuditPage />
    </QueryClientProvider>,
  )
}

describe('FieldAuditPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation(async (path: string) =>
      path.includes('/orphan') ? orphan : unmanaged)
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('默认加载未受管理字段并渲染标准表格（全高铺满 + 首列选择控件 + 表头排序入口）', async () => {
    const { container } = renderPage()
    expect(await screen.findByText('PRODUCT')).toBeInTheDocument()
    expect(screen.getByText('CLIENT')).toBeInTheDocument()
    expect(screen.getByText('共 2 行')).toBeInTheDocument()
    // 全高铺满 + 表内滚动（页面级不出现滚动条）
    expect(container.querySelector('.erp-full-list-page')).not.toBeNull()
    // 首列选择控件：全选 + 行选择
    expect(screen.getByLabelText('选择当前页')).toBeInTheDocument()
    expect(screen.getAllByLabelText('选择此行')).toHaveLength(2)
    // 可排序列渲染表头操作按钮（clientSideSorting）
    expect(screen.getByLabelText('表头操作数据表名')).toBeInTheDocument()
  })

  it('点击行内选中该行并清除其它行（rowClickSingleSelect 单选语义）', async () => {
    renderPage()
    await screen.findByText('PRODUCT')
    fireEvent.click(screen.getByText('PRODUCT').closest('tr')!)
    let boxes = screen.getAllByLabelText('选择此行')
    expect(boxes[0]).toBeChecked()
    expect(boxes[1]).not.toBeChecked()

    fireEvent.click(screen.getByText('CLIENT').closest('tr')!)
    boxes = screen.getAllByLabelText('选择此行')
    expect(boxes[0]).not.toBeChecked()
    expect(boxes[1]).toBeChecked()
  })

  it('切换未知管理字段视图并清空选择', async () => {
    renderPage()
    await screen.findByText('PRODUCT')
    fireEvent.click(screen.getByText('PRODUCT').closest('tr')!)
    expect(screen.getAllByLabelText('选择此行')[0]).toBeChecked()

    fireEvent.click(screen.getByRole('button', { name: '未知管理字段' }))
    expect(await screen.findByText('GONE')).toBeInTheDocument()
    expect(apiClientMock.get).toHaveBeenCalledWith('/table-data/field-audit/orphan')
    expect(screen.getByText('共 1 行')).toBeInTheDocument()
    expect(screen.getByText('旧字段')).toBeInTheDocument()
    // 视图切换后清空上一视图的选中行
    await waitFor(() => {
      expect(screen.getAllByLabelText('选择此行')[0]).not.toBeChecked()
    })
  })

  it('加载失败显示错误与重新加载入口', async () => {
    apiClientMock.get.mockRejectedValue(new Error('boom'))
    renderPage()
    expect(await screen.findByText('数据加载失败')).toBeInTheDocument()
    expect(screen.getByText('发生未知错误，请稍后重试。')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '重新加载' })).toBeInTheDocument()
  })
})
