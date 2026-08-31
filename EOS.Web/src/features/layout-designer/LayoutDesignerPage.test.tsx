import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { LayoutDesignerPage } from './LayoutDesignerPage'

const apiClientMock = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  postFile: vi.fn(),
}))

vi.mock('../../services/api', () => ({ apiClient: apiClientMock }))

const definition = {
  formatId: '1405',
  title: '客户订单',
  isCustom: false,
  layoutId: null,
  mode: { canDesign: true, canAdjust: true },
  layoutJson: JSON.stringify({
    schemaVersion: 1,
    kind: 'document',
    page: { size: 'A4', orientation: 'portrait', margin: { top: 11.29, right: 11.29, bottom: 11.29, left: 11.29 } },
    sections: {
      header: { height: 26, elements: [
        { id: 'h1', type: 'text', x: 0, y: 0.71, w: 187.4, h: 7.2, content: '{{SYS.HEADER_COMPANY}}', style: { fontSize: 14, bold: true, align: 'center' } },
      ] },
      content: { elements: [
        { id: 'c1', type: 'field', x: 0, y: 4.6, w: 100, h: 4.8, field: 'MASTER.ORDER_NO', style: { fontSize: 9 } },
        { id: 't1', type: 'table', x: 0, y: 20, w: 180, h: 60, dataSource: 'details', showHeader: true,
          columns: [{ field: 'DETAILS.PRO_NO', label: '料号', width: 40 }] },
      ] },
      footer: { height: 14, elements: [] },
    },
  }),
  dataContract: {
    columns: [{ key: 'ORDER_NO', label: '订单号', type: 'string' }],
    detailColumns: [{ key: 'PRO_NO', label: '料号', type: 'string' }],
  },
  systemFields: ['SYS.TITLE', 'SYS.PAGE_NUMBER'],
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/layout-designer/1405']}>
        <Routes>
          <Route path="/layout-designer/:moduleId" element={<LayoutDesignerPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('LayoutDesignerPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockResolvedValue(definition)
    apiClientMock.post.mockResolvedValue({ saved: true })
    apiClientMock.postFile.mockResolvedValue(new Blob(['pdf']))
  })

  afterEach(() => { vi.clearAllMocks() })

  it('加载 definition 后渲染画布与元素', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText(/客户订单/)).toBeInTheDocument())
    expect(screen.getByText('header')).toBeInTheDocument()
    expect(screen.getByText('content')).toBeInTheDocument()
    expect(screen.getByText('[MASTER.ORDER_NO]')).toBeInTheDocument()
  })

  it('点击元素打开属性面板并可编辑', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText('[MASTER.ORDER_NO]')).toBeInTheDocument())
    fireEvent.click(screen.getByText('[MASTER.ORDER_NO]'))
    await waitFor(() => expect(screen.getByText('字段绑定')).toBeInTheDocument())
    expect(screen.getByLabelText(/X \(mm\)/)).toHaveValue(0)
    fireEvent.change(screen.getByLabelText(/X \(mm\)/), { target: { value: '12' } })
    expect(screen.getByLabelText(/X \(mm\)/)).toHaveValue(12)
  })

  it('保存调用 save 接口并标记已保存', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText('保存')).toBeInTheDocument())
    fireEvent.click(screen.getByText('保存'))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/layout-designer/1405/save',
      expect.objectContaining({ clientId: null }),
    ))
    await waitFor(() => expect(screen.getByText('已保存')).toBeInTheDocument())
  })

  it('预览调用 preview 接口', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText('预览')).toBeInTheDocument())
    fireEvent.click(screen.getByText('预览'))
    await waitFor(() => expect(apiClientMock.postFile).toHaveBeenCalledWith(
      '/layout-designer/1405/preview',
      expect.objectContaining({ clientId: null }),
    ))
  })

  it('画布缩放下拉可切换缩放比例', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByLabelText('画布缩放')).toBeInTheDocument())
    fireEvent.change(screen.getByLabelText('画布缩放'), { target: { value: '2' } })
    expect(screen.getByLabelText('画布缩放')).toHaveValue('2')
  })

  it('Shift 多选两个元素后出现对齐工具栏', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText('[MASTER.ORDER_NO]')).toBeInTheDocument())
    fireEvent.click(screen.getByText('[MASTER.ORDER_NO]'), { shiftKey: true })
    fireEvent.click(screen.getByText('料号'), { shiftKey: true })
    await waitFor(() => expect(screen.getByText(/对齐（2 个元素）/)).toBeInTheDocument())
  })

  it('完整模式可编辑 table 列并添加列', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText('料号')).toBeInTheDocument())
    fireEvent.click(screen.getByText('料号'))
    await waitFor(() => expect(screen.getByText('列定义')).toBeInTheDocument())
    fireEvent.click(screen.getByText('+ 添加列'))
    await waitFor(() => expect(screen.getByLabelText('列 2 字段')).toBeInTheDocument())
  })

  it('图层列表展示分组与元素', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText('图层')).toBeInTheDocument())
    expect(screen.getByText('正文')).toBeInTheDocument()
    expect(screen.getByText('页脚')).toBeInTheDocument()
    expect(screen.getByText('h1')).toBeInTheDocument()
    expect(screen.getByText('c1')).toBeInTheDocument()
    expect(screen.getByText('t1')).toBeInTheDocument()
  })

  it('页头字典弹窗可打开并显示条目', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path === '/layout-designer/1405/definition') return definition
      if (path === '/layout-designer/headers') {
        return [{ headerId: 'DEFAULT', name: '默认', company: '示例公司', companyEn: null, headerText: null, logoPath: null }]
      }
      throw new Error('unexpected')
    })
    renderPage()
    await waitFor(() => expect(screen.getByTitle('页头字典引用')).toBeInTheDocument())
    fireEvent.click(screen.getByTitle('页头字典引用'))
    await waitFor(() => expect(screen.getByText(/页头字典/)).toBeInTheDocument())
    await waitFor(() => expect(screen.getAllByText(/示例公司/).length).toBeGreaterThan(0))
  })

  it('微调模式隐藏元素库并显示简化属性面板', async () => {
    const adjustDefinition = { ...definition, mode: { canDesign: false, canAdjust: true } }
    apiClientMock.get.mockResolvedValue(adjustDefinition)
    renderPage()
    await waitFor(() => expect(screen.getByText('[MASTER.ORDER_NO]')).toBeInTheDocument())
    expect(screen.queryByText('元素库（拖入画布）')).toBeNull()
    fireEvent.click(screen.getByText('[MASTER.ORDER_NO]'))
    await waitFor(() => expect(screen.getByText('位置微调（mm）')).toBeInTheDocument())
    expect(screen.queryByText('字号')).toBeNull()
  })

  it('预览数据场景选择后预览请求携带对应参数', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByLabelText('预览数据场景')).toBeInTheDocument())
    fireEvent.change(screen.getByLabelText('预览数据场景'), { target: { value: '2' } })
    fireEvent.click(screen.getByText('预览'))
    await waitFor(() => expect(apiClientMock.postFile).toHaveBeenCalledWith(
      '/layout-designer/1405/preview',
      expect.objectContaining({ rows: 50, variant: null }),
    ))
  })
})
