import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { LayoutDesignerPage } from './LayoutDesignerPage'


vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

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
  return renderWithProviders(
      <MemoryRouter initialEntries={['/layout-designer/1405']}>
        <Routes>
          <Route path="/layout-designer/:moduleId" element={<LayoutDesignerPage />} />
        </Routes>
      </MemoryRouter>
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
    // 元素库 6 个拖拽项（完整模式）
    for (const label of ['文本', '字段', '图片', '分隔线', '矩形', '表格', '条形码']) {
      expect(screen.getByRole('button', { name: label })).toBeInTheDocument()
    }
    // 快捷文本
    expect(screen.getByRole('button', { name: '页码' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '日期' })).toBeInTheDocument()
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
    await waitFor(() => expect(screen.getByText('数据来源')).toBeInTheDocument())
    fireEvent.click(screen.getByText('生成预览'))
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
    // 画布表格列头（字段面板也有"料号"标签，取画布中的最后一个）
    fireEvent.click(screen.getAllByText('料号').at(-1)!, { shiftKey: true })
    await waitFor(() => expect(screen.getByText(/对齐（2 个元素）/)).toBeInTheDocument())
  })

  it('完整模式可编辑 table 列并添加列', async () => {
    renderPage()
    await waitFor(() => expect(screen.getAllByText('料号').length).toBeGreaterThan(0))
    fireEvent.click(screen.getAllByText('料号').at(-1)!)
    await waitFor(() => expect(screen.getByText('列定义')).toBeInTheDocument())
    fireEvent.click(screen.getByText('+ 添加列'))
    await waitFor(() => expect(screen.getByLabelText('列 2 字段')).toBeInTheDocument())
  })

  it('图层列表展示分组与元素', async () => {
    renderPage()
    await waitFor(() => expect(screen.getAllByText('图层').length).toBeGreaterThan(0))
    expect(screen.getByText('正文')).toBeInTheDocument()
    expect(screen.getByText('页脚')).toBeInTheDocument()
    expect(screen.getAllByText('h1').length).toBeGreaterThan(0)
    expect(screen.getAllByText('c1').length).toBeGreaterThan(0)
    expect(screen.getAllByText('t1').length).toBeGreaterThan(0)
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
    await waitFor(() => expect(screen.getByText('预览')).toBeInTheDocument())
    fireEvent.click(screen.getByText('预览'))
    await waitFor(() => expect(screen.getByLabelText('数据场景')).toBeInTheDocument())
    fireEvent.change(screen.getByLabelText('数据场景'), { target: { value: '2' } })
    fireEvent.click(screen.getByText('生成预览'))
    await waitFor(() => expect(apiClientMock.postFile).toHaveBeenCalledWith(
      '/layout-designer/1405/preview',
      expect.objectContaining({ rows: 50, variant: null }),
    ))
  })

  it('真实单据预览请求携带主键', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText('预览')).toBeInTheDocument())
    fireEvent.click(screen.getByText('预览'))
    await waitFor(() => expect(screen.getByLabelText('真实单据')).toBeInTheDocument())
    fireEvent.click(screen.getByLabelText('真实单据'))
    await waitFor(() => expect(screen.getByPlaceholderText('输入真实单据的单号')).toBeInTheDocument())
    fireEvent.change(screen.getByPlaceholderText('输入真实单据的单号'), { target: { value: 'DD13010001' } })
    fireEvent.click(screen.getByText('生成预览'))
    await waitFor(() => expect(apiClientMock.postFile).toHaveBeenCalledWith(
      '/layout-designer/1405/preview',
      expect.objectContaining({ key: ['DD13010001'] }),
    ))
  })

  it('右键元素弹出上下文菜单', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText('[MASTER.ORDER_NO]')).toBeInTheDocument())
    fireEvent.contextMenu(screen.getByText('[MASTER.ORDER_NO]'))
    await waitFor(() => expect(screen.getByText('复制')).toBeInTheDocument())
    expect(screen.getByText('复制样式')).toBeInTheDocument()
    expect(screen.getByText('粘贴样式')).toBeInTheDocument()
    expect(screen.getByText('置顶')).toBeInTheDocument()
    expect(screen.getByText('上一层')).toBeInTheDocument()
    expect(screen.getByText('下一层')).toBeInTheDocument()
    expect(screen.getByText('置底')).toBeInTheDocument()
    expect(screen.getByText('删除')).toBeInTheDocument()
    expect(screen.getAllByText('属性').length).toBeGreaterThan(0)
  })

  it('工具栏提供模板与历史入口', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByTitle('模板库')).toBeInTheDocument())
    expect(screen.getByTitle('版本历史')).toBeInTheDocument()
  })

  it('右侧面板 Tab 可在图层与属性间切换，点击元素自动切到属性', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByRole('tab', { name: '图层' })).toBeInTheDocument())
    // 默认图层面板
    expect(screen.getByText('正文')).toBeInTheDocument()
    // 点击元素 → 自动切到属性
    fireEvent.click(screen.getByText('[MASTER.ORDER_NO]'))
    await waitFor(() => expect(screen.getByText('字段绑定')).toBeInTheDocument())
    // 手动切回图层
    fireEvent.click(screen.getByRole('tab', { name: '图层' }))
    await waitFor(() => expect(screen.getByText('正文')).toBeInTheDocument())
    expect(screen.queryByText('字段绑定')).toBeNull()
  })

  it('历史 Tab 显示操作记录', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByRole('tab', { name: '历史' })).toBeInTheDocument())
    fireEvent.click(screen.getByRole('tab', { name: '历史' }))
    await waitFor(() => expect(screen.getByText('当前状态')).toBeInTheDocument())
    expect(screen.getByText(/点击历史步骤可跳转恢复/)).toBeInTheDocument()
  })

  it('页模板弹窗可打开并切换第一页/续页/末页', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByTitle('多页版式模板')).toBeInTheDocument())
    fireEvent.click(screen.getByTitle('多页版式模板'))
    await waitFor(() => expect(screen.getByText(/页模板（多页版式/)).toBeInTheDocument())
    expect(screen.getByRole('button', { name: '续页' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '末页' })).toBeInTheDocument()
    // 添加页头文本
    fireEvent.click(screen.getByRole('button', { name: '+ 文本' }))
    await waitFor(() => expect(screen.getByPlaceholderText('文本内容')).toBeInTheDocument())
  })

  it('字段元素绑定下拉只提供主表与系统值（不含明细字段）', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText('[MASTER.ORDER_NO]')).toBeInTheDocument())
    fireEvent.click(screen.getByText('[MASTER.ORDER_NO]'))
    await waitFor(() => expect(screen.getByLabelText('字段绑定')).toBeInTheDocument())
    expect(screen.getByText('MASTER.ORDER_NO（订单号）')).toBeInTheDocument()
    expect(screen.getByText('SYS.TITLE')).toBeInTheDocument()
    expect(screen.queryByText('DETAILS.PRO_NO（料号）')).toBeNull()
  })

  it('右侧面板可折叠为窄条并展开', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByTitle('折叠面板')).toBeInTheDocument())
    fireEvent.click(screen.getByTitle('折叠面板'))
    await waitFor(() => expect(screen.getByTitle('图层')).toBeInTheDocument())
    fireEvent.click(screen.getByTitle('图层'))
    await waitFor(() => expect(screen.getByText('正文')).toBeInTheDocument())
  })

  it('文本内容输入 {{ 触发字段补全候选', async () => {
    renderPage()
    await waitFor(() => expect(screen.getAllByText('h1').length).toBeGreaterThan(0))
    fireEvent.click(screen.getAllByText('h1')[0])
    await waitFor(() => expect(screen.getByPlaceholderText(/静态文本/)).toBeInTheDocument())
    const input = screen.getByPlaceholderText(/静态文本/)
    fireEvent.change(input, { target: { value: '编号：{{' } })
    await waitFor(() => expect(screen.getByText('MASTER.ORDER_NO')).toBeInTheDocument())
  })
})
