import { act, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DataChooserInput } from './DataChooserInput'
import type { FormFieldDefinition } from './formDefinition'

const apiClientMock = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  postFile: vi.fn(),
}))

vi.mock('../../services/api', () => ({ apiClient: apiClientMock }))

function field(overrides: Partial<FormFieldDefinition> = {}): FormFieldDefinition {
  return {
    key: 'CLIENT_ID', label: '客户', dataType: 'nvarchar', displayLength: 100, displayFormat: null,
    isRequired: false, verifyIndex: null, regex: null, defaultValue: null, isReadonly: false, isVisible: true,
    onlyChoose: false, chooseMultiple: false, choosePage: null,
    choosers: [{ active: true, table: 'CLIENT', description: null, moduleId: null, filter: null, returnMapping: 'txt_CLIENT_ID=CLIENT_ID' }],
    isPrimaryKey: false, isAutoIncrement: false, isVirtual: false, isCost: false, isSecrecy: false,
    serverFilled: false, maxLength: null, tabNo: 1, formOrder: null, span: 1, newLine: false,
    cellGroup: null, cellRole: 0, options: [], displayOnly: false,
    ...overrides,
  }
}

class FakeIntersectionObserver {
  static instances: FakeIntersectionObserver[] = []
  callback: IntersectionObserverCallback

  constructor(callback: IntersectionObserverCallback) {
    this.callback = callback
    FakeIntersectionObserver.instances.push(this)
  }

  observe = vi.fn()
  unobserve = vi.fn()
  disconnect = vi.fn()

  trigger(entries: IntersectionObserverEntry[]) {
    this.callback(entries, this as unknown as IntersectionObserver)
  }
}

const columns = [{ key: 'CLIENT_ID', label: '客户编号', dataType: 'nvarchar', format: null }]
const page1 = Array.from({ length: 50 }, (_, index) => ({ CLIENT_ID: `C${index}` }))
const page2 = Array.from({ length: 25 }, (_, index) => ({ CLIENT_ID: `C${index + 50}` }))

describe('DataChooserInput', () => {
  beforeEach(() => {
    vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver)
    FakeIntersectionObserver.instances = []
  })

  afterEach(() => {
    vi.clearAllMocks()
    vi.unstubAllGlobals()
  })

  it('初始加载第一页并显示总数', async () => {
    apiClientMock.get.mockResolvedValue({ columns, rows: page1, total: 75 })
    render(<DataChooserInput moduleId="1209" field={field()} onPick={vi.fn()} onClose={vi.fn()} />)
    await waitFor(() => expect(screen.getByText('C0')).toBeInTheDocument())
    expect(screen.getByText(/共 75 条/)).toBeInTheDocument()
    expect(screen.getByText('C49')).toBeInTheDocument()
    expect(screen.queryByText('C50')).not.toBeInTheDocument()
  })

  it('滚动到底自动加载下一页并追加行', async () => {
    apiClientMock.get
      .mockResolvedValueOnce({ columns, rows: page1, total: 75 })
      .mockResolvedValueOnce({ columns, rows: page2, total: 75 })
    render(<DataChooserInput moduleId="1209" field={field()} onPick={vi.fn()} onClose={vi.fn()} />)
    await waitFor(() => expect(screen.getByText('C0')).toBeInTheDocument())
    const observer = FakeIntersectionObserver.instances.at(-1)!
    act(() => observer.trigger([{ isIntersecting: true } as IntersectionObserverEntry]))
    await waitFor(() => expect(screen.getByText('C50')).toBeInTheDocument())
    expect(screen.getByText('C74')).toBeInTheDocument()
    expect(apiClientMock.get).toHaveBeenLastCalledWith(
      expect.stringContaining('/form-chooser/CLIENT_ID'),
      expect.objectContaining({ query: expect.objectContaining({ page: '2' }) }),
    )
  })

  it('已加载全部后不再触发下一页', async () => {
    apiClientMock.get.mockResolvedValue({ columns, rows: page1, total: 50 })
    render(<DataChooserInput moduleId="1209" field={field()} onPick={vi.fn()} onClose={vi.fn()} />)
    await waitFor(() => expect(screen.getByText('C0')).toBeInTheDocument())
    expect(FakeIntersectionObserver.instances).toHaveLength(0)
    expect(screen.getByText('已加载全部')).toBeInTheDocument()
  })
})
