import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { apiClientMock } from '../../test/apiMock'
import { ToastProvider } from '../../components/ui/Toast'
import { useDocumentActionRunner, type DocumentActionMeta } from './documentActionRunner'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const action = (overrides: Partial<DocumentActionMeta> = {}): DocumentActionMeta => ({
  key: 'recalc-account',
  label: '重算账面数量',
  confirmTag: false,
  failMode: 'BLOCK',
  placement: 'detail',
  params: null,
  ...overrides,
})

/**
 * 执行器把参数表单与二次确认作为 `dialog` 返回，由调用页面渲染；
 * 这里用一个夹具组件承载它，并把 run 暴露出来供用例驱动。
 */
function setup(options: Partial<Parameters<typeof useDocumentActionRunner>[0]> = {}) {
  const onRefreshed = vi.fn(async () => {})
  const onNavigate = vi.fn()
  const props = { moduleId: '130101', keyValues: ['PD001'], dirty: false, onRefreshed, onNavigate, ...options }
  const handle: { run: (action: DocumentActionMeta) => Promise<void> } = { run: async () => {} }
  function Harness() {
    const runner = useDocumentActionRunner(props)
    handle.run = runner.run
    return <>{runner.dialog}</>
  }
  render(<ToastProvider><Harness /></ToastProvider>)
  return { handle, onRefreshed, onNavigate }
}

describe('useDocumentActionRunner', () => {
  beforeEach(() => {
    apiClientMock.post.mockResolvedValue({ outcome: 'refreshed', message: '已重算' })
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('无二次确认的操作直接执行：幂等键走请求头，refreshed 后刷新单据', async () => {
    const { handle, onRefreshed } = setup()

    await act(async () => { await handle.run(action()) })

    expect(apiClientMock.post).toHaveBeenCalledTimes(1)
    const [url, body, init] = apiClientMock.post.mock.calls[0]
    expect(url).toBe('/document-workbench/130101/action/recalc-account')
    expect(body).toMatchObject({ key: ['PD001'], confirm: true })
    expect(String(init.headers['X-Idempotency-Key']).length).toBeGreaterThan(0)
    expect(onRefreshed).toHaveBeenCalled()
  })

  it('CONFIRM_TAG 的操作先探路再执行，两次用不同的幂等键（真执行那次不会被探路占掉）', async () => {
    apiClientMock.post.mockImplementation(async (_url: string, body: { confirm?: boolean }) =>
      body?.confirm
        ? { outcome: 'refreshed', message: '已重算' }
        : { outcome: 'message', message: '将重算 3 行账面数量', requiresConfirmation: true },
    )
    const { handle, onRefreshed } = setup()

    await act(async () => { await handle.run(action({ confirmTag: true })) })

    expect(apiClientMock.post).toHaveBeenCalledTimes(1)
    expect(apiClientMock.post.mock.calls[0][1]).toMatchObject({ confirm: false })
    // 探路只是"先算影响"：把将发生什么告诉用户，此时不刷新、也不写库
    expect(await screen.findByText('将重算 3 行账面数量')).toBeInTheDocument()
    expect(onRefreshed).not.toHaveBeenCalled()

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: '确定' }))
    })

    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledTimes(2))
    expect(apiClientMock.post.mock.calls[1][1]).toMatchObject({ confirm: true })
    const probeKey = apiClientMock.post.mock.calls[0][2].headers['X-Idempotency-Key']
    const runKey = apiClientMock.post.mock.calls[1][2].headers['X-Idempotency-Key']
    expect(runKey).not.toBe(probeKey)
    expect(onRefreshed).toHaveBeenCalled()
  })

  /**
   * ADR-028 §2.2 的"一次用户操作一个关联键"：探路与真执行是同一次点击引发的两个请求，
   * 必须共用同一个 `X-Correlation-Id`——否则用户报障给的那个编号只能对上两次请求里的一次，
   * 排障时看不到"先算了什么、再执行了什么"的完整链路。
   */
  it('同一次点击的探路与执行共用同一个关联键（幂等键仍各自独立）', async () => {
    apiClientMock.post.mockImplementation(async (_url: string, body: { confirm?: boolean }) =>
      body?.confirm
        ? { outcome: 'refreshed', message: '已重算' }
        : { outcome: 'message', message: '将重算 3 行账面数量', requiresConfirmation: true },
    )
    const { handle } = setup()

    await act(async () => { await handle.run(action({ confirmTag: true })) })
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: '确定' }))
    })
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledTimes(2))

    const probeCorrelation = apiClientMock.post.mock.calls[0][2].headers['X-Correlation-Id']
    const runCorrelation = apiClientMock.post.mock.calls[1][2].headers['X-Correlation-Id']
    expect(String(probeCorrelation).length).toBeGreaterThan(0)
    expect(runCorrelation).toBe(probeCorrelation)
  })

  /** 两次不同的点击应当是两个不同的键，否则"这一次点击"会被上一次的记录混进来。 */
  it('两次独立的点击用不同的关联键', async () => {
    const { handle } = setup()

    await act(async () => { await handle.run(action()) })
    await act(async () => { await handle.run(action()) })

    expect(apiClientMock.post).toHaveBeenCalledTimes(2)
    const first = apiClientMock.post.mock.calls[0][2].headers['X-Correlation-Id']
    const second = apiClientMock.post.mock.calls[1][2].headers['X-Correlation-Id']
    expect(second).not.toBe(first)
  })

  it('界面有未保存改动时不发起请求，并提示先保存', async () => {
    const { handle } = setup({ dirty: true })

    await act(async () => { await handle.run(action()) })

    expect(apiClientMock.post).not.toHaveBeenCalled()
    expect(await screen.findByText(/请先保存/)).toBeInTheDocument()
  })

  it('没有单据主键（新增态）时不发起请求', async () => {
    const { handle } = setup({ keyValues: null })

    await act(async () => { await handle.run(action()) })

    expect(apiClientMock.post).not.toHaveBeenCalled()
    expect(await screen.findByText(/先保存并打开一张单据/)).toBeInTheDocument()
  })

  it('声明了参数的操作：先填表，必填缺失不发请求，填好后随请求下发', async () => {
    const { handle } = setup()
    const withParams = action({
      params: { fields: [{ key: 'relocateTo', label: '目标库位', type: 'string', required: true, maxLength: 20 }] },
    })

    await act(async () => { await handle.run(withParams) })
    expect(apiClientMock.post).not.toHaveBeenCalled()

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: '确定' }))
    })
    expect(apiClientMock.post).not.toHaveBeenCalled()
    expect(await screen.findByText(/请填写：目标库位/)).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText(/目标库位/), { target: { value: 'A-R1' } })
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: '确定' }))
    })

    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledTimes(1))
    expect(apiClientMock.post.mock.calls[0][1]).toMatchObject({ params: { relocateTo: 'A-R1' } })
  })

  it('navigated 结果把用户带到生成出来的下游单据', async () => {
    apiClientMock.post.mockResolvedValue({
      outcome: 'navigated',
      message: '已生成调整单',
      targetModuleId: 130107,
      targetKey: ['ADJ001'],
    })
    const { handle, onNavigate, onRefreshed } = setup()

    await act(async () => { await handle.run(action({ key: 'generate-adjustment', label: '生成调整单' })) })

    expect(onNavigate).toHaveBeenCalledWith(130107, ['ADJ001'])
    expect(onRefreshed).not.toHaveBeenCalled()
  })

  it('warnings 照常提示，不让用户以为一切顺利', async () => {
    apiClientMock.post.mockResolvedValue({
      outcome: 'message',
      message: '已执行（有告警）',
      warnings: [{ code: 'ACTION_WARNING', message: '部分明细未取到最新库存' }],
    })
    const { handle } = setup()

    await act(async () => { await handle.run(action()) })

    expect(await screen.findByText(/部分明细未取到最新库存/)).toBeInTheDocument()
  })
})
