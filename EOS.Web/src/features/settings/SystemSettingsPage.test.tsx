import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SystemSettingsPage } from './SystemSettingsPage'
import type { SystemParameterGroup, SystemParameterItem, SystemParameterList } from './settingsTypes'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

function parameter(
  key: string,
  valueType: string,
  description: string,
  value: string | null,
  defaultValue: string | null,
  overrides: Partial<SystemParameterItem> = {},
): SystemParameterItem {
  return {
    key,
    value,
    valueType,
    defaultValue,
    groupCode: 'PARTNER',
    groupLabel: '往来与账期',
    description,
    effectScope: 'immediate',
    seqNo: 10,
    options: null,
    effectiveValue: value ?? defaultValue,
    usesDefault: value === null,
    isReferenced: true,
    ...overrides,
  }
}

const groups: SystemParameterGroup[] = [
  {
    groupCode: 'PARTNER',
    groupLabel: '往来与账期',
    parameters: [
      parameter('CLIENT_DAYS', 'int', '客户未交易天数', '30', '1000000'),
      parameter('SUPPLIER_DAYS', 'int', '厂商未交易天数', '100000', '1000000', { isReferenced: false }),
    ],
  },
  {
    groupCode: 'DOC_LINK',
    groupLabel: '单据联动开关',
    parameters: [
      parameter('SEND_TAG', 'bit', '送货单扣库存', '1', null, { groupCode: 'DOC_LINK', groupLabel: '单据联动开关' }),
    ],
  },
  {
    groupCode: 'UI',
    groupLabel: '界面与查询',
    parameters: [
      parameter('LOGIN_F12', 'bit', '用 F12 键登录系统', null, null, {
        groupCode: 'UI',
        groupLabel: '界面与查询',
        effectScope: 'restart',
      }),
    ],
  },
]

const payload: SystemParameterList = { ownerModule: 110111, scope: 'system', groups }

// 考勤侧才有文本型参数（工资字段映射），文本控件单独用这个载荷验证
const hrGroups: SystemParameterGroup[] = [
  {
    groupCode: 'ATT_CARD',
    groupLabel: '考勤卡号解析',
    parameters: [
      parameter('MACHINE_START', 'int', '机号起始位', '1', '0', {
        groupCode: 'ATT_CARD',
        groupLabel: '考勤卡号解析',
      }),
    ],
  },
  {
    groupCode: 'WAGE_MAP',
    groupLabel: '工资字段映射',
    parameters: [
      parameter('WAGE_ADD', 'string', '工资超额字段', 'WAGE_TOTAL', null, {
        groupCode: 'WAGE_MAP',
        groupLabel: '工资字段映射',
      }),
    ],
  },
]

const hrPayload: SystemParameterList = { ownerModule: 180213, scope: 'hr-setup', groups: hrGroups }

function renderPage(path = '/settings/system') {
  return renderWithProviders(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/settings/:table" element={<SystemSettingsPage />} />
      </Routes>
    </MemoryRouter>,
  )
}

describe('SystemSettingsPage（系统参数分组选项卡）', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation(async (url: string) =>
      url.includes('hr-setup') ? hrPayload : payload,
    )
    apiClientMock.put.mockResolvedValue(undefined)
    vi.spyOn(window, 'confirm').mockReturnValue(true)
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('按分组渲染选项卡，并可切换到其它分组', async () => {
    const { container } = renderPage()
    expect(await screen.findByRole('tab', { name: '往来与账期' })).toBeInTheDocument()
    // 页签沿用系统级页签面板（与菜单管理右侧面板、统一表单同一套设计语言）
    expect(container.querySelector('.erp-tabbed-panel-tabs')).not.toBeNull()
    expect(container.querySelector('.erp-tabbed-panel-body')).not.toBeNull()
    expect(screen.getByRole('tab', { name: '单据联动开关' })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: '界面与查询' })).toBeInTheDocument()
    // 默认展示第一个分组
    expect(screen.getByLabelText('客户未交易天数')).toBeInTheDocument()
    expect(screen.queryByLabelText('送货单扣库存')).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('tab', { name: '单据联动开关' }))
    expect(screen.getByLabelText('送货单扣库存')).toBeInTheDocument()
    expect(screen.queryByLabelText('客户未交易天数')).not.toBeInTheDocument()
  })

  it('按参数类型渲染控件：开关→复选框、整数→数字输入', async () => {
    renderPage()
    expect(await screen.findByLabelText('客户未交易天数')).toHaveAttribute('type', 'number')

    fireEvent.click(screen.getByRole('tab', { name: '单据联动开关' }))
    expect(screen.getByLabelText('送货单扣库存')).toHaveAttribute('type', 'checkbox')
    expect(screen.getByLabelText('送货单扣库存')).toBeChecked()
  })

  it('文本参数渲染为文本框并按生效值回填', async () => {
    renderPage('/settings/hr-setup')
    fireEvent.click(await screen.findByRole('tab', { name: '工资字段映射' }))
    expect(screen.getByLabelText('工资超额字段')).toHaveValue('WAGE_TOTAL')
  })

  it('当前无引用方的参数带标记，有引用方的不带', async () => {
    renderPage()
    // 控件与标签同为 .erp-form-field 的子节点，故按字段容器取文本
    const supplier = (await screen.findByLabelText('厂商未交易天数')).closest('.erp-form-field')
    expect(supplier?.textContent).toContain('当前无引用方')
    const client = screen.getByLabelText('客户未交易天数').closest('.erp-form-field')
    expect(client?.textContent).not.toContain('当前无引用方')
  })

  it('需重启生效的参数带标注', async () => {
    renderPage()
    fireEvent.click(await screen.findByRole('tab', { name: '界面与查询' }))
    expect(screen.getByText('需重启')).toBeInTheDocument()
    // 参数键随说明一并展示，便于与配置/日志对照
    expect(screen.getByText('LOGIN_F12')).toBeInTheDocument()
  })

  it('保存提交全部参数并按范围段寻址', async () => {
    renderPage()
    const input = await screen.findByLabelText('客户未交易天数')
    fireEvent.change(input, { target: { value: '45' } })
    fireEvent.click(screen.getByRole('button', { name: /确定/ }))

    await waitFor(() => {
      expect(apiClientMock.put).toHaveBeenCalledWith(
        '/settings/system',
        expect.objectContaining({ CLIENT_DAYS: '45', SEND_TAG: 'true' }),
      )
    })
    expect(await screen.findByRole('alert')).toHaveTextContent('设置成功')
  })

  it('历史路由片段仍可寻址（HR_SETUP → hr-setup）', async () => {
    renderPage('/settings/HR_SETUP')
    await waitFor(() => {
      expect(apiClientMock.get).toHaveBeenCalledWith('/settings/hr-setup')
    })
  })

  it('保存被服务端拒存时显示错误而不是静默失败', async () => {
    apiClientMock.put.mockRejectedValue(new Error('参数 \'CLIENT_DAYS\' 的值不合法：应为整数'))
    renderPage()
    fireEvent.click(await screen.findByRole('button', { name: /确定/ }))
    expect(await screen.findByRole('alert')).toHaveTextContent('设置失败')
  })
})
