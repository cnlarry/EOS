import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AuditLogPage } from './AuditLogPage'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const page = {
  rows: [
    {
      EVENT_ID: 91,
      OCCURRED_AT: '2026-10-15 10:20:30.000',
      CORRELATION_ID: 'corr-1',
      ACTOR_USER_ID: 'larry',
      ACTOR_DISPLAY_NAME: '拉里',
      MODULE_ID: 2313,
      RESOURCE_TYPE: 'MODULE',
      RESOURCE_KEY: '2313',
      ACTION: 'RIGHTS_UPDATE',
      RESULT: 0,
      ERROR_CODE: 'FORBIDDEN',
      SUMMARY: '缺少 CanSetup',
    },
    {
      EVENT_ID: 90,
      OCCURRED_AT: '2026-10-15 10:19:00.000',
      CORRELATION_ID: 'corr-2',
      ACTOR_USER_ID: 'ada',
      ACTOR_DISPLAY_NAME: '艾达',
      MODULE_ID: 2305,
      RESOURCE_TYPE: 'GROUP',
      RESOURCE_KEY: 'G01',
      ACTION: 'GROUP_UPDATE',
      RESULT: 1,
      ERROR_CODE: null,
      SUMMARY: '修改用户组',
    },
  ],
  total: 2,
  page: 1,
  pageSize: 20,
}

function renderPage() {
  return renderWithProviders(<AuditLogPage />)
}

describe('AuditLogPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation(async () => page)
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('渲染审计行：操作人 / 动作 / 对象 / 结果，失败行标失败', async () => {
    const { container } = renderPage()
    expect(await screen.findByText('RIGHTS_UPDATE')).toBeInTheDocument()
    expect(screen.getByText('GROUP_UPDATE')).toBeInTheDocument()
    expect(screen.getByText('larry')).toBeInTheDocument()
    expect(screen.getByText('ada')).toBeInTheDocument()
    // 「成功 / 失败」在结果筛选下拉里也有同名选项，这里只认行内的 badge
    const badges = (text: string) => screen.getAllByText(text).filter((node) => node.classList.contains('badge'))
    expect(badges('失败')).toHaveLength(1)
    expect(badges('成功')).toHaveLength(1)
    expect(screen.getByText('FORBIDDEN')).toBeInTheDocument()
    expect(container.querySelector('.erp-full-list-page')).not.toBeNull()
  })

  it('说清它与运行日志是两层（审计查库表，运行日志读文件）', async () => {
    renderPage()
    await screen.findByText('RIGHTS_UPDATE')
    expect(screen.getByText(/谁改的、改成什么/)).toBeInTheDocument()
    expect(screen.getByText(/运行日志/)).toBeInTheDocument()
  })

  it('加载失败显示错误与重新加载入口', async () => {
    apiClientMock.get.mockRejectedValue(new Error('boom'))
    renderPage()
    expect(await screen.findByText('数据加载失败')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '重新加载' })).toBeInTheDocument()
  })
})
