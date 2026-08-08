import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../types/api'
import { FormEditorPage } from './FormEditorPage'
import type { FormDefinition } from './formDefinition'

const apiClientMock = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  postFile: vi.fn(),
}))

vi.mock('../../services/api', () => ({ apiClient: apiClientMock }))

function field(key: string, label: string, overrides: Partial<FormDefinition['masterFields'][number]> = {}) {
  return {
    key, label, dataType: 'nvarchar', displayLength: 100, displayFormat: null,
    isRequired: false, verifyIndex: null, regex: null, defaultValue: '', isReadonly: false, isVisible: true,
    onlyChoose: false, chooseMultiple: false, choosePage: null, choosers: [],
    isPrimaryKey: false, isAutoIncrement: false, isVirtual: false, isCost: false, isSecrecy: false,
    serverFilled: false, maxLength: null, ...overrides,
  }
}

const formDefinition: FormDefinition = {
  moduleId: 1209,
  title: '产品版次',
  masterTable: 'PRODUCT_EDITION',
  detailTable: 'PRODUCT_DETAIL',
  hasAdd: true,
  hasEdit: true,
  mode: 'new',
  masterFields: [
    field('PRO_NO', '产品编号', { isRequired: true, isPrimaryKey: true, choosers: [{ active: true, table: 'PRODUCT', description: null, moduleId: null, filter: null, returnMapping: 'txt_PRO_NO=PRO_NO;txt_PRO_NAME=PRO_NAME' }] }),
    field('EDITION', '版次', { isPrimaryKey: true }),
    field('QTY', '数量', { dataType: 'decimal', defaultValue: '5' }),
    field('FLAG', '启用', { dataType: 'bit', defaultValue: '1' }),
    field('CREATE_DATE', '创建时间', { dataType: 'datetime', serverFilled: true, isReadonly: true }),
  ],
  detailFields: [field('ITEM', '明细项', { isRequired: true })],
  masterPkOrder: ['PRO_NO', 'EDITION'],
  detailNoFields: 'PRO_NO',
  detailDfVerify: 'ITEM',
}

const recordBundle = {
  master: { PRO_NO: 'P1', EDITION: 'A', QTY: '10', FLAG: true, CREATE_DATE: '2026-08-01T00:00:00Z' },
  details: [{ ITEM: 'X1' }],
}

const chooserData = {
  columns: [{ key: 'PRO_NO', label: '产品编号' }, { key: 'PRO_NAME', label: '名称' }],
  rows: [{ PRO_NO: 'P9', PRO_NAME: '高强钢' }],
}

function installApiMocks() {
  apiClientMock.get.mockImplementation(async (path: string) => {
    const p = String(path)
    if (p.includes('/form-definition')) return formDefinition
    if (p.includes('/record')) return recordBundle
    if (p.includes('/form-chooser/')) return chooserData
    throw new Error(`unexpected GET ${p}`)
  })
  apiClientMock.post.mockResolvedValue({ key: ['P9', '1'] })
  apiClientMock.put.mockResolvedValue({ key: ['P1', 'A'] })
}

function renderEditor(initialEntry: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const router = createMemoryRouter(
    [
      { path: '/document-workbench/:moduleId', element: <div>BACK_LIST</div> },
      { path: '/document-workbench/:moduleId/new', element: <FormEditorPage /> },
      { path: '/document-workbench/:moduleId/edit', element: <FormEditorPage /> },
    ],
    { initialEntries: [initialEntry] },
  )
  return render(<QueryClientProvider client={queryClient}><RouterProvider router={router} /></QueryClientProvider>)
}

function masterInputs(container: HTMLElement): HTMLInputElement[] {
  return Array.from(container.querySelectorAll<HTMLInputElement>('input.form-control:not([disabled])'))
}

describe('FormEditorPage', () => {
  beforeEach(() => {
    installApiMocks()
    vi.stubGlobal('confirm', vi.fn(() => true))
  })

  afterEach(() => {
    vi.clearAllMocks()
    vi.unstubAllGlobals()
  })

  it('加载中显示 LoadingState', () => {
    apiClientMock.get.mockReturnValue(new Promise(() => undefined))
    renderEditor('/document-workbench/1209/new')
    expect(screen.getByText('正在加载表单…')).toBeInTheDocument()
  })

  it('模块未启用（404）显示明确提示', async () => {
    apiClientMock.get.mockRejectedValue(new ApiError(404, { code: 'NOT_FOUND', message: 'not found' }))
    renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByText('该模块未启用统一表单编辑（含存盘后业务逻辑的模块暂不开放，或不在白名单内）。')).toBeInTheDocument())
  })

  it('新增模式按默认值初始化字段', async () => {
    const { container } = renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByText('新建产品版次')).toBeInTheDocument())
    expect(screen.getByDisplayValue('5')).toBeInTheDocument()
    expect(container.querySelector('input[type="checkbox"]')).toBeChecked()
    expect(screen.getByText('由系统维护')).toBeInTheDocument()
  })

  it('客户端校验拦截必填为空并展示字段错误', async () => {
    renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByText('新建产品版次')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(screen.getByText(/数据校验未通过/)).toBeInTheDocument())
    expect(screen.getByText('该字段不能为空。')).toBeInTheDocument()
    expect(apiClientMock.post).not.toHaveBeenCalled()
  })

  it('新增保存成功后回列表页', async () => {
    const { container } = renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByText('新建产品版次')).toBeInTheDocument())
    fireEvent.change(masterInputs(container)[0], { target: { value: 'P9' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/document-workbench/1209/record',
      { values: { PRO_NO: 'P9', EDITION: '', QTY: '5', FLAG: '1' }, details: [] },
    ))
    await waitFor(() => expect(screen.getByText('BACK_LIST')).toBeInTheDocument())
  })

  it('保存 400 展示服务端字段错误', async () => {
    apiClientMock.post.mockRejectedValue(new ApiError(400, {
      code: 'VALIDATION',
      message: '校验失败',
      fieldErrors: [{ field: 'QTY', message: '数量不能超过库存。', code: 'RANGE' }],
    }))
    const { container } = renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByText('新建产品版次')).toBeInTheDocument())
    fireEvent.change(masterInputs(container)[0], { target: { value: 'P9' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(screen.getByText(/数据校验未通过：数量不能超过库存。/)).toBeInTheDocument())
  })

  it('编辑模式加载记录、保存时携带 original 与主键 key', async () => {
    renderEditor('/document-workbench/1209/edit?key=["P1","A"]')
    await waitFor(() => expect(screen.getByText('编辑产品版次')).toBeInTheDocument())
    expect(screen.getByDisplayValue('P1')).toBeInTheDocument()
    expect(screen.getByDisplayValue('X1')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith(
      expect.stringContaining('/document-workbench/1209/record?key='),
      expect.objectContaining({
        values: expect.objectContaining({ PRO_NO: 'P1', EDITION: 'A', QTY: '10', FLAG: 'true' }),
        original: expect.objectContaining({ PRO_NO: 'P1', EDITION: 'A', QTY: '10', FLAG: 'true' }),
        details: [{ ITEM: 'X1' }],
      }),
    ))
    await waitFor(() => expect(screen.getByText('BACK_LIST')).toBeInTheDocument())
  })

  it('主表必填缺失时拒绝新增明细行', async () => {
    renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByText('新建产品版次')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '新增一行' }))
    await waitFor(() => expect(screen.getByText('请先填写主表字段：PRO_NO，再新增明细。')).toBeInTheDocument())
  })

  it('新增与删除明细行', async () => {
    const { container } = renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByText('新建产品版次')).toBeInTheDocument())
    fireEvent.change(masterInputs(container)[0], { target: { value: 'P9' } })
    fireEvent.click(screen.getByRole('button', { name: '新增一行' }))
    await waitFor(() => expect(screen.getAllByRole('button', { name: '删除' })).toHaveLength(1))
    fireEvent.click(screen.getByRole('button', { name: '删除' }))
    await waitFor(() => expect(screen.queryByRole('button', { name: '删除' })).not.toBeInTheDocument())
  })

  it('选择器按 returnMapping 回填主表字段并置脏', async () => {
    renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByText('新建产品版次')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '选择' }))
    await waitFor(() => expect(screen.getByText('选择 产品编号')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '查询' }))
    await waitFor(() => expect(screen.getByText('高强钢')).toBeInTheDocument())
    fireEvent.click(screen.getByText('高强钢').closest('tr')! as HTMLElement)
    await waitFor(() => expect(screen.getByDisplayValue('P9')).toBeInTheDocument())
    const event = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(event)
    expect(event.defaultPrevented).toBe(true)
  })

  it('修改字段后触发未保存离开提示', async () => {
    const { container } = renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByText('新建产品版次')).toBeInTheDocument())
    fireEvent.change(masterInputs(container)[0], { target: { value: 'X' } })
    const event = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(event)
    expect(event.defaultPrevented).toBe(true)
  })

  it('保存失败展示通用错误', async () => {
    apiClientMock.post.mockRejectedValue(new Error('网络错误'))
    const { container } = renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByText('新建产品版次')).toBeInTheDocument())
    fireEvent.change(masterInputs(container)[0], { target: { value: 'P9' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(screen.getByText('网络错误')).toBeInTheDocument())
  })
})
