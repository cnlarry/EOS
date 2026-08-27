import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { ReportRightsRow } from './types'
import { ReportRightsMatrix } from './ReportRightsMatrix'

const apiClientMock = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  postFile: vi.fn(),
}))

vi.mock('../../services/api', () => ({ apiClient: apiClientMock }))

const reportRows: ReportRightsRow[] = [
  {
    moduleId: 129801, moduleTitle: '产品资料明细', reportId: 'R129801', reportName: '产品资料明细表',
    preview: false, print: false, export: false, dataFilter: '', hasPersonal: false,
    effective: { source: 'none', preview: false, print: false, export: false, dataFilter: '' },
  },
]

function renderMatrix() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ReportRightsMatrix open mode="group" targetId="CG" onClose={vi.fn()} />
    </QueryClientProvider>,
  )
}

describe('ReportRightsMatrix', () => {
  beforeEach(() => {
    apiClientMock.get.mockResolvedValue(reportRows)
    apiClientMock.put.mockResolvedValue(undefined)
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('渲染报表权限行并保存勾选', async () => {
    renderMatrix()
    await waitFor(() => expect(screen.getByText('R129801')).toBeInTheDocument())
    fireEvent.click(screen.getByLabelText('预览 R129801'))
    fireEvent.click(screen.getByLabelText('列印 R129801'))
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalled())
    const [url, body] = apiClientMock.put.mock.calls[0]
    expect(url).toBe('/admin/groups/CG/report-rights')
    expect(body.items).toHaveLength(1)
    expect(body.items[0]).toMatchObject({ moduleId: 129801, reportId: 'R129801', preview: true, print: true, export: false })
  })

  it('编辑 DATA_FILTER 并随保存提交', async () => {
    renderMatrix()
    await waitFor(() => expect(screen.getByText('R129801')).toBeInTheDocument())
    const input = await screen.findByPlaceholderText('受控过滤表达式（非法保存时拒绝 400）')
    fireEvent.change(input, { target: { value: "STATE=1" } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalled())
    const body = apiClientMock.put.mock.calls[0][1]
    expect(body.items[0]).toMatchObject({ dataFilter: 'STATE=1' })
  })
})
