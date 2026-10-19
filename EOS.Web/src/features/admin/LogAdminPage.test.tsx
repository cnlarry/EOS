import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { ToastProvider } from '../../components/ui/Toast'
import { screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { LogAdminPage } from './LogAdminPage'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const files = {
  files: [
    { name: 'api-20261015.log', sizeBytes: 2048, lastWriteTime: '2026-10-15 22:10:00' },
    { name: 'api-20261014.log', sizeBytes: 1024, lastWriteTime: '2026-10-14 22:10:00' },
  ],
  totalBytes: 3072,
  levels: ['Warning', 'Error'],
}

const entries = {
  entries: [
    {
      ts: '2026-10-15T22:09:00.123+08:00',
      level: 'Error',
      category: 'EOS.API.RequestLoggingMiddleware',
      event: 'request_failed',
      message: '请求失败 GET /api/v1/admin/groups',
      correlationId: 'corr-1',
      moduleId: '2305',
      errorCode: 'FORBIDDEN',
      exception: null,
      file: 'api-20261015.log',
    },
  ],
  count: 1,
  truncated: false,
  unavailable: [],
}

// 下载诊断包的结果走全局轻提示，页面依赖 ToastProvider
function renderPage() {
  return renderWithProviders(<ToastProvider><LogAdminPage /></ToastProvider>)
}

describe('LogAdminPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation(async (path: string) =>
      path.startsWith('/logs/files') ? files : entries)
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('系统日志一屏一张电子表格：渲染日志条目（时间到秒），且不再有诊断信息卡', async () => {
    const { container } = renderPage()
    expect(await screen.findByText('请求失败 GET /api/v1/admin/groups')).toBeInTheDocument()
    // 时间列取到秒：写盘格式里的毫秒与时区后缀不显示
    expect(screen.getByText('2026-10-15 22:09:00')).toBeInTheDocument()
    // 「Error」在级别下拉里也有同名选项，这里只认行内的级别 badge
    expect(screen.getAllByText('Error').filter((node) => node.classList.contains('badge'))).toHaveLength(1)
    expect(screen.getByText('corr-1')).toBeInTheDocument()
    expect(container.querySelector('.erp-full-list-page')).not.toBeNull()
    // 诊断信息不再占一张卡（版本/构建/健康检查只在报障那一刻有用，改为随取）
    expect(screen.queryByText('产品版本')).toBeNull()
    expect(screen.queryByText('迁移台账')).toBeNull()
  })

  it('与统一工作台列表同一套形制：首列复选框 + 列宽可拖', async () => {
    const { container } = renderPage()
    await screen.findByText('请求失败 GET /api/v1/admin/groups')
    expect(screen.getAllByRole('checkbox')).toHaveLength(entries.entries.length + 1)
    expect(container.querySelectorAll('.erp-col-resizer').length).toBeGreaterThan(0)
  })

  it('工具条给出刷新 / 下载诊断包 / 复制诊断信息三个动作', async () => {
    renderPage()
    await screen.findByText('请求失败 GET /api/v1/admin/groups')
    expect(screen.getByRole('button', { name: '刷新' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '下载诊断包' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '复制诊断信息' })).toBeInTheDocument()
  })

  it('按天文件下拉可切换，条数与体积在底部状态行；不再有“最近 N 条”控件', async () => {
    renderPage()
    await screen.findByText('请求失败 GET /api/v1/admin/groups')
    expect(screen.getByLabelText('日志文件')).toBeInTheDocument()
    expect(screen.getByText('api-20261015.log（2.0 KB）')).toBeInTheDocument()
    expect(screen.getByText(/命中 1 条 · 2 个文件/)).toBeInTheDocument()
    // 一次读多少条不是用户要做的决定：读更多靠缩小条件
    expect(screen.queryByLabelText('条数')).toBeNull()
    expect(screen.queryByText(/最近 \d+ 条/)).toBeNull()
  })

  it('时间、消息、报障编号全同的两行日志各自成行（行键不依赖日志字段）', async () => {
    const duplicated = {
      ...entries,
      entries: [
        { ...entries.entries[0], exception: '第一份异常栈' },
        { ...entries.entries[0], exception: '第二份异常栈' },
      ],
      count: 2,
    }
    apiClientMock.get.mockImplementation(async (path: string) =>
      path.startsWith('/logs/files') ? files : duplicated)
    // 行键重复时 React 会打 "Encountered two children with the same key"，
    // 并在窗口化下复用/丢弃行节点（表现为表格中间大面积空白）
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => undefined)
    const { container } = renderPage()
    expect(await screen.findAllByText('请求失败 GET /api/v1/admin/groups')).toHaveLength(2)
    expect(container.querySelectorAll('tbody tr[data-order-id]')).toHaveLength(2)
    const duplicateKeyWarnings = consoleError.mock.calls.filter((args) => String(args[0]).includes('same key'))
    expect(duplicateKeyWarnings).toHaveLength(0)
    consoleError.mockRestore()
  })

  it('读失败的文件明说，不伪装成没有日志', async () => {
    apiClientMock.get.mockImplementation(async (path: string) =>
      path.startsWith('/logs/files')
        ? files
        : { ...entries, entries: [], count: 0, unavailable: ['api-20261015.log'] })
    renderPage()
    expect(await screen.findByText(/有 1 个文件读取失败/)).toBeInTheDocument()
    expect(screen.getByText(/不等于这些文件里没有日志/)).toBeInTheDocument()
  })
})
