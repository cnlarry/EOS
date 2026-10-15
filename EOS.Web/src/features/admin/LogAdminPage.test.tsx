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
      ts: '2026-10-15 22:09:00.123',
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

  it('系统日志一屏一张电子表格：渲染日志条目，且不再有诊断信息卡', async () => {
    const { container } = renderPage()
    expect(await screen.findByText('请求失败 GET /api/v1/admin/groups')).toBeInTheDocument()
    // 「Error」在级别下拉里也有同名选项，这里只认行内的级别 badge
    expect(screen.getAllByText('Error').filter((node) => node.classList.contains('badge'))).toHaveLength(1)
    expect(screen.getByText('corr-1')).toBeInTheDocument()
    expect(container.querySelector('.erp-full-list-page')).not.toBeNull()
    // 诊断信息不再占一张卡（版本/构建/健康检查只在报障那一刻有用，改为随取）
    expect(screen.queryByText('产品版本')).toBeNull()
    expect(screen.queryByText('迁移台账')).toBeNull()
  })

  it('工具条给出刷新 / 下载诊断包 / 复制诊断信息三个动作', async () => {
    renderPage()
    await screen.findByText('请求失败 GET /api/v1/admin/groups')
    expect(screen.getByRole('button', { name: '刷新' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '下载诊断包' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '复制诊断信息' })).toBeInTheDocument()
  })

  it('按天文件下拉可切换，条数与体积在底部状态行', async () => {
    renderPage()
    await screen.findByText('请求失败 GET /api/v1/admin/groups')
    expect(screen.getByLabelText('日志文件')).toBeInTheDocument()
    expect(screen.getByText('api-20261015.log（2.0 KB）')).toBeInTheDocument()
    expect(screen.getByText(/命中 1 条 · 2 个文件/)).toBeInTheDocument()
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
