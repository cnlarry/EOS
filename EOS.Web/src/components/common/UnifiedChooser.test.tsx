import { apiClientMock } from '../../test/apiMock'
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { UnifiedChooser, type UnifiedChooserSource } from './UnifiedChooser'


vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const columns = [
  { key: 'CLIENT_ID', label: '客户编号', dataType: 'nvarchar', format: null },
  { key: 'CLIENT_NAME', label: '客户名称', dataType: 'nvarchar', format: null },
]
const rows = [
  { CLIENT_ID: 'C0', CLIENT_NAME: '客户甲' },
  { CLIENT_ID: 'C1', CLIENT_NAME: '客户乙' },
]

class FakeIntersectionObserver {
  static instances: FakeIntersectionObserver[] = []
  callback: IntersectionObserverCallback

  constructor(callback: IntersectionObserverCallback) {
    this.callback = callback
    FakeIntersectionObserver.instances.push(this)
  }

  observe = vi.fn()
  unobserve = vi.fn()
  disconnect = vi.fn()

  trigger(entries: IntersectionObserverEntry[]) {
    this.callback(entries, this as unknown as IntersectionObserver)
  }
}

function renderChooser(overrides: Partial<Parameters<typeof UnifiedChooser>[0]> = {}) {
  const props = {
    open: true,
    title: '选择客户',
    source: { kind: 'sourceKey', key: 'demo.clients' } as UnifiedChooserSource,
    mode: 'single' as const,
    onPick: vi.fn(),
    onClose: vi.fn(),
    ...overrides,
  }
  return { ...render(<UnifiedChooser {...props} />), props }
}

describe('UnifiedChooser', () => {
  beforeEach(() => {
    // 用 mockReset 而不是只靠 afterEach 的 clearAllMocks：后者只清调用记录，
    // **不清 mockResolvedValueOnce 的队列**。上一条用例若没把一次性返回值消耗完
    // （例如触底没触发、第二页请求没发出），残留值会喂给下一条用例，
    // 表现成"下一条用例突然拿不到自己的数据"——两条本来独立的断言被绑在一起。
    apiClientMock.post.mockReset()
    apiClientMock.get.mockReset()
    apiClientMock.post.mockResolvedValue({ columns, rows, total: 2 })
    apiClientMock.get.mockResolvedValue({ columns, rows, total: 2 })
    vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver)
    FakeIntersectionObserver.instances = []
  })

  afterEach(() => {
    vi.clearAllMocks()
    vi.unstubAllGlobals()
  })

  it('sourceKey：POST /chooser/query 拉取数据并渲染网格', async () => {
    renderChooser()
    await waitFor(() => expect(screen.getByText('客户甲')).toBeInTheDocument())
    expect(apiClientMock.post).toHaveBeenCalledWith('/chooser/query', expect.objectContaining({
      sourceKey: 'demo.clients',
      page: 1,
      pageSize: 50,
    }))
    expect(screen.getByText('共 2 条，已选 0 项')).toBeInTheDocument()
  })

  it('sourceKey：首次加载应用服务端 defaultKeys 作为默认显示列', async () => {
    apiClientMock.post.mockResolvedValue({ columns, rows, total: 2, defaultKeys: ['CLIENT_ID'] })
    renderChooser()
    await waitFor(() => expect(screen.getByText('C0')).toBeInTheDocument())
    expect(screen.queryByText('客户甲')).not.toBeInTheDocument()
  })

  it('单选模式：首列 Radio，勾选不触发选择，左上角确认才回填', async () => {
    const { props } = renderChooser()
    await waitFor(() => expect(screen.getByText('客户甲')).toBeInTheDocument())
    const confirm = screen.getByRole('button', { name: '确认' })
    expect(confirm).toBeDisabled()

    const radios = screen.getAllByRole('radio')
    expect(radios).toHaveLength(2)
    fireEvent.click(radios[0])
    expect(props.onPick).not.toHaveBeenCalled()
    expect(confirm).toBeEnabled()
    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    expect(props.onPick).toHaveBeenCalledWith([{ CLIENT_ID: 'C0', CLIENT_NAME: '客户甲' }])
  })

  it('点行只改变选中状态，不触发选择动作', async () => {
    const { props } = renderChooser()
    await waitFor(() => expect(screen.getByText('客户甲')).toBeInTheDocument())
    fireEvent.click(screen.getByText('客户甲').closest('tr')! as HTMLElement)
    expect(props.onPick).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    expect(props.onPick).toHaveBeenCalledWith([{ CLIENT_ID: 'C0', CLIENT_NAME: '客户甲' }])
  })

  it('多选模式：首列 CheckBox（含表头全选），确认回填勾选行', async () => {
    const { props } = renderChooser({ mode: 'multi' })
    await waitFor(() => expect(screen.getByText('客户甲')).toBeInTheDocument())
    const confirm = screen.getByRole('button', { name: '确认' })
    expect(confirm).toBeDisabled()

    const checkboxes = screen.getAllByRole('checkbox')
    fireEvent.click(checkboxes[0])
    expect(props.onPick).not.toHaveBeenCalled()
    expect(confirm).toBeEnabled()
    fireEvent.click(confirm)
    expect(props.onPick).toHaveBeenCalledWith(rows)
  })

  it('loader：使用调用方加载函数，客户端数据直接展示', async () => {
    const load = vi.fn().mockResolvedValue({ columns, rows, total: 2 })
    const { props } = renderChooser({ source: { kind: 'loader', load } })
    await waitFor(() => expect(screen.getByText('客户甲')).toBeInTheDocument())
    expect(load).toHaveBeenCalledWith(expect.objectContaining({ page: 1, pageSize: 50 }))
    fireEvent.click(screen.getAllByRole('radio')[1])
    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    expect(props.onPick).toHaveBeenCalledWith([{ CLIENT_ID: 'C1', CLIENT_NAME: '客户乙' }])
  })

  it('formField：GET form-chooser 并透传主表/明细模板值', async () => {
    const { props } = renderChooser({
      source: { kind: 'formField', moduleId: '1209', fieldKey: 'CLIENT_ID' },
      masterValues: { PRO_NO: 'P1' },
      detailValues: { LINE_NO: '2' },
    })
    await waitFor(() => expect(screen.getByText('客户甲')).toBeInTheDocument())
    expect(apiClientMock.get).toHaveBeenCalledWith(
      expect.stringContaining('/document-workbench/1209/form-chooser/CLIENT_ID'),
      expect.objectContaining({
        query: expect.objectContaining({
          master: JSON.stringify({ PRO_NO: 'P1' }),
          detail: JSON.stringify({ LINE_NO: '2' }),
        }),
      }),
    )
    fireEvent.click(screen.getAllByRole('radio')[0])
    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    expect(props.onPick).toHaveBeenCalledTimes(1)
  })

  it('关键字查询：输入后点查询重新请求并带 keyword/filterField', async () => {
    renderChooser()
    await waitFor(() => expect(screen.getByText('客户甲')).toBeInTheDocument())
    fireEvent.change(screen.getByPlaceholderText('输入查询条件，回车查询'), { target: { value: '甲' } })
    fireEvent.change(screen.getAllByRole('combobox')[0], { target: { value: 'CLIENT_NAME' } })
    fireEvent.click(screen.getByRole('button', { name: '查询' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenLastCalledWith('/chooser/query', expect.objectContaining({
      keyword: '甲',
      filterField: 'CLIENT_NAME',
      page: 1,
    })))
  })

  it('服务端分页：滚动到底自动加载下一页并追加行', async () => {
    apiClientMock.post
      .mockResolvedValueOnce({ columns, rows, total: 4 })
      .mockResolvedValueOnce({ columns, rows: [{ CLIENT_ID: 'C2', CLIENT_NAME: '客户丙' }, { CLIENT_ID: 'C3', CLIENT_NAME: '客户丁' }], total: 4 })
    renderChooser()
    await waitFor(() => expect(screen.getByText('客户甲')).toBeInTheDocument())
    expect(screen.getByText(/已加载 2 条/)).toBeInTheDocument()
    // 触底哨兵由 ErpTable 在"有数据且还有下一页"时挂载（onEndReached/hasMore 一起下发），
    // 观察器随那次提交创建；先等它出现再触发，别把"挂载时机"当成"必然已就绪"。
    await waitFor(() => expect(FakeIntersectionObserver.instances.length).toBeGreaterThan(0))
    const observer = FakeIntersectionObserver.instances.at(-1)!
    act(() => observer.trigger([{ isIntersecting: true } as IntersectionObserverEntry]))
    await waitFor(() => expect(screen.getByText('客户丙')).toBeInTheDocument())
    expect(apiClientMock.post).toHaveBeenLastCalledWith('/chooser/query', expect.objectContaining({ page: 2 }))
    expect(screen.queryByText(/正在加载更多/)).not.toBeInTheDocument()
  })

  it('高级查询：添加条件并应用后携带结构化条件重新请求', async () => {
    renderChooser()
    await waitFor(() => expect(screen.getByText('客户甲')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '高级查询' }))
    await waitFor(() => expect(screen.getByRole('heading', { name: '高级查询' })).toBeInTheDocument())
    fireEvent.change(screen.getByLabelText('条件字段'), { target: { value: 'CLIENT_NAME' } })
    fireEvent.change(screen.getByLabelText('条件运算符'), { target: { value: 'contains' } })
    fireEvent.change(screen.getByPlaceholderText('值'), { target: { value: '甲' } })
    fireEvent.click(screen.getByRole('button', { name: '应用' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenLastCalledWith('/chooser/query', expect.objectContaining({
      conditions: [{ field: 'CLIENT_NAME', operator: 'contains', value: '甲', valueTo: '', logic: 'and' }],
    })))
  })

  it('选择列：保存显示列配置后网格按配置过滤列', async () => {
    renderChooser()
    await waitFor(() => expect(screen.getByText('客户甲')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '选择列' }))
    await waitFor(() => expect(screen.getByRole('heading', { name: '选择列' })).toBeInTheDocument())
    const visibleSelect = screen.getByLabelText('选择器列已选字段') as HTMLSelectElement
    fireEvent.change(visibleSelect, { target: { value: 'CLIENT_ID' } })
    fireEvent.click(screen.getByTitle('移除选中字段'))
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(screen.queryByText('C0')).not.toBeInTheDocument())
    expect(screen.getByText('客户甲')).toBeInTheDocument()
  })

  it('选择列：保存后按所选顺序渲染列，而不是服务端字段顺序', async () => {
    renderChooser()
    await waitFor(() => expect(screen.getByText('客户甲')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '选择列' }))
    await waitFor(() => expect(screen.getByRole('heading', { name: '选择列' })).toBeInTheDocument())
    const visibleSelect = screen.getByLabelText('选择器列已选字段') as HTMLSelectElement
    fireEvent.change(visibleSelect, { target: { value: 'CLIENT_NAME' } })
    fireEvent.click(screen.getByTitle('上移'))
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(screen.queryByRole('heading', { name: '选择列' })).not.toBeInTheDocument())
    const headers = screen.getAllByRole('columnheader').map(cell => (cell.textContent ?? '').trim())
    expect(headers).toEqual(['', '客户名称', '客户编号'])
  })
})
