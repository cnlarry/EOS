import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ActionCard } from './ActionCard'
import type { FormDefinition } from '../document-workbench/formDefinition'
import type { AssistantRecordActionPreview } from './types'

function field(key: string, label: string, overrides: Partial<FormDefinition['masterFields'][number]> = {}) {
  return {
    key, label, dataType: 'nvarchar', displayLength: 100, displayFormat: null,
    isRequired: false, verifyIndex: null, regex: null, defaultValue: '', isReadonly: false, isVisible: true,
    onlyChoose: false, chooseMultiple: false, choosePage: null, choosers: [],
    isPrimaryKey: false, isAutoIncrement: false, isVirtual: false, isCost: false, isSecrecy: false,
    serverFilled: false, maxLength: null,
    tabNo: 1, formOrder: null, span: 1, newLine: false, cellGroup: null, cellRole: 0, options: [], displayOnly: false, canCopy: true,
    ...overrides,
  }
}

function formDefinition(overrides: Partial<FormDefinition> = {}): FormDefinition {
  return {
    moduleId: 1403,
    title: '客户询价单',
    masterTable: 'COP_CHAFFER_M',
    detailTable: null,
    hasAdd: true,
    hasEdit: true,
    mode: 'new',
    ifCopy: true,
    searchMaster: false,
    searchDetail: false,
    masterFields: [
      field('CHAFFER_TYPE', '询价单别', { isPrimaryKey: true }),
      field('CHAFFER_NO', '询价单号', { isPrimaryKey: true, isAutoIncrement: true }),
      field('REMARK', '备注'),
      field('CLIENT_ID', '客户', {
        isReadonly: true,
        choosers: [{ active: true, table: 'CLIENT', description: '客户主档', moduleId: null, filter: null, returnMapping: '[{"target":"CLIENT_ID","column":"CLIENT_ID"},{"target":"CLIENT_NAME","column":"CLIENT_NAME"}]', serialNo: 1 }],
      }),
      field('CREATE_DATE', '创建时间', { dataType: 'datetime', serverFilled: true, isReadonly: true }),
    ],
    detailFields: [],
    masterPkOrder: ['CHAFFER_TYPE', 'CHAFFER_NO'],
    detailNoFields: '',
    detailDfVerify: '',
    tabs: [],
    hasWorkflow: false,
    hasStatelessApprove: false,
    defaultValues: {},
    canDelete: true,
    canApprove: false,
    canDeapprove: false,
    canEndCase: false,
    canUnEndCase: false,
    canAddNew: true,
    canEdit: true,
    canFileView: false,
    canFileUpda: false,
    canFileEdit: false,
    canFileDele: false,
    canSetup: false,
    canFormDesign: false,
    ...overrides,
  }
}

/** 两行预演：一行可执行、一行被拒（逐行摊开的判别性来自"两行都要看得见"）。 */
function previewDraft(overrides: Partial<AssistantRecordActionPreview> = {}): AssistantRecordActionPreview {
  return {
    kind: 'record-action-preview',
    moduleId: 1403,
    moduleTitle: '客户询价单',
    action: 'insert',
    blocked: false,
    moduleDenialCode: null,
    moduleDenialMessage: null,
    rows: [
      { keys: [], values: { CHAFFER_TYPE: 'XJ', REMARK: '照抄上一单' }, allowed: true, denialCode: null, denialMessage: null, impacts: null },
      { keys: [], values: { CHAFFER_TYPE: 'XJ', REMARK: '第二行' }, allowed: false, denialCode: 'ADD_CAPABILITY_MISSING', denialMessage: '你没有这个模块的新增权限。', impacts: null },
    ],
    notes: ['预演在同一事务内跑完整条路径后无条件回滚，与真实执行共用同一段代码。'],
    ...overrides,
  }
}

interface Call {
  url: string
  method: string
  body: Record<string, unknown> | null
  headers: Record<string, string>
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

/** 按 URL 分派的 fetch 桩：form-definition / preview / apply 各一条。 */
function installFetch(handlers: {
  definition?: () => Response
  preview?: () => Response
  apply?: () => Response
  chooser?: () => Response
} = {}): Call[] {
  const calls: Call[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    const method = (init?.method ?? 'GET').toUpperCase()
    calls.push({
      url,
      method,
      body: typeof init?.body === 'string' ? JSON.parse(init.body) as Record<string, unknown> : null,
      headers: (init?.headers ?? {}) as Record<string, string>,
    })
    if (url.includes('/form-definition')) return (handlers.definition ?? (() => jsonResponse(formDefinition())))()
    // 统一选择器：formField 数据源经 form-chooser 端点取数（列与默认列由服务端给）
    if (url.includes('/form-chooser/')) {
      return (handlers.chooser ?? (() => jsonResponse({
        columns: [{ key: 'CLIENT_ID', label: '客户编号' }, { key: 'CLIENT_NAME', label: '客户名称' }],
        rows: [{ CLIENT_ID: 'C9', CLIENT_NAME: '高强钢客户' }],
        total: 1,
      })))()
    }
    if (url.includes('/record-actions/preview')) {
      return (handlers.preview ?? (() => jsonResponse(previewDraft())))()
    }
    if (url.includes('/record-actions/apply')) {
      return (handlers.apply ?? (() => jsonResponse({
        kind: 'record-action-result',
        moduleId: 1403,
        moduleTitle: '客户询价单',
        action: 'insert',
        moduleDenialCode: null,
        moduleDenialMessage: null,
        rows: [
          { keys: ['XJ', 'XJ2609001'], succeeded: true, code: null, message: null, resultKeys: ['XJ', 'XJ2609001'], idempotencyKey: 'k1' },
          { keys: ['XJ', 'XJ2609002'], succeeded: false, code: 'VALIDATION_FAILED', message: '备注必填。', resultKeys: null, idempotencyKey: 'k2' },
        ],
      })))()
    }
    return jsonResponse({})
  }))
  return calls
}

/** 路由探针：断言"全程不离开助手"——路径变了就看得见。 */
function LocationProbe() {
  const location = useLocation()
  return <div data-testid="path">{location.pathname}</div>
}

function renderCard(draft: AssistantRecordActionPreview, path = '/workbench/1403') {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <LocationProbe />
      <ActionCard draft={draft} />
    </MemoryRouter>,
  )
}

describe('ActionCard', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('逐行摊开：多行时逐行主键与结论都看得见，被拒的行给出具体原因', async () => {
    installFetch()
    renderCard(previewDraft())

    // 逐行：两行各自有主键位（新单没有主键，用"（新单）"占位）与结论
    await waitFor(() => expect(screen.getAllByText('（新单）')).toHaveLength(2))
    expect(screen.getByText('可执行')).toBeInTheDocument()
    expect(screen.getByText(/不可执行：你没有这个模块的新增权限。/)).toBeInTheDocument()
  })

  it('字段元数据来自服务端：按 mode=new 取表单定义，可编辑字段由定义渲染', async () => {
    const calls = installFetch()
    renderCard(previewDraft())

    const definitionCall = await waitFor(() => {
      const call = calls.find(item => item.url.includes('/form-definition'))
      expect(call).toBeTruthy()
      return call!
    })
    expect(definitionCall.url).toContain('/document-workbench/1403/form-definition')
    expect(definitionCall.url).toContain('mode=new')

    // 定义里的可写字段渲染成输入；主键自增列与服务端维护列不渲染输入
    await waitFor(() => expect(screen.getByLabelText('备注（第 1 行）')).toBeInTheDocument())
    expect(screen.queryByLabelText('询价单号（第 1 行）')).toBeNull()
    expect(screen.queryByLabelText('创建时间（第 1 行）')).toBeNull()
  })

  it('就地改值后重算预演：改过的值随请求发回服务端', async () => {
    const calls = installFetch()
    renderCard(previewDraft())

    const input = await screen.findByLabelText('备注（第 1 行）')
    fireEvent.change(input, { target: { value: '改过的备注' } })
    fireEvent.click(screen.getByRole('button', { name: '重新预演' }))

    await waitFor(() => expect(calls.some(call => call.url.includes('/record-actions/preview'))).toBe(true))
    const call = calls.find(item => item.url.includes('/record-actions/preview'))!
    expect(call.body).toMatchObject({ module_id: 1403, action: 'insert' })
    const rows = call.body?.rows as Array<{ values: Record<string, string> }>
    expect(rows[0].values.REMARK).toBe('改过的备注')
    // 全程没有离开助手：路径仍是打开卡片时的那一个
    expect(screen.getByTestId('path').textContent).toBe('/workbench/1403')
  })

  it('确认执行：带上本次确认的幂等键，并把结果回读出来', async () => {
    const calls = installFetch()
    renderCard(previewDraft())

    const apply = await screen.findByRole('button', { name: '确认执行' })
    fireEvent.click(apply)

    await waitFor(() => expect(calls.some(call => call.url.includes('/record-actions/apply'))).toBe(true))
    const call = calls.find(item => item.url.includes('/record-actions/apply'))!
    expect(call.method).toBe('POST')
    // 幂等键随本次确认给出（服务端据此去重），模型可见的参数 schema 里没有它
    expect(call.headers['X-Idempotency-Key']).toBeTruthy()
    expect(call.body).toMatchObject({ module_id: 1403, action: 'insert' })

    // 结果回读：成功的主键与失败的原因都看得到
    await waitFor(() => expect(screen.getByText(/已保存：XJ\/XJ2609001/)).toBeInTheDocument())
    expect(screen.getByText(/未保存：备注必填。/)).toBeInTheDocument()
    expect(screen.getByTestId('path').textContent).toBe('/workbench/1403')
  })

  it('重复点确认用的是同一个幂等键（同一张卡 = 同一次用户意图）', async () => {
    const calls = installFetch({ apply: () => jsonResponse({ code: 'BOOM', message: '执行失败。' }, 500) })
    renderCard(previewDraft())

    fireEvent.click(await screen.findByRole('button', { name: '确认执行' }))
    await waitFor(() => expect(screen.getByRole('alert')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '确认执行' }))
    await waitFor(() => expect(calls.filter(call => call.url.includes('/record-actions/apply'))).toHaveLength(2))

    const keys = calls.filter(call => call.url.includes('/record-actions/apply')).map(call => call.headers['X-Idempotency-Key'])
    expect(keys[0]).toBe(keys[1])
  })

  it('删除：默认不勾选、级联影响面可见，未勾选不可执行', async () => {
    const calls = installFetch()
    renderCard(previewDraft({
      action: 'delete',
      rows: [
        {
          keys: ['XJ', 'XJ2609001'], values: null, allowed: true, denialCode: null, denialMessage: null,
          impacts: [{ effectKey: 'inventory-move', eventCode: 'APPROVE_EFFECT', effectName: '库存移动', targetTable: 'INV_PRO_DEPOT', targetField: 'QTY', opCode: 'ACCUM' }],
        },
        { keys: ['XJ', 'XJ2609002'], values: null, allowed: true, denialCode: null, denialMessage: null, impacts: [] },
      ],
    }))

    const boxes = await screen.findAllByRole('checkbox')
    expect(boxes).toHaveLength(2)
    expect(boxes.every(box => !(box as HTMLInputElement).checked)).toBe(true)

    // 影响面必须看得见（"这张单带着哪些业务副作用"）
    expect(screen.getByText(/INV_PRO_DEPOT\.QTY \[ACCUM\]/)).toBeInTheDocument()

    // 未勾选 ⇒ 不可执行
    expect(screen.getByRole('button', { name: /确认删除所选/ })).toBeDisabled()

    fireEvent.click(boxes[1])
    const confirm = screen.getByRole('button', { name: /确认删除所选（1 行）/ })
    expect(confirm).not.toBeDisabled()
    fireEvent.click(confirm)

    await waitFor(() => expect(calls.some(call => call.url.includes('/record-actions/apply'))).toBe(true))
    const call = calls.find(item => item.url.includes('/record-actions/apply'))!
    expect(call.body).toMatchObject({ action: 'delete' })
    const rows = call.body?.rows as Array<{ keys: string[] }>
    expect(rows).toHaveLength(1)
    expect(rows[0].keys).toEqual(['XJ', 'XJ2609002'])
  })

  it('引用数据走统一选择器：按 formField 元数据取数并回填映射（键名大小写不敏感）', async () => {
    const calls = installFetch()
    renderCard(previewDraft())

    fireEvent.click(await screen.findByRole('button', { name: '选择 客户（第 1 行）' }))

    // 走的是统一选择器 + 服务端 form-chooser 端点（没有自建选择弹窗）
    const chooserCall = await waitFor(() => {
      const call = calls.find(item => item.url.includes('/form-chooser/CLIENT_ID'))
      expect(call).toBeTruthy()
      return call!
    })
    expect(chooserCall.url).toContain('/document-workbench/1403/form-chooser/CLIENT_ID')

    // 选中后按 RETURN_ITEMS 回填（映射里的列名大小写与结果行不一致也要取到）
    fireEvent.click(await screen.findByRole('radio'))
    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    await waitFor(() => expect((screen.getByLabelText('客户（第 1 行）') as HTMLInputElement).value).toBe('C9'))
  })

  it('模块级不可用：明说原因，且不提供执行入口', () => {
    installFetch()
    renderCard(previewDraft({
      blocked: true,
      moduleDenialCode: 'ADD_CAPABILITY_MISSING',
      moduleDenialMessage: '你没有这个模块的新增权限。',
      rows: [],
    }))

    expect(screen.getByRole('alert').textContent).toContain('你没有这个模块的新增权限。')
    expect(screen.queryByRole('button', { name: '确认执行' })).toBeNull()
  })

  it('不可执行的行：不提供输入与勾选（判定以服务端结论为准，前端不替它放行）', async () => {
    installFetch()
    renderCard(previewDraft({
      action: 'delete',
      rows: [
        { keys: ['XJ', 'XJ2609001'], values: null, allowed: true, denialCode: null, denialMessage: null, impacts: [] },
        { keys: ['XJ', 'XJ2609002'], values: null, allowed: false, denialCode: 'RECORD_OUT_OF_SCOPE', denialMessage: '目标记录不在当前用户数据范围内。', impacts: [] },
      ],
    }))

    const deniedBox = await screen.findByLabelText('选择删除 XJ/XJ2609002')
    expect(deniedBox).toBeDisabled()
    const deniedRow = screen.getByText('XJ/XJ2609002').closest('.erp-assistant-action-row') as HTMLElement
    expect(within(deniedRow).getByText(/不可执行：目标记录不在当前用户数据范围内。/)).toBeInTheDocument()
    // 可执行的行照常能勾
    expect(screen.getByLabelText('选择删除 XJ/XJ2609001')).not.toBeDisabled()
  })
})
