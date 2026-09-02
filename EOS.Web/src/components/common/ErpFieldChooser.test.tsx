import { apiClientMock } from '../../test/apiMock'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ErpFieldChooser, type ErpFieldChooserProps } from './ErpFieldChooser'


vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const columns = [
  { key: 'F_ID', label: '字段名', dataType: 'nvarchar', format: null },
  { key: 'F_DESC', label: '描述', dataType: 'nvarchar', format: null },
  { key: 'F_TYPE', label: '类型', dataType: 'nvarchar', format: null },
]
const rows = [
  { F_ID: 'PRO_NO', F_DESC: '产品编号', F_TYPE: 'nvarchar' },
  { F_ID: 'PRO_NAME', F_DESC: '产品名称', F_TYPE: 'nvarchar' },
]

function renderChooser(overrides: Partial<ErpFieldChooserProps> = {}) {
  const props: ErpFieldChooserProps = {
    open: true,
    title: '选择排序字段',
    source: { kind: 'sourceKey', key: 'menu-admin.fields', args: { tableId: 'PRODUCT' } },
    mode: 'multi',
    getRowId: (row) => String(row.F_ID),
    value: '',
    valueFormat: 'semicolon',
    onSave: vi.fn(),
    onClose: vi.fn(),
    ...overrides,
  }
  const { container } = render(<ErpFieldChooser {...props} />)
  return { ...props, container }
}

function rightRow(key: string) {
  const list = screen.getByLabelText('已选字段列表')
  return within(list).getByText(key).closest('.erp-field-picker-row') as HTMLElement
}

describe('ErpFieldChooser', () => {
  beforeEach(() => {
    apiClientMock.post.mockResolvedValue({ columns, rows, total: 2 })
    apiClientMock.get.mockResolvedValue({ columns, rows, total: 2 })
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('打开按 value 回显已选（semicolon），左栏勾选加入右栏，保存序列化为分号串', async () => {
    const props = renderChooser({ value: 'PRO_NO' })
    await waitFor(() => expect(screen.getByLabelText('选择字段 PRO_NO')).toBeInTheDocument())

    const proNoCheckbox = screen.getByLabelText('选择字段 PRO_NO') as HTMLInputElement
    expect(proNoCheckbox.checked).toBe(true)

    fireEvent.click(screen.getByLabelText('选择字段 PRO_NAME'))
    expect(within(rightRow('PRO_NAME')).getByText('产品名称')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    expect(props.onSave).toHaveBeenCalledWith('PRO_NO;PRO_NAME')
    expect(props.onClose).toHaveBeenCalled()
  })

  it('sort 模式（comma-dir）：回显带方向，右栏切换升降序，保存为逗号+方向串', async () => {
    const props = renderChooser({
      mode: 'sort',
      valueFormat: 'comma-dir',
      value: 'PRO_NO ASC,PRO_NAME DESC',
    })
    await waitFor(() => expect(screen.getByLabelText('选择字段 PRO_NO')).toBeInTheDocument())

    const nameRow = rightRow('PRO_NAME')
    expect(within(nameRow).getByRole('button', { name: '降序' })).toBeInTheDocument()
    expect(within(rightRow('PRO_NO')).getByRole('button', { name: '升序' })).toBeInTheDocument()

    fireEvent.click(within(nameRow).getByRole('button', { name: '降序' }))
    expect(within(nameRow).getByRole('button', { name: '升序' })).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    expect(props.onSave).toHaveBeenCalledWith('PRO_NO ASC,PRO_NAME ASC')
  })

  it('下移调整已选顺序，保存保持新顺序', async () => {
    const props = renderChooser({ value: 'PRO_NO;PRO_NAME' })
    await waitFor(() => expect(screen.getByLabelText('选择字段 PRO_NO')).toBeInTheDocument())

    fireEvent.click(screen.getByRole('button', { name: '下移 PRO_NO' }))
    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    expect(props.onSave).toHaveBeenCalledWith('PRO_NAME;PRO_NO')
  })

  it('上移调整已选顺序，保存保持新顺序', async () => {
    const props = renderChooser({ value: 'PRO_NO;PRO_NAME' })
    await waitFor(() => expect(screen.getByLabelText('选择字段 PRO_NO')).toBeInTheDocument())

    fireEvent.click(screen.getByRole('button', { name: '上移 PRO_NAME' }))
    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    expect(props.onSave).toHaveBeenCalledWith('PRO_NAME;PRO_NO')
  })

  it('移除已选字段，保存串同步移除', async () => {
    const props = renderChooser({ value: 'PRO_NO;PRO_NAME' })
    await waitFor(() => expect(screen.getByLabelText('选择字段 PRO_NO')).toBeInTheDocument())

    fireEvent.click(screen.getByRole('button', { name: '移除 PRO_NAME' }))
    expect(within(rightRow('PRO_NO')).getByText('产品编号')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '移除 PRO_NAME' })).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    expect(props.onSave).toHaveBeenCalledWith('PRO_NO')
  })

  it('comma 格式（报表）：行键为表.字段，保存为逗号分隔原文', async () => {
    const reportRows = [
      { T_ID: 'COP_M', F_ID: 'SEND_NO', F_DESC: '送货单号', F_TYPE: 'nvarchar' },
      { T_ID: 'COP_M', F_ID: 'SEND_DATE', F_DESC: '送货日期', F_TYPE: 'nvarchar' },
    ]
    apiClientMock.post.mockResolvedValue({ columns, rows: reportRows, total: 2 })
    const props = renderChooser({
      source: { kind: 'sourceKey', key: 'report-admin.fields', args: { moduleId: '3302' } },
      value: 'COP_M.SEND_NO',
      valueFormat: 'comma',
      getRowId: (row: { T_ID?: unknown; F_ID?: unknown }) => `${String(row.T_ID)}.${String(row.F_ID)}`,
    })
    await waitFor(() => expect(screen.getByLabelText('选择字段 COP_M.SEND_NO')).toBeInTheDocument())
    expect((screen.getByLabelText('选择字段 COP_M.SEND_NO') as HTMLInputElement).checked).toBe(true)

    fireEvent.click(screen.getByLabelText('选择字段 COP_M.SEND_DATE'))
    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    expect(props.onSave).toHaveBeenCalledWith('COP_M.SEND_NO,COP_M.SEND_DATE')
  })

  it('关键字查询：回车/查询按钮重新请求并携带 keyword', async () => {
    renderChooser()
    await waitFor(() => expect(screen.getByLabelText('选择字段 PRO_NO')).toBeInTheDocument())

    fireEvent.change(screen.getByPlaceholderText('输入字段名/描述，回车查询'), { target: { value: '产品' } })
    fireEvent.keyDown(screen.getByPlaceholderText('输入字段名/描述，回车查询'), { key: 'Enter' })
    await waitFor(() => expect(apiClientMock.post).toHaveBeenLastCalledWith('/chooser/query', expect.objectContaining({
      sourceKey: 'menu-admin.fields',
      args: { tableId: 'PRODUCT' },
      keyword: '产品',
      page: 1,
    })))
  })

  it('滚动到底自动加载下一页并追加字段', async () => {
    apiClientMock.post
      .mockResolvedValueOnce({ columns, rows, total: 4 })
      .mockResolvedValueOnce({
        columns,
        rows: [{ F_ID: 'PRO_COLOR', F_DESC: '产品颜色', F_TYPE: 'nvarchar' }],
        total: 4,
      })
    renderChooser()
    await waitFor(() => expect(screen.getByLabelText('选择字段 PRO_NO')).toBeInTheDocument())
    expect(screen.getByText(/已加载 2 个/)).toBeInTheDocument()

    const list = screen.getByLabelText('待选字段列表')
    Object.defineProperty(list, 'scrollTop', { value: 1000, configurable: true })
    Object.defineProperty(list, 'scrollHeight', { value: 1100, configurable: true })
    Object.defineProperty(list, 'clientHeight', { value: 100, configurable: true })
    fireEvent.scroll(list)

    await waitFor(() => expect(screen.getByLabelText('选择字段 PRO_COLOR')).toBeInTheDocument())
    expect(apiClientMock.post).toHaveBeenLastCalledWith('/chooser/query', expect.objectContaining({ page: 2 }))
  })
})
