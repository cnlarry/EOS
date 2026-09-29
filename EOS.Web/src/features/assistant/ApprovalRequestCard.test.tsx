import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApprovalRequestCard } from './ApprovalRequestCard'
import type { AssistantApprovalRequestPreview } from './types'

/** 两行请求卡：一行可执行、一行不可执行——逐行摊开的判别性来自"两行都要看得见"。 */
function requestDraft(overrides: Partial<AssistantApprovalRequestPreview> = {}): AssistantApprovalRequestPreview {
  return {
    kind: 'approval-request-preview',
    moduleId: 1403,
    moduleTitle: '客户询价单',
    action: 'approve',
    blocked: false,
    moduleDenialCode: null,
    moduleDenialMessage: null,
    rows: [
      { keys: ['XJ', 'XJ2609001'], status: '未批核', allowed: true, denialCode: null, denialMessage: null },
      {
        keys: ['XJ', 'XJ2609002'], status: '已结案', allowed: false,
        denialCode: 'FINISHED_EDIT_FORBIDDEN', denialMessage: '记录已结案，禁止编辑（请先取消结案）。',
      },
    ],
    notes: ['确认后由界面直接调既有的批核 / 解批 / 结案 / 取消结案端点，助手侧不持有这四个端点。'],
    ...overrides,
  }
}

interface Call {
  url: string
  method: string
  body: Record<string, unknown> | null
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

/** 按 URL 分派的 fetch 桩：确认留痕与既有的批核族端点各一条。 */
function installFetch(): Call[] {
  const calls: Call[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    calls.push({
      url,
      method: (init?.method ?? 'GET').toUpperCase(),
      body: typeof init?.body === 'string' ? JSON.parse(init.body) as Record<string, unknown> : null,
    })
    if (url.includes('/approval-requests/confirm')) return jsonResponse({ confirmed: 1 })
    return jsonResponse({ ok: true })
  }))
  return calls
}

/** 路由探针：断言"全程不离开助手"——路径变了就看得见。 */
function LocationProbe() {
  const location = useLocation()
  return <div data-testid="path">{location.pathname}</div>
}

function renderCard(draft: AssistantApprovalRequestPreview, path = '/workbench/1403') {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <LocationProbe />
      <ApprovalRequestCard draft={draft} />
    </MemoryRouter>,
  )
}

describe('ApprovalRequestCard', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('逐行摊开：主键、状态与结论都看得见，被拒的行给出具体原因', () => {
    installFetch()
    renderCard(requestDraft())

    expect(screen.getByText('XJ/XJ2609001')).toBeTruthy()
    expect(screen.getByText('XJ/XJ2609002')).toBeTruthy()
    expect(screen.getByText('未批核')).toBeTruthy()
    expect(screen.getByText('已结案')).toBeTruthy()
    expect(screen.getByText('可执行')).toBeTruthy()
    expect(screen.getByText('不可执行：记录已结案，禁止编辑（请先取消结案）。')).toBeTruthy()
  })

  it('逐行勾选：不可执行的行勾不上，可执行的行默认勾上', () => {
    installFetch()
    renderCard(requestDraft())

    const allowed = screen.getByLabelText('选择批核 XJ/XJ2609001') as HTMLInputElement
    const denied = screen.getByLabelText('选择批核 XJ/XJ2609002') as HTMLInputElement
    expect(allowed.checked).toBe(true)
    expect(allowed.disabled).toBe(false)
    expect(denied.checked).toBe(false)
    expect(denied.disabled).toBe(true)
  })

  it('取消勾选的行不会被提交', async () => {
    const calls = installFetch()
    renderCard(requestDraft({
      rows: [
        { keys: ['XJ', 'A1'], status: '未批核', allowed: true, denialCode: null, denialMessage: null },
        { keys: ['XJ', 'A2'], status: '未批核', allowed: true, denialCode: null, denialMessage: null },
      ],
    }))

    fireEvent.click(screen.getByLabelText('选择批核 XJ/A2'))
    fireEvent.click(screen.getByRole('button', { name: '确认批核所选（1 行）' }))

    await waitFor(() => expect(
      calls.filter(call => call.url.includes('/document-workbench/')).length,
    ).toBe(1))
    const approval = calls.find(call => call.url.includes('/document-workbench/'))
    expect(approval?.body?.key).toBe(JSON.stringify(['XJ', 'A1']))
  })

  it('确认后由界面直接调既有端点，逐行提交并带上各自的幂等键', async () => {
    const calls = installFetch()
    renderCard(requestDraft())

    fireEvent.click(screen.getByRole('button', { name: '确认批核所选（1 行）' }))

    await waitFor(() => expect(screen.getByText('已提交批核')).toBeTruthy())
    const approvals = calls.filter(call => call.url.includes('/document-workbench/'))
    expect(approvals).toHaveLength(1)
    expect(approvals[0].url).toContain('/document-workbench/1403/approve')
    expect(approvals[0].method).toBe('POST')
    expect(approvals[0].body?.key).toBe(JSON.stringify(['XJ', 'XJ2609001']))
    expect(String(approvals[0].body?.idempotencyKey ?? '')).not.toBe('')
    // 用户点击单独留一条痕：AI 发起一条、用户确认一条
    expect(calls.some(call => call.url.includes('/approval-requests/confirm'))).toBe(true)
  })

  it('全程不离开助手：确认前后路径不变', async () => {
    installFetch()
    renderCard(requestDraft())

    expect(screen.getByTestId('path').textContent).toBe('/workbench/1403')
    fireEvent.click(screen.getByRole('button', { name: '确认批核所选（1 行）' }))
    await waitFor(() => expect(screen.getByText('已提交批核')).toBeTruthy())
    expect(screen.getByTestId('path').textContent).toBe('/workbench/1403')
  })

  it('整卡被拒时只显示原因，不给确认入口', () => {
    installFetch()
    renderCard(requestDraft({
      blocked: true,
      moduleDenialCode: 'NO_APPROVE_RIGHT',
      moduleDenialMessage: '你没有「批核」权限。',
      rows: [],
    }))

    expect(screen.getByRole('alert').textContent).toBe('你没有「批核」权限。')
    expect(screen.queryByRole('button')).toBeNull()
  })
})
