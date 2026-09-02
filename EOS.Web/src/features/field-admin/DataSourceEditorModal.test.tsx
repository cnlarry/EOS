import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DataSourceEditorModal, type DataSourceDraft } from './DataSourceEditorModal'
import type { ChooserSource } from './FieldEditorForm'


vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

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
  return renderWithProviders(
      <DataSourceEditorModal
        open
        initial={initial}
        currentTable="ORDER_M"
        endpoints={{ load: async () => null, save: async () => undefined }}
        onClose={() => undefined}
        onSave={onSave}
      />
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

  it('高级条件 raw JSON 损坏时确定给出错误反馈且不回调 onSave', () => {
    const onSave = vi.fn()
    renderModal(
      { source, filterRows: [{ key: 'r0', field: '', operator: '', value: '', logic: null, raw: '{"broken' }], returnRows: [] },
      onSave,
    )
    fireEvent.click(screen.getByRole('button', { name: '确定' }))
    expect(screen.getByRole('alert')).toHaveTextContent('JSON 无法解析')
    expect(onSave).not.toHaveBeenCalled()
  })

  it('高级条件行可进入编辑态修改 JSON 并保存更新', async () => {
    const onSave = vi.fn()
    renderModal(
      { source, filterRows: [{ key: 'r0', field: '', operator: '', value: '', logic: null, raw: '{"logic":"AND","items":[]}' }], returnRows: [] },
      onSave,
    )
    // 只读态：先「编辑」进入编辑态
    fireEvent.click(screen.getByRole('button', { name: '编辑' }))
    const editor = screen.getByLabelText('高级条件 JSON') as HTMLTextAreaElement
    expect(editor.value).toBe('{"logic":"AND","items":[]}')
    fireEvent.change(editor, { target: { value: '{"logic":"AND","items":[{"field":"CLIENT.CREDIT_LIMIT","operator":"GT","value":"0"}]}' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    // 保存后回到只读态，确定应携带更新后的 JSON
    fireEvent.click(screen.getByRole('button', { name: '确定' }))
    expect(onSave).toHaveBeenCalled()
    const draft = onSave.mock.calls[0][0] as DataSourceDraft
    expect(draft.filterRows[0].raw).toBe('{"logic":"AND","items":[{"field":"CLIENT.CREDIT_LIMIT","operator":"GT","value":"0"}]}')
  })

  it('高级条件编辑态坏 JSON 保存按钮给出错误反馈', () => {
    const onSave = vi.fn()
    renderModal(
      { source, filterRows: [{ key: 'r0', field: '', operator: '', value: '', logic: null, raw: '{"logic":"AND","items":[]}' }], returnRows: [] },
      onSave,
    )
    fireEvent.click(screen.getByRole('button', { name: '编辑' }))
    const editor = screen.getByLabelText('高级条件 JSON') as HTMLTextAreaElement
    fireEvent.change(editor, { target: { value: '{"broken' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    expect(screen.getByRole('alert')).toHaveTextContent('JSON 无法解析')
    // 仍停留在编辑态，未提交；确定应被阻止
    fireEvent.click(screen.getByRole('button', { name: '确定' }))
    expect(onSave).not.toHaveBeenCalled()
  })

  it('未选择来源列的过滤条件行阻止确定', () => {
    const onSave = vi.fn()
    renderModal({ source, filterRows: [{ key: 'r0', field: '', operator: 'EQ', value: '', logic: null }], returnRows: [] }, onSave)
    fireEvent.click(screen.getByRole('button', { name: '确定' }))
    expect(screen.getByRole('alert')).toHaveTextContent('未选择来源列')
    expect(onSave).not.toHaveBeenCalled()
  })

  it('未配对的回填映射行阻止确定', () => {
    const onSave = vi.fn()
    renderModal({ source, filterRows: [], returnRows: [{ key: 'm0', column: 'CLIENT_ID', target: '' }] }, onSave)
    fireEvent.click(screen.getByRole('button', { name: '确定' }))
    expect(screen.getByRole('alert')).toHaveTextContent('未配对')
    expect(onSave).not.toHaveBeenCalled()
  })

  it('FieldPickerSelect 支持键盘开关（ArrowDown 打开 / Escape 关闭）', async () => {
    renderModal({ source, filterRows: [], returnRows: [] })
    await waitFor(() => expect(apiClientMock.get).toHaveBeenCalledWith('/admin/tables/CLIENT/columns'))
    fireEvent.click(screen.getByRole('button', { name: '+ 条件' }))
    const trigger = screen.getByRole('button', { name: '过滤条件字段' })
    fireEvent.keyDown(trigger, { key: 'ArrowDown' })
    const listbox = screen.getByRole('listbox', { name: '过滤条件字段' })
    expect(listbox).toBeInTheDocument()
    fireEvent.keyDown(listbox, { key: 'Escape' })
    await waitFor(() => expect(screen.queryByRole('listbox', { name: '过滤条件字段' })).not.toBeInTheDocument())
  })
})
