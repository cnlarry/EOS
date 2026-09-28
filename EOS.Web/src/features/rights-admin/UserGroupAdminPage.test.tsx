import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { UserGroupAdminPage } from './UserGroupAdminPage'


vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const groups = [
  { groupId: 'CG', groupDescription: '采购', memberCount: 2, remark: '采购组备注' },
  { groupId: 'CW', groupDescription: '财务', memberCount: 0 },
]

function renderPage() {
  return renderWithProviders(
      <MemoryRouter initialEntries={['/admin/groups']}>
        <Routes>
          <Route path="/admin/groups" element={<UserGroupAdminPage />} />
          <Route path="/admin/groups/:groupId/rights" element={<div>GROUP_RIGHTS_PAGE</div>} />

          <Route path="/admin/groups/:groupId/members" element={<div>GROUP_MEMBERS_PAGE</div>} />
        </Routes>
      </MemoryRouter>
)
}

async function loaded() {
  await waitFor(() => expect(screen.getByText('CG')).toBeInTheDocument())
}

function rowOf(groupId: string) {
  return screen.getByText(groupId).closest('tr')!
}

describe('UserGroupAdminPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockResolvedValue(groups)
    apiClientMock.post.mockResolvedValue(undefined)
    apiClientMock.put.mockResolvedValue(undefined)
    apiClientMock.delete.mockResolvedValue(undefined)
    vi.stubGlobal('confirm', vi.fn(() => true))
  })

  afterEach(() => {
    vi.clearAllMocks()
    vi.unstubAllGlobals()
  })

  it('渲染用户组列表（全高铺满 + 首列选择 + 表头排序 + 操作列）', async () => {
    const { container } = renderPage()
    await loaded()
    expect(screen.getByText('采购')).toBeInTheDocument()
    expect(screen.getByText('财务')).toBeInTheDocument()
    expect(container.querySelector('.erp-full-list-page')).not.toBeNull()
    expect(screen.getByLabelText('选择当前页')).toBeInTheDocument()
    expect(screen.getAllByLabelText('选择此行')).toHaveLength(2)
    expect(screen.getByLabelText('表头操作组ID')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '新增' })).toBeInTheDocument()
    // 操作列：组权限/成员/编辑/删除（报表权限矩阵已退场）
    const actions = within(rowOf('CG'))
    for (const name of ['组权限', '成员', '编辑', '删除']) {
      expect(actions.getByRole('button', { name })).toBeInTheDocument()
    }
  })

  it('成员数徽标：0 灰、>0 绿', async () => {
    renderPage()
    await loaded()
    const cwRow = rowOf('CW')
    const cgRow = rowOf('CG')
    expect(within(cwRow).getByText('0').className).toContain('bg-secondary-subtle')
    expect(within(cgRow).getByText('2').className).toContain('bg-success-subtle')
  })

  it('搜索按组ID/组描述过滤', async () => {
    renderPage()
    await loaded()
    fireEvent.change(screen.getByLabelText('搜索用户组'), { target: { value: '财务' } })
    await waitFor(() => expect(screen.queryByText('采购')).not.toBeInTheDocument())
    expect(screen.getByText('财务')).toBeInTheDocument()
  })

  it('新增用户组走弹窗并 POST', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '新增' }))
    expect(screen.getByRole('dialog')).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('组ID'), { target: { value: 'NEWG' } })
    fireEvent.change(screen.getByLabelText('组描述'), { target: { value: '新用户组' } })
    fireEvent.change(screen.getByLabelText('备注'), { target: { value: '备注' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/admin/groups',
      { groupId: 'NEWG', groupDescription: '新用户组', remark: '备注' },
    ))
  })

  it('编辑用户组预填弹窗并 PUT', async () => {
    renderPage()
    await loaded()
    fireEvent.click(within(rowOf('CG')).getByRole('button', { name: '编辑' }))
    const dialog = screen.getByRole('dialog')
    expect(dialog.querySelector('#group-id')).toBeDisabled()
    expect((dialog.querySelector('#group-description') as HTMLInputElement).value).toBe('采购')
    fireEvent.change(screen.getByLabelText('组描述'), { target: { value: '采购部' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith(
      '/admin/groups/CG',
      { groupDescription: '采购部', remark: '采购组备注' },
    ))
  })

  it('双击用户组行等同于点击编辑', async () => {
    renderPage()
    await loaded()
    fireEvent.doubleClick(rowOf('CG'))
    const dialog = screen.getByRole('dialog')
    expect((dialog.querySelector('#group-id') as HTMLInputElement).value).toBe('CG')
    expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument()
  })

  it('删除经确认后调用 DELETE', async () => {
    renderPage()
    await loaded()
    fireEvent.click(within(rowOf('CG')).getByRole('button', { name: '删除' }))
    expect(window.confirm).toHaveBeenCalled()
    await waitFor(() => expect(apiClientMock.delete).toHaveBeenCalledWith('/admin/groups/CG'))
  })

  it('删除取消时不调用接口', async () => {
    vi.mocked(window.confirm).mockReturnValue(false)
    renderPage()
    await loaded()
    fireEvent.click(within(rowOf('CG')).getByRole('button', { name: '删除' }))
    expect(apiClientMock.delete).not.toHaveBeenCalled()
  })

  it('操作列导航到组权限完整子页面', async () => {
    renderPage()
    await loaded()
    fireEvent.click(within(rowOf('CG')).getByRole('button', { name: '组权限' }))
    expect(await screen.findByText('GROUP_RIGHTS_PAGE')).toBeInTheDocument()
  })

  // 报表权限的逐报表矩阵已退场（唯一真源改为归属模块的 REPORT_TAG），这里不再有对应入口。

  it('操作列导航到成员完整子页面', async () => {
    renderPage()
    await loaded()
    fireEvent.click(within(rowOf('CG')).getByRole('button', { name: '成员' }))
    expect(await screen.findByText('GROUP_MEMBERS_PAGE')).toBeInTheDocument()
  })
})

