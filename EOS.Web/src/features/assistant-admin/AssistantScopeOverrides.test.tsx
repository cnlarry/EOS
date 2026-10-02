import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ToastProvider } from '../../components/ui/Toast'
import { AssistantScopeOverrides } from './AssistantScopeOverrides'
import { listScopes, upsertScope } from './api'

/**
 * 统一选择器是真组件（要起弹窗、拉数据源），这里只关心"选中之后回填了什么"，
 * 所以替换成一个能被测试直接触发的桩：它把来源键透出来（用哪一层的数据源可见），
 * 并按来源返回对应的行形状。
 */
vi.mock('../../components/common/UnifiedChooser', () => ({
  UnifiedChooser: ({ open, source, onPick, onClose }: {
    open: boolean
    source: { key: string }
    onPick: (rows: unknown[]) => void
    onClose: () => void
  }) => {
    if (!open) return null
    return (
      <div role="dialog" aria-label={source.key}>
        <button
          onClick={() => onPick(source.key === 'assistant-admin.modules'
            ? [{ M_IDX: '1401', M_DESC: '客户订单' }]
            : [{ USER_ID: 'zhangsan' }])}
        >
          挑选
        </button>
        <button onClick={onClose}>关闭</button>
      </div>
    )
  },
}))

vi.mock('./api', () => ({
  listScopes: vi.fn(),
  upsertScope: vi.fn(),
  deleteScopeLayer: vi.fn(),
}))

/** 只列**服务端声明过层**的参数：动作族可被模块覆盖，日限额可被用户覆盖。 */
const SCOPABLE = [
  {
    key: 'ACTION_DELETE', displayName: '助手可执行「删除」', valueType: 'bit', unit: null,
    displayNameOfPolicy: '只能收紧（关得掉、放不开）', layers: ['MODULE'], rangeHint: '0 或 1',
  },
  {
    key: 'USER_DAILY_CAP_YUAN', displayName: '每人日上限', valueType: 'decimal', unit: '元',
    displayNameOfPolicy: '可放宽', layers: ['USER'], rangeHint: '大于 0',
  },
]

function renderPanel() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <ToastProvider>
        <AssistantScopeOverrides />
      </ToastProvider>
    </QueryClientProvider>,
  )
}

function optionValues(select: HTMLElement): string[] {
  return Array.from((select as HTMLSelectElement).options).map(option => option.value)
}

describe('AssistantScopeOverrides', () => {
  beforeEach(() => {
    localStorage.clear()
    vi.mocked(listScopes).mockResolvedValue({ items: [], scopable: SCOPABLE })
    vi.mocked(upsertScope).mockResolvedValue(undefined)
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('默认按用户：参数下拉里只有声明过用户层的项', async () => {
    renderPanel()

    await waitFor(() => expect(screen.getByLabelText('参数')).toBeInTheDocument())
    // 模块层参数不该出现在用户层：写进去服务端也会拒，不如根本不显示
    expect(optionValues(screen.getByLabelText('参数'))).toEqual(['', 'USER_DAILY_CAP_YUAN'])
    expect(screen.getByLabelText('目标用户')).toBeInTheDocument()
  })

  it('切到按模块：参数换成模块层的项，且对象走模块数据源', async () => {
    renderPanel()
    await waitFor(() => expect(screen.getByLabelText('参数')).toBeInTheDocument())

    fireEvent.change(screen.getByLabelText('作用域层级'), { target: { value: 'MODULE' } })

    expect(optionValues(screen.getByLabelText('参数'))).toEqual(['', 'ACTION_DELETE'])
    expect(screen.getByLabelText('目标模块')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: '选择' }))
    // 来源键要跟着层走：拿用户源去挑模块是挑不出来的
    expect(screen.getByRole('dialog', { name: 'assistant-admin.modules' })).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: '挑选' }))
    expect(screen.getByLabelText('目标模块')).toHaveValue('1401 客户订单')

    fireEvent.change(screen.getByLabelText('参数'), { target: { value: 'ACTION_DELETE' } })
    fireEvent.change(screen.getByLabelText('覆盖值'), { target: { value: '0' } })
    fireEvent.click(screen.getByRole('button', { name: '保存覆盖' }))

    // 这一条是本用例的重点：层必须跟着界面所选发出去（写死 USER 就会把模块号存成用户名）
    await waitFor(() => expect(vi.mocked(upsertScope)).toHaveBeenCalledWith({
      scopeType: 'MODULE', scopeKey: '1401', paramKey: 'ACTION_DELETE', value: '0',
    }))
  })

  it('切层会清掉已选对象与参数——避免把模块号当用户写进去', async () => {
    renderPanel()
    await waitFor(() => expect(screen.getByLabelText('参数')).toBeInTheDocument())

    fireEvent.change(screen.getByLabelText('作用域层级'), { target: { value: 'MODULE' } })
    fireEvent.click(screen.getByRole('button', { name: '选择' }))
    fireEvent.click(screen.getByRole('button', { name: '挑选' }))
    fireEvent.change(screen.getByLabelText('参数'), { target: { value: 'ACTION_DELETE' } })
    expect(screen.getByRole('button', { name: '保存覆盖' })).toBeEnabled()

    fireEvent.change(screen.getByLabelText('作用域层级'), { target: { value: 'USER' } })

    expect(screen.getByLabelText('目标用户')).toHaveValue('')
    expect(screen.getByLabelText('参数')).toHaveValue('')
    // 对象与参数都空了，保存自然不可点：不会发出一个"用户 1401"的覆盖
    expect(screen.getByRole('button', { name: '保存覆盖' })).toBeDisabled()
  })

  it('这一层没有可设项时给一句话，不摆一个空表单', async () => {
    // 设置页测试用的就是这份数据（scopable 为空）：面板必须能静静渲染，
    // 而不是让人对着几个选不出东西的下拉框猜
    vi.mocked(listScopes).mockResolvedValue({ items: [], scopable: [] })

    renderPanel()

    expect(await screen.findByText(/因此这一层没有可设的项/)).toBeInTheDocument()
    expect(screen.queryByLabelText('参数')).toBeNull()
  })

  it('清单里带出显示名——号是身份，名字才是给人看的', async () => {
    vi.mocked(listScopes).mockResolvedValue({
      items: [
        // 服务端把名字 join 好了；解析不到就是 null（模块被删、账号没登记姓名）
        { scopeType: 'MODULE', scopeKey: '1401', scopeLabel: '客户订单', paramKey: 'ACTION_DELETE', value: '0', updatedBy: 'admin', updatedAt: null },
        { scopeType: 'USER', scopeKey: 'wangwu', scopeLabel: null, paramKey: 'USER_DAILY_CAP_YUAN', value: '8', updatedBy: 'admin', updatedAt: null },
      ],
      scopable: SCOPABLE,
    })

    renderPanel()

    const table = await screen.findByRole('table')
    expect(within(table).getByText('1401')).toBeInTheDocument()
    expect(within(table).getByText('客户订单')).toBeInTheDocument()
    // 解析不到名字时只留号，不留一行空占位或回落成键名的假名字
    expect(within(table).getByText('wangwu')).toBeInTheDocument()
    expect(within(table).queryByText('—')).toBeNull()
  })

  it('清单里要标出层级——同一列里的 1401 可能是模块号也可能是用户名', async () => {
    vi.mocked(listScopes).mockResolvedValue({
      items: [
        { scopeType: 'MODULE', scopeKey: '1401', scopeLabel: '客户订单', paramKey: 'ACTION_DELETE', value: '0', updatedBy: 'admin', updatedAt: null },
        { scopeType: 'USER', scopeKey: 'zhangsan', scopeLabel: '张三', paramKey: 'USER_DAILY_CAP_YUAN', value: '8', updatedBy: 'admin', updatedAt: null },
      ],
      scopable: SCOPABLE,
    })

    renderPanel()

    const table = await screen.findByRole('table')
    expect(within(table).getByText('按模块')).toBeInTheDocument()
    expect(within(table).getByText('按用户')).toBeInTheDocument()
    // 参数显示名来自服务端的 scopable，不是原样吐键名
    expect(within(table).getByText('助手可执行「删除」')).toBeInTheDocument()
  })
})
