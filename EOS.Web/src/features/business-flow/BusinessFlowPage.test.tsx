import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter } from 'react-router-dom'
import { useAuth } from '../auth/authContext'
import { renderWithProviders } from '../../test/renderWithProviders'
import { BusinessFlowPage } from './BusinessFlowPage'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))
vi.mock('../auth/authContext', () => ({ useAuth: vi.fn() }))
// 画布是 React Flow 的宿主（要真实尺寸与布局），页面级用例只关心下钻与明细表，故替身掉。
// 替身把节点 id 拼成一行，用来断言"图与表读的是同一批数据"。
vi.mock('./BusinessFlowCanvas', () => ({
  BusinessFlowCanvas: (props: { nodes: { id: string }[] }) => (
    <div data-testid="flow-canvas">{props.nodes.map((node) => node.id).join(',')}</div>
  ),
}))

const bootstrap = {
  user: {
    id: 'admin', username: 'admin', displayName: '系统管理员', employeeId: 'E001',
    avatarText: '管', avatarUrl: null, groupNames: '系统管理员', organization: { id: 'o', name: '总公司' },
  },
  permissions: [] as string[],
  navigation: [],
}

const overview = {
  domains: [
    { rootIdx: 10, name: '基本参数', moduleCount: 12, internalEdgeCount: 3, outEdgeCount: 4, inEdgeCount: 1, relationEdgeCount: 0 },
    { rootIdx: 14, name: '销售管理', moduleCount: 9, internalEdgeCount: 2, outEdgeCount: 1, inEdgeCount: 3, relationEdgeCount: 2 },
  ],
  links: [{ fromRootIdx: 10, toRootIdx: 14, edgeCount: 5, relationEdgeCount: 2 }],
}

const linkGraph = {
  fromRootIdx: 10,
  fromName: '基本参数',
  toRootIdx: 14,
  toName: '销售管理',
  edges: [
    {
      fromModuleId: 1201, toModuleId: 1405, fromModuleName: '产品档案', toModuleName: '销售订单',
      registered: false, relationName: null, fieldCount: 3,
      fields: ['COP_ORDER_D.PRO_ID', 'COP_ORDER_D.PRO_NAME', 'COP_ORDER_D.UNIT'],
      keyPairs: [], tablePairs: ['PRODUCT → COP_ORDER_D'],
    },
    {
      // 同一对模块既是字段取值来源、又是已登记单据关系：明细里只占一行，两个标签并列
      fromModuleId: 1201, toModuleId: 1410, fromModuleName: '产品档案', toModuleName: '客户订单',
      registered: true, relationName: '客户订单定位键（效果）', fieldCount: 2,
      fields: ['COP_ORDER_M.PRO_ID', 'COP_ORDER_M.QTY'],
      keyPairs: ['PRO_ID → PRO_ID'], tablePairs: ['PRODUCT → COP_ORDER_M'],
    },
  ],
}

function renderPage(entry: string) {
  vi.mocked(useAuth).mockReturnValue({
    bootstrap,
    loading: false,
    login: vi.fn(),
    logout: vi.fn(),
    hasPermission: () => true,
  })
  return renderWithProviders(
    <MemoryRouter initialEntries={[entry]}>
      <BusinessFlowPage />
    </MemoryRouter>,
  )
}

/** 明细表（画布替身里没有 table，故这是页面上唯一那张表）。 */
function detailTable() {
  return within(screen.getByRole('table'))
}

describe('BusinessFlowPage 第三级：两个业务域之间的关系', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path === '/admin/business-flow/overview') return overview
      if (path === '/admin/business-flow/domains/10/links/14') return linkGraph
      throw new Error(`unexpected GET ${path}`)
    })
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('带 ?domain=10&peer=14 打开时直接落到关系明细，默认进视图页签，不再请求域内图', async () => {
    renderPage('/admin/business-flow?domain=10&peer=14')

    await waitFor(() => expect(apiClientMock.get).toHaveBeenCalledWith('/admin/business-flow/domains/10/links/14'))

    // 标题写清是哪两个模块；摘要说明"合并后的条数为何比线上两个数字之和少"
    expect(await screen.findByText('基本参数 → 销售管理')).toBeInTheDocument()
    expect(screen.getByText('共 2 条模块关系（其中 1 条同时是已登记单据关系，故比连线上的两个数字之和少） · 上游 1 个模块 · 下游 2 个模块')).toBeInTheDocument()

    // 默认是视图页签：只有画布，没有表格
    expect(screen.getByRole('tab', { name: '视图' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByTestId('flow-canvas')).toHaveTextContent('module:1201,module:1405,module:1410')
    expect(screen.queryByRole('table')).toBeNull()

    // 切到列表页签：两条关系逐条罗列（上游模块、下游模块、关系类型、涉及表对、引用字段、登记键列）
    fireEvent.click(screen.getByRole('tab', { name: '列表' }))
    expect(detailTable().getAllByText('产品档案')).toHaveLength(2)
    expect(detailTable().getByText('销售订单')).toBeInTheDocument()
    expect(detailTable().getByText('PRODUCT → COP_ORDER_D')).toBeInTheDocument()
    // 两条关系都是字段取值来源（第二条同时还登记为单据关系，故多一个标签）
    expect(detailTable().getAllByText('字段取值来源')).toHaveLength(2)
    expect(detailTable().getByText('已登记单据关系')).toBeInTheDocument()
    expect(detailTable().getByText('COP_ORDER_D.PRO_ID、COP_ORDER_D.PRO_NAME、COP_ORDER_D.UNIT')).toBeInTheDocument()
    expect(detailTable().getByText('PRO_ID → PRO_ID')).toBeInTheDocument()

    // 不该顺手把第二级的域内图也拉下来
    expect(apiClientMock.get).not.toHaveBeenCalledWith('/admin/business-flow/domains/10')
  })

  it('两个页签读的是同一批过滤后的数据', async () => {
    renderPage('/admin/business-flow?domain=10&peer=14')
    fireEvent.click(await screen.findByRole('tab', { name: '列表' }))
    await waitFor(() => expect(detailTable().getByText('COP_ORDER_D.PRO_ID、COP_ORDER_D.PRO_NAME、COP_ORDER_D.UNIT')).toBeInTheDocument())

    fireEvent.click(screen.getByRole('button', { name: '只看登记关系' }))

    // 只被字段引用命中的那对模块整行消失；留下来的那条仍是双重身份，两个标签都还在
    await waitFor(() => expect(detailTable().queryByText('COP_ORDER_D.PRO_ID、COP_ORDER_D.PRO_NAME、COP_ORDER_D.UNIT')).toBeNull())
    expect(detailTable().queryByText('销售订单')).toBeNull()
    expect(detailTable().getByText('客户订单')).toBeInTheDocument()
    expect(detailTable().getByText('字段取值来源')).toBeInTheDocument()
    expect(screen.getByText('共 1 条模块关系（其中 1 条同时是已登记单据关系，故比连线上的两个数字之和少） · 上游 1 个模块 · 下游 1 个模块')).toBeInTheDocument()

    // 同一份过滤后的数据在视图页签上也只剩这一条边
    fireEvent.click(screen.getByRole('tab', { name: '视图' }))
    expect(screen.getByTestId('flow-canvas')).toHaveTextContent('module:1201,module:1410')
    expect(screen.getByTestId('flow-canvas')).not.toHaveTextContent('module:1405')
  })

  it('没有关系时两个页签各自给空态而不是空白', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path === '/admin/business-flow/overview') return overview
      if (path === '/admin/business-flow/domains/10/links/14') return { ...linkGraph, edges: [] }
      throw new Error(`unexpected GET ${path}`)
    })
    renderPage('/admin/business-flow?domain=10&peer=14')
    expect(await screen.findByText('这两个业务域之间没有符合当前过滤条件的关系。')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('tab', { name: '列表' }))
    expect(screen.getByText('这两个业务域之间没有关系。')).toBeInTheDocument()
  })
})
