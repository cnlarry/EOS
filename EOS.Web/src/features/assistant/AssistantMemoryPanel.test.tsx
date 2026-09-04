import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { AssistantMemoryPanel } from './AssistantMemoryPanel'

describe('AssistantMemoryPanel', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('加载并展示本人记忆，删除后从列表移除', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(Response.json({
        preferences: '{"theme":"dark"}',
        memories: [{ id: '7', type: 'fact', key: '常用模块', value: '先看送货单', source: 'manual', updatedAt: '' }],
      }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetchMock)

    render(<AssistantMemoryPanel onAskDigest={() => undefined} onClose={() => undefined} />)

    await waitFor(() => expect(screen.getByText('常用模块')).toBeInTheDocument())
    expect(screen.getByText(/先看送货单/)).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: '删除记忆 常用模块' }))
    await waitFor(() => expect(screen.queryByText('常用模块')).toBeNull())
  })

  it('今日摘要按钮回调由调用方承接', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(Response.json({ preferences: null, memories: [] })))
    const onAskDigest = vi.fn()
    render(<AssistantMemoryPanel onAskDigest={onAskDigest} onClose={() => undefined} />)

    await waitFor(() => expect(screen.getByText(/暂无记忆/)).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '今日摘要' }))
    expect(onAskDigest).toHaveBeenCalledTimes(1)
  })
})
