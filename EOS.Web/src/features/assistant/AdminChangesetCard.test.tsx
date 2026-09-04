import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { AdminChangesetCard } from './AdminChangesetCard'
import type { AssistantAdminDraft } from './types'

const draft: AssistantAdminDraft = {
  kind: 'admin-changeset',
  goal: '加一页',
  blocked: false,
  tables: [
    {
      table: 'COP_ORDER_M', action: 'add_fields', status: 'ok',
      wouldCreate: ['TMP_FLAG'], wouldSkip: ['ORDER_NO：已有元数据，跳过'], errors: [],
    },
  ],
  errors: [],
  changeset: { version: 1, goal: '加一页' },
}

describe('AdminChangesetCard', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('试算未通过时不提供确认执行', () => {
    render(<AdminChangesetCard draft={{ ...draft, blocked: true }} />)
    expect(screen.getByText(/试算未通过/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /确认执行/ })).toBeNull()
  })

  it('确认执行调用结构化端点并展示结果', async () => {
    const fetchMock = vi.fn().mockResolvedValue(Response.json({
      goal: '加一页', tablesRegistered: 0, fieldsCreated: 1, fieldsSkipped: 1,
      notes: ['COP_ORDER_M：新增字段 1 个'],
    }))
    vi.stubGlobal('fetch', fetchMock)

    render(<AdminChangesetCard draft={draft} />)
    fireEvent.click(screen.getByRole('button', { name: /确认执行/ }))

    await waitFor(() => expect(screen.getByText(/执行完成/)).toBeInTheDocument())
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    const body = JSON.parse(init.body as string) as { confirmed: boolean }
    expect(body.confirmed).toBe(true)
  })
})
