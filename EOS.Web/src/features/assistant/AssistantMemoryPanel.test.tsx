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
      .mockResolvedValueOnce(Response.json({ memories: [] }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetchMock)

    render(<AssistantMemoryPanel onAskDigest={() => undefined} onClose={() => undefined} />)

    await waitFor(() => expect(screen.getByText('常用模块')).toBeInTheDocument())
    expect(screen.getByText(/先看送货单/)).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: '删除记忆 常用模块' }))
    await waitFor(() => expect(screen.queryByText('常用模块')).toBeNull())
  })

  it('今日摘要按钮回调由调用方承接', async () => {
    vi.stubGlobal('fetch', vi.fn()
      .mockResolvedValueOnce(Response.json({ preferences: null, memories: [] }))
      .mockResolvedValueOnce(Response.json({ memories: [] })))
    const onAskDigest = vi.fn()
    render(<AssistantMemoryPanel onAskDigest={onAskDigest} onClose={() => undefined} />)

    await waitFor(() => expect(screen.getByText(/暂无记忆/)).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '今日摘要' }))
    expect(onAskDigest).toHaveBeenCalledTimes(1)
  })

  it('待确认记忆可确认，确认后从待确认区移除', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(Response.json({ preferences: null, memories: [] }))
      .mockResolvedValueOnce(Response.json({
        memories: [{ id: '9', type: 'preference', key: '常用模块', value: '先看送货单', confidence: 82 }],
      }))
      .mockResolvedValueOnce(Response.json({ resolved: 'confirmed' }))
      .mockResolvedValueOnce(Response.json({ preferences: null, memories: [] }))
      .mockResolvedValueOnce(Response.json({ memories: [] }))
    vi.stubGlobal('fetch', fetchMock)

    render(<AssistantMemoryPanel onAskDigest={() => undefined} onClose={() => undefined} />)
    await waitFor(() => expect(screen.getByText('常用模块')).toBeInTheDocument())

    fireEvent.click(screen.getByRole('button', { name: '确认记住 常用模块' }))
    await waitFor(() => expect(screen.queryByText('AI 想记住这些')).toBeNull())
  })

  it('忘记我需点两次确认，第二次清空并刷新', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(Response.json({ preferences: null, memories: [] }))
      .mockResolvedValueOnce(Response.json({ memories: [] }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
      .mockResolvedValueOnce(Response.json({ preferences: null, memories: [] }))
      .mockResolvedValueOnce(Response.json({ memories: [] }))
    vi.stubGlobal('fetch', fetchMock)

    render(<AssistantMemoryPanel onAskDigest={() => undefined} onClose={() => undefined} />)
    await waitFor(() => expect(screen.getByText(/暂无记忆/)).toBeInTheDocument())

    fireEvent.click(screen.getByRole('button', { name: /忘记我/ }))
    expect(screen.getByRole('button', { name: /再次点击确认清空/ })).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: /再次点击确认清空/ }))
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(5))
  })
})
