import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AssistantDock } from './AssistantDock'

function sseResponse(chunks: string[]): Response {
  const encoder = new TextEncoder()
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      for (const chunk of chunks) controller.enqueue(encoder.encode(chunk))
      controller.close()
    },
  })
  return new Response(stream, { status: 200 })
}

describe('AssistantDock', () => {
  beforeEach(() => {
    localStorage.clear()
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('默认渲染悬浮球，点击打开抽屉并加载会话列表', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(Response.json([
        { id: '11', userId: 'u1', title: '库存问题', createdAt: '', lastActiveAt: '' },
      ]))
      .mockResolvedValueOnce(Response.json([]))
    vi.stubGlobal('fetch', fetchMock)

    render(<AssistantDock />)
    expect(screen.queryByRole('complementary')).toBeNull()

    fireEvent.click(screen.getByRole('button', { name: '打开工作助手' }))
    await waitFor(() => expect(screen.getByRole('complementary')).not.toBeNull())
    await waitFor(() => expect(screen.getByText('库存问题')).toBeInTheDocument())
  })

  it('Ctrl+/ 快捷键切换抽屉开关，状态写入 localStorage', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(Response.json([])))
    render(<AssistantDock />)

    fireEvent.keyDown(window, { key: '/', ctrlKey: true })
    expect(screen.getByRole('complementary')).not.toBeNull()
    expect(localStorage.getItem('erp-assistant-open')).toBe('true')

    fireEvent.keyDown(window, { key: '/', ctrlKey: true })
    expect(screen.queryByRole('complementary')).toBeNull()
    expect(localStorage.getItem('erp-assistant-open')).toBe('false')
  })

  it('发送消息后流式增量进入助手气泡', async () => {
    localStorage.setItem('erp-assistant-open', 'true')
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(Response.json([{ id: '11', userId: 'u1', title: '会话A', createdAt: '', lastActiveAt: '' }]))
      .mockResolvedValueOnce(Response.json([]))
      .mockResolvedValueOnce(sseResponse([
        'event: delta\ndata: {"text":"你"}\n\n',
        'event: delta\ndata: {"text":"好"}\n\nevent: error\ndata: {"code":"X","message":"中断"}\n\n',
      ]))
    vi.stubGlobal('fetch', fetchMock)

    render(<AssistantDock />)
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2))

    fireEvent.change(screen.getByPlaceholderText(/输入问题/), { target: { value: '在吗' } })
    fireEvent.click(screen.getByRole('button', { name: '发送' }))

    await waitFor(() => expect(screen.getAllByText(/你好|⚠ 中断|在吗/i).length).toBeGreaterThan(0))
    await waitFor(() => expect(screen.getByText(/你好/)).toBeInTheDocument())
  })
})
