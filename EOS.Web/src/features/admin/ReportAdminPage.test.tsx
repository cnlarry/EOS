import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ReportAdminPage } from './ReportAdminPage'

const apiClientMock = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn(), postFile: vi.fn() }))
vi.mock('../../services/api', () => ({ apiClient: apiClientMock }))

const modules = [{ moduleId: 1404, description: '采购订单' }]
const reports = [{
  reportId: 'R1', reportName: '采购报表', moduleId: 1404, isoNo: null, headerId: null, tailId: null,
  footerText: null, defaultPaper: null, isDefault: true, reportFilter: null, defaultPrinter: null, remark: null,
}]
const fieldChooserData = {
  columns: [
    { key: 'T_ID', label: '表名', dataType: 'nvarchar', format: null },
    { key: 'F_ID', label: '字段名', dataType: 'nvarchar', format: null },
    { key: 'F_DESC', label: '描述', dataType: 'nvarchar', format: null },
    { key: 'F_TYPE', label: '类型', dataType: 'nvarchar', format: null },
  ],
  rows: [
    { T_ID: 'PO', F_ID: 'PRO_NO', F_DESC: '料号', F_TYPE: 'nvarchar' },
    { T_ID: 'PO', F_ID: 'QTY', F_DESC: '数量', F_TYPE: 'numeric' },
  ],
  total: 2,
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ReportAdminPage />
    </QueryClientProvider>,
  )
}

describe('ReportAdminPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path === '/report-admin/modules') return modules
      if (path.startsWith('/report-admin/reports')) return reports
      if (path.startsWith('/report-admin/sorts')) return []
      if (path === '/report-admin/headers') return []
      if (path === '/report-admin/tails') return []
      throw new Error(`unexpected GET ${path}`)
    })
    apiClientMock.post.mockImplementation(async (path: string) => {
      if (path === '/chooser/query') return fieldChooserData
      throw new Error(`unexpected POST ${path}`)
    })
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('排序/分组字段选择：统一选择器选字段后回填「表.列」串', async () => {
    renderPage()
    await waitFor(() => expect(screen.getAllByRole('combobox').length).toBeGreaterThan(0))
    fireEvent.change(screen.getAllByRole('combobox')[0], { target: { value: '1404' } })
    await waitFor(() => expect(screen.getByText('采购报表')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '排序方案' }))
    await waitFor(() => expect(screen.getAllByRole('button', { name: '选择字段…' }).length).toBeGreaterThan(0))

    // 排序字段：选择「料号」后确认回填 PO.PRO_NO
    fireEvent.click(screen.getAllByRole('button', { name: '选择字段…' })[0])
    await waitFor(() => expect(screen.getByText('料号')).toBeInTheDocument())
    fireEvent.click(screen.getByText('料号').closest('tr')! as HTMLElement)
    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    await waitFor(() => expect(screen.getByDisplayValue('PO.PRO_NO')).toBeInTheDocument())

    // 分组字段：选择「数量」后确认回填 PO.QTY
    fireEvent.click(screen.getAllByRole('button', { name: '选择字段…' })[1])
    await waitFor(() => expect(screen.getByText('数量')).toBeInTheDocument())
    fireEvent.click(screen.getByText('数量').closest('tr')! as HTMLElement)
    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    await waitFor(() => expect(screen.getByDisplayValue('PO.QTY')).toBeInTheDocument())
  })
})
