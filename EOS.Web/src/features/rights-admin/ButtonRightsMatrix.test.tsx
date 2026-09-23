import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { apiClientMock } from '../../test/apiMock'
import { renderWithProviders } from '../../test/renderWithProviders'
import { ButtonRightsMatrix, type ButtonRightsRow } from './ButtonRightsMatrix'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const rows: ButtonRightsRow[] = [
  {
    moduleId: 130101,
    moduleTitle: '库存盘点单',
    key: 'recalc-account',
    label: '重算账面数量',
    granted: false,
    hasOverrideRow: false,
    groupGranted: false,
  },
  {
    moduleId: 130101,
    moduleTitle: '库存盘点单',
    key: 'generate-adjustment',
    label: '生成调整单',
    granted: true,
    hasOverrideRow: true,
    groupGranted: false,
  },
]

describe('ButtonRightsMatrix', () => {
  beforeEach(() => {
    apiClientMock.get.mockResolvedValue(rows)
    apiClientMock.put.mockResolvedValue(undefined)
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('默认只显示已授权的按钮（缺省是拒绝，不是全开）', async () => {
    renderWithProviders(
      <ButtonRightsMatrix open mode="group" targetId="CG" onClose={() => {}} />,
    )

    // 用 waitFor 反复取：装载完成后会再渲染一轮（名单态与列定义随之重建），直接持有元素会拿到已卸载的节点。
    await waitFor(() => expect(screen.getByLabelText('允许点击 生成调整单')).toBeInTheDocument())
    expect(screen.queryByLabelText('允许点击 重算账面数量')).not.toBeInTheDocument()

    fireEvent.click(screen.getByLabelText('仅显示已授权'))
    await waitFor(() => expect(screen.getByLabelText('允许点击 重算账面数量')).toBeInTheDocument())
  })

  it('勾选后保存按 (模块, 按钮) 提交名单行', async () => {
    renderWithProviders(
      <ButtonRightsMatrix open mode="group" targetId="CG" onClose={() => {}} />,
    )
    fireEvent.click(await screen.findByLabelText('仅显示已授权'))
    await waitFor(() => expect(screen.getByLabelText('允许点击 重算账面数量')).toBeInTheDocument())

    fireEvent.click(screen.getByLabelText('允许点击 重算账面数量'))
    fireEvent.click(screen.getByRole('button', { name: '保存' }))

    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalled())
    const [url, body] = apiClientMock.put.mock.calls[0]
    expect(url).toBe('/admin/groups/CG/button-rights')
    expect(body.items).toEqual([{ moduleId: 130101, key: 'recalc-account', granted: true }])
  })
})
