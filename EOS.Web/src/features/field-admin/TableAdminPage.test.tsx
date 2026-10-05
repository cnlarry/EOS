import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../types/api'
import { TableAdminPage } from './TableAdminPage'


vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

// 环境负载下（并跑 web dev/API/playwright）弹窗交互用例 5s 默认超时偶发超时，
// 单独跑 10/10 通过；放宽到 15s 消除偶发（同仓库其它表单页测试同此配置风格）。
vi.setConfig({ testTimeout: 15000 })

const tables = Array.from({ length: 20 }, (_, index) => ({
  tableId: `TABLE_${String(index).padStart(2, '0')}`,
  description: `表${index}`,
  kind: index % 2 === 0 ? 'P' : 'S',
  type: 'M',
  fieldCount: 10 + index,
  unmanagedCount: index === 0 ? 3 : 0,
  orphanCount: index === 1 ? 2 : 0,
}))

function renderPage() {
  return renderWithProviders(
      <MemoryRouter initialEntries={['/admin/tables']}>
        <Routes>
          <Route path="/admin/tables" element={<TableAdminPage />} />
          <Route path="/admin/tables/:tableId/fields" element={<div>FIELDS_PAGE</div>} />
        </Routes>
      </MemoryRouter>
)
}

async function loaded() {
  await waitFor(() => expect(screen.queryByText('正在加载数据表…')).not.toBeInTheDocument())
}

describe('TableAdminPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockResolvedValue(tables)
  })

  afterEach(() => {
    vi.clearAllMocks()
    vi.unstubAllGlobals()
  })

  it('加载失败显示错误', async () => {
    apiClientMock.get.mockRejectedValue(new ApiError(500, { code: 'X', message: '表挂了' }))
    renderPage()
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('表挂了'))
  })

  it('渲染数据表并客户端过滤（无分页，一次性加载）', async () => {
    renderPage()
    await loaded()
    expect(screen.getByText('TABLE_00')).toBeInTheDocument()
    expect(screen.queryByText('共 20 条，第 1/2 页')).not.toBeInTheDocument()
    fireEvent.change(screen.getByRole('searchbox', { name: '搜索数据表' }), { target: { value: 'TABLE_19' } })
    await waitFor(() => expect(screen.queryByText('TABLE_00')).not.toBeInTheDocument())
    expect(screen.getByText('TABLE_19')).toBeInTheDocument()
  })

  it('管理字段按钮进入该表的字段维护页', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getAllByRole('button', { name: '管理字段' })[0])
    expect(screen.getByText('FIELDS_PAGE')).toBeInTheDocument()
  })

  it('编辑按钮打开表信息弹窗并提交', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path === '/admin/tables') return tables
      if (path === '/admin/tables/TABLE_00') {
        return {
          tableId: 'TABLE_00', description: '表0', kind: 'P', type: 'M', remark: null,
          fkTable1: null, fkTable2: null, fkTable3: null, fkTable4: null, fkTable5: null,
          queryRelation: 'COMPANY', defaultCondition: null, defaultVerify: null,
          canImport: false, lastUpdatedBy: 'admin', lastUpdatedAt: '2026-08-01T00:00:00Z',
        }
      }
      throw new Error(`unexpected GET ${path}`)
    })
    renderPage()
    await loaded()
    fireEvent.click(screen.getAllByRole('button', { name: /^编辑/ })[0])
    await waitFor(() => expect(screen.getByText('数据表信息（TABLE_00）')).toBeInTheDocument())
    await waitFor(() => expect(screen.getByDisplayValue('表0')).toBeInTheDocument())
    fireEvent.change(screen.getByDisplayValue('表0'), { target: { value: '表0改' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith(
      '/admin/tables/TABLE_00',
      expect.objectContaining({ table: expect.objectContaining({ description: '表0改' }), original: expect.anything() }),
    ))
  })

  it('首列渲染单选列且行点击选中', async () => {
    renderPage()
    await loaded()
    const radios = screen.getAllByRole('radio', { name: '选择此行' })
    expect(radios).toHaveLength(20)
    fireEvent.click(radios[3])
    expect(radios[3]).toBeChecked()
    expect(radios[0]).not.toBeChecked()
  })

  it('按性质筛选后仅显示对应行', async () => {
    renderPage()
    await loaded()
    fireEvent.change(screen.getByRole('combobox', { name: '按性质筛选' }), { target: { value: 'S' } })
    await waitFor(() => expect(screen.getByText('TABLE_01')).toBeInTheDocument())
    expect(screen.queryByText('TABLE_00')).not.toBeInTheDocument()
  })

  it('显示未管理与幽灵字段计数', async () => {
    renderPage()
    await loaded()
    expect(screen.getByText('3')).toBeInTheDocument()
    expect(screen.getByText('2')).toBeInTheDocument()
    expect(document.querySelector('.badge.bg-green-lt')).not.toBeNull()
  })

  it('未管理与幽灵计数带悬停说明，0 与大于 0 文案各自成立', async () => {
    renderPage()
    await loaded()
    const unmanaged = screen.getByText('3')
    const orphan = screen.getByText('2')
    expect(unmanaged.getAttribute('title')).toBe('3 个物理列还没有字段元数据')
    expect(orphan.getAttribute('title')).toBe('2 个字段元数据已找不到对应物理列')

    // 0 值的文案同样只在提示里出现，不占列表宽度
    expect(screen.getAllByText('0').some((badge) => badge.getAttribute('title') === '该表所有物理列都已有字段元数据')).toBe(true)
    expect(screen.getAllByText('0').some((badge) => badge.getAttribute('title') === '没有幽灵字段')).toBe(true)
  })

  it('新增改为选取物理表/视图登记，并按物理结构生成字段元数据', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path === '/admin/tables') return tables
      if (path === '/admin/lookups/physical-tables') return [
        { tableId: 'KB_COLLECTION', objectType: 'U', description: '知识库集合', columnCount: 6 },
        { tableId: 'V_USERS', objectType: 'V', description: '用户视图', columnCount: 5 },
      ]
      throw new Error(`unexpected GET ${path}`)
    })
    apiClientMock.post.mockResolvedValue({
      tableId: 'KB_COLLECTION', description: '知识库集合', kind: 'P', type: 'TABLE',
      fieldCreated: 6, fieldSkipped: 0, skippedReasons: [],
    })
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '新增' }))
    await waitFor(() => expect(screen.getByText('新增数据表元数据（选取物理表或视图）')).toBeInTheDocument())
    await waitFor(() => expect(screen.getByText('KB_COLLECTION')).toBeInTheDocument())
    expect(screen.getByText('V_USERS')).toBeInTheDocument()
    // 未选行时不能登记
    expect(screen.getByRole('button', { name: '登记并生成字段' })).toBeDisabled()

    // 弹窗迭在列表之上，两者都有"选择此行"单选列：必须限定在弹窗内取行
    const dialog = screen.getByRole('dialog')
    fireEvent.click(within(dialog).getAllByRole('radio', { name: '选择此行' })[0])
    expect(screen.getByRole('button', { name: '登记并生成字段' })).toBeEnabled()
    fireEvent.click(screen.getByRole('button', { name: '登记并生成字段' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/admin/tables/from-physical',
      { tableId: 'KB_COLLECTION' },
    ))
    await waitFor(() => expect(screen.getByText(/生成 6 个字段元数据/)).toBeInTheDocument())
  })

  it('删除需要确认并调用删除接口', async () => {
    vi.stubGlobal('confirm', vi.fn(() => true))
    renderPage()
    await loaded()
    fireEvent.click(screen.getAllByRole('button', { name: /^删除/ })[0])
    await waitFor(() => expect(apiClientMock.delete).toHaveBeenCalledWith('/admin/tables/TABLE_00'))
  })

  it('删除取消时不调用接口', async () => {
    vi.stubGlobal('confirm', vi.fn(() => false))
    renderPage()
    await loaded()
    fireEvent.click(screen.getAllByRole('button', { name: /^删除/ })[0])
    expect(apiClientMock.delete).not.toHaveBeenCalled()
  })
})
