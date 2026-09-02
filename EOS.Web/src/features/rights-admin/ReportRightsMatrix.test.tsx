import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { ReportRightsRow } from './types'
import { ReportRightsMatrix } from './ReportRightsMatrix'


vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const reportRows: ReportRightsRow[] = [
  {
    moduleId: 129801, moduleTitle: '产品资料明细', reportId: 'R129801', reportName: '产品资料明细表',
    preview: false, print: false, export: false, dataFilter: '', hasPersonal: true,
    effective: { source: 'group', preview: false, print: false, export: false, dataFilter: '' },
  },
]

function renderMatrix() {
  return renderWithProviders(
      <ReportRightsMatrix open mode="group" targetId="CG" onClose={vi.fn()} />
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
