import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { MechanismOverviewPage } from './MechanismOverviewPage'

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/admin/assistant/mechanism']}>
        <MechanismOverviewPage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('MechanismOverviewPage', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('列出工具（含风险分级）、可代理动作与能力面边界', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse({
      tools: [
        { name: 'search_records', risk: 'Read', description: '查询模块记录', parametersJson: '{}' },
        { name: 'apply_record_action', risk: 'Write', description: '执行记录动作', parametersJson: '{}' },
      ],
      actions: [{
        name: 'insert',
        actorSubject: '当前登录用户',
        target: '模块定义的主表行',
        parameters: 'AssistantRecordActionArguments.ParametersJson',
        idempotencyKey: '服务端按工具调用身份推导',
        auditAction: 'INSERT（与业务同事务）',
        endpoint: 'DocumentWorkbenchRepository.CreateRecordAsync',
        implementation: 'AssistantRecordActionService.InvokeAsync',
      }],
      boundaries: [{ title: '密钥不入库', detail: '库里只存环境变量名。' }],
    })))

    renderPage()

    // 工具清单与风险分级（风险是判断"这个工具能不能放开"的第一眼信息）
    expect(await screen.findByText('search_records')).toBeInTheDocument()
    expect(screen.getByText('只读')).toBeInTheDocument()
    expect(screen.getByText('写入')).toBeInTheDocument()

    // 动作清单：幂等实现与审计动作这些"能不能安全代理"的关键字段要看得到
    expect(screen.getByText('insert')).toBeInTheDocument()
    expect(screen.getByText('AssistantRecordActionService.InvokeAsync')).toBeInTheDocument()

    // 能力面边界：让"不能做什么"与"能做什么"同样可见
    expect(screen.getByText('密钥不入库')).toBeInTheDocument()
    expect(screen.getByText(/结构上做不到/)).toBeInTheDocument()
  })
})
