import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DataSourceEditorModal, type DataSourceDraft } from './DataSourceEditorModal'
import type { ChooserSource } from './FieldEditorForm'

const apiClientMock = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  postFile: vi.fn(),
}))

vi.mock('../../services/api', () => ({ apiClient: apiClientMock }))

const source: ChooserSource = {
  active: true,
  table: 'CLIENT',
  description: '客户资料',
  moduleId: null,
  filter: null,
  returnMapping: null,
  serialNo: 1,
}

function renderModal(initial: DataSourceDraft | null, onSave = vi.fn()) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <DataSourceEditorModal
        open
        initial={initial}
        currentTable="ORDER_M"
        endpoints={{ load: async () => null, save: async () => undefined }}
        onClose={() => undefined}
        onSave={onSave}
      />
    </QueryClientProvider>,
  )
}

function installMocks() {
  apiClientMock.get.mockImplementation((path: string) => {
    if (path === '/admin/tables/CLIENT/columns') {
      return Promise.resolve([
        { name: 'CLIENT_ID', dataType: 'nvarchar', description: '客户编号' },
        { name: 'CLIENT_NAME', dataType: 'nvarchar', description: '客户名称' },
        { name: 'CREDIT_LIMIT', dataType: 'decimal', description: '信用额度' },
      ])
    }
    if (path === '/admin/tables/ORDER_M/fields') {
      return Promise.resolve({
        items: [
          { fieldId: 'CLIENT_ID', description: '客户', dataType: 'nvarchar' },
          { fieldId: 'CLIENT_NAME', description: '客户名称', dataType: 'nvarchar' },
          { fieldId: 'AMOUNT', description: '金额', dataType: 'decimal' },
        ],
      })
    }
    return Promise.resolve([])
  })
}

describe('DataSourceEditorModal', () => {
  beforeEach(() => {
    installMocks()
  })
  afterEach(() => {
    vi.clearAllMocks()
  })

  it('渲染来源配置、过滤构建器与回填构建器', async () => {
    renderModal({ source, filterRows: [], returnRows: [] })
    expect(screen.getByText('编辑数据源：客户资料')).toBeInTheDocument()
    expect(screen.getByDisplayValue('CLIENT')).toBeInTheDocument()
    expect(screen.getByDisplayValue('客户资料')).toBeInTheDocument()
    expect(screen.getByText('+ 条件')).toBeInTheDocument()
    expect(screen.getByText('+ 回填项')).toBeInTheDocument()
  })

  it('回填目标字段按来源列同类型过滤', async () => {
    const onSave = vi.fn()
    renderModal({ source, filterRows: [], returnRows: [] }, onSave)
    await waitFor(() => expect(apiClientMock.get).toHaveBeenCalledWith('/admin/tables/CLIENT/columns'))
    fireEvent.click(screen.getByRole('button', { name: '+ 回填项' }))
    fireEvent.click(screen.getByRole('button', { name: '回填来源列' }))
    await waitFor(() => expect(screen.getByRole('option', { name: /客户编号/ })).toBeInTheDocument())
    fireEvent.click(screen.getByRole('option', { name: /信用额度/ }))
    // CREDIT_LIMIT 为 decimal：目标字段下拉仅含 AMOUNT（decimal），不含 nvarchar 目标
    fireEvent.click(screen.getByRole('button', { name: '回填目标字段' }))
    expect(screen.getByRole('option', { name: /金额\(AMOUNT\)/ })).toBeInTheDocument()
    expect(screen.queryByRole('option', { name: /客户\(CLIENT_ID\)/ })).not.toBeInTheDocument()
  })

  it('保存回调携带过滤行与回填行', async () => {
    const onSave = vi.fn()
    renderModal({ source, filterRows: [], returnRows: [] }, onSave)
    await waitFor(() => expect(apiClientMock.get).toHaveBeenCalledWith('/admin/tables/CLIENT/columns'))
    fireEvent.click(screen.getByRole('button', { name: '+ 条件' }))
    fireEvent.click(screen.getByRole('button', { name: '+ 回填项' }))
    fireEvent.click(screen.getByRole('button', { name: '过滤条件字段' }))
    await waitFor(() => expect(screen.getByRole('option', { name: /客户编号/ })).toBeInTheDocument())
    fireEvent.click(screen.getByRole('option', { name: /客户编号/ }))
    fireEvent.click(screen.getByRole('button', { name: '回填来源列' }))
    fireEvent.click(screen.getByRole('option', { name: /客户名称/ }))
    fireEvent.click(screen.getByRole('button', { name: '回填目标字段' }))
    fireEvent.click(screen.getByRole('option', { name: /客户名称\(CLIENT_NAME\)/ }))
    fireEvent.click(screen.getByRole('button', { name: '确定' }))
    expect(onSave).toHaveBeenCalled()
    const draft = onSave.mock.calls[0][0] as DataSourceDraft
    expect(draft.source.table).toBe('CLIENT')
    expect(draft.filterRows).toHaveLength(1)
    expect(draft.returnRows).toHaveLength(1)
  })
})
