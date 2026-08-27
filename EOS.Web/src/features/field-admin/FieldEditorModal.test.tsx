import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { FieldEditorModal, type FieldEditorEndpoints, type FieldMeta } from './FieldEditorModal'

function meta(overrides: Partial<FieldMeta> = {}): FieldMeta {
  return {
    key: 'CODE', tableId: 'T1', label: '编号', dataType: 'nvarchar', width: 120, align: 'left', headerAlign: 'center',
    format: null, isVisible: true, isDefault: true, isQueryable: true, isReadonly: false, isRequired: true,
    isCost: false, isSecrecy: false, defaultValue: null, verifyIndex: null, regex: null, remark: null,
    browseUrl: null, browseModuleId: null, onlyChoose: false, chooseMultiple: false, choosePage: null,
    choosers: [
      { active: false, table: null, description: null, moduleId: null, filter: null, returnMapping: null },
      { active: false, table: null, description: null, moduleId: null, filter: null, returnMapping: null },
      { active: false, table: null, description: null, moduleId: null, filter: null, returnMapping: null },
      { active: false, table: null, description: null, moduleId: null, filter: null, returnMapping: null },
    ],
    isVirtual: false, virtualExpression: null, canCopy: true, isAutoIncrement: false,
    convertFunction: null, dataSourceSql: null, lastUpdatedBy: 'admin', lastUpdatedAt: '2026-08-01T00:00:00Z',
    tabNo: 1, formOrder: null, span: 1, newLine: false, cellGroup: null, cellRole: 0, options: null,
    ...overrides,
  }
}

function renderModal(open: boolean, mode: 'new' | 'edit', endpoints: FieldEditorEndpoints, onSaved = vi.fn(), onClose = vi.fn()) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return {
    ...render(
      <QueryClientProvider client={queryClient}>
        <FieldEditorModal open={open} mode={mode} tableId="T1" fieldKey="CODE" endpoints={endpoints} onClose={onClose} onSaved={onSaved} />
      </QueryClientProvider>,
    ),
    onSaved,
    onClose,
  }
}

describe('FieldEditorModal', () => {
  afterEach(() => {
    vi.clearAllMocks()
  })

  it('open=false 时不渲染', () => {
    const { container } = renderModal(false, 'edit', { load: vi.fn(), save: vi.fn() })
    expect(container).toBeEmptyDOMElement()
  })

  it('新增模式渲染空草稿且必填校验禁用保存', async () => {
    const save = vi.fn().mockResolvedValue(undefined)
    const { container, onSaved } = renderModal(true, 'new', { load: vi.fn(), save })
    const saveButton = screen.getByRole('button', { name: '保存' })
    expect(saveButton).toBeDisabled()
    const inputs = Array.from(container.querySelectorAll<HTMLInputElement>('input.form-control'))
    fireEvent.change(inputs[0], { target: { value: 'T2' } })
    fireEvent.change(inputs[1], { target: { value: 'CODE2' } })
    fireEvent.change(inputs[2], { target: { value: '新字段' } })
    fireEvent.change(container.querySelector<HTMLSelectElement>('select.form-select')!, { target: { value: 'decimal' } })
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeEnabled())
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(save).toHaveBeenCalled())
    expect(save.mock.calls[0][1]).toBe('T2')
    expect(save.mock.calls[0][3]).toBeNull()
    expect(onSaved).toHaveBeenCalled()
  })

  it('编辑模式加载元数据并保存（含 original）', async () => {
    const loaded = meta()
    const save = vi.fn().mockResolvedValue(undefined)
    const { container, onSaved } = renderModal(true, 'edit', { load: vi.fn().mockResolvedValue(loaded), save })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    const inputs = Array.from(container.querySelectorAll<HTMLInputElement>('input.form-control'))
    fireEvent.change(inputs[2], { target: { value: '编号2' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(save).toHaveBeenCalled())
    const [input, tableId, fieldId, original] = save.mock.calls[0]
    expect(tableId).toBe('T1')
    expect(fieldId).toBe('CODE')
    expect(input.label).toBe('编号2')
    expect(original).toEqual(expect.objectContaining({ label: '编号' }))
    expect(original).not.toBeNull()
    expect(onSaved).toHaveBeenCalled()
  })

  it('编辑加载失败显示错误提示', async () => {
    renderModal(true, 'edit', { load: vi.fn().mockRejectedValue(new Error('boom')), save: vi.fn() })
    await waitFor(() => expect(screen.getByText('无法加载该字段的元数据，请确认当前账号具有字段设置权限。')).toBeInTheDocument())
  })

  it('编辑加载中显示 LoadingState', () => {
    renderModal(true, 'edit', { load: vi.fn().mockReturnValue(new Promise(() => undefined)), save: vi.fn() })
    expect(screen.getByText('正在加载字段元数据…')).toBeInTheDocument()
  })

  it('分区切换展示对应控件', async () => {
    const { container } = renderModal(true, 'edit', { load: vi.fn().mockResolvedValue(meta()), save: vi.fn() })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('tab', { name: '录入与校验' }))
    expect(container.querySelector('input.form-control')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('tab', { name: '权限与备注' }))
    expect(container.querySelector('input[type="checkbox"]')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('tab', { name: '高级设置' }))
    expect(screen.getByLabelText('虚拟字段')).toBeDisabled()
    expect(screen.getByText('最后修改人')).toBeInTheDocument()
    expect(screen.getByDisplayValue('admin')).toBeInTheDocument()
  })

  it('高级表达式受控编辑（校验/预览/发布）', async () => {
    renderModal(true, 'edit', {
      load: vi.fn().mockResolvedValue(meta({ virtualExpression: '1+1', convertFunction: 'CONVERT(X)', dataSourceSql: 'SELECT 1' })),
      save: vi.fn(),
      validateExpression: vi.fn().mockResolvedValue({ ok: true, errors: [], hints: ['白名单 v1'], whiteListVersion: 1 }),
      previewExpression: vi.fn().mockResolvedValue({ ok: true, errors: [], rows: [{ COL: 'v1' }] }),
      publishExpression: vi.fn().mockResolvedValue(undefined),
    })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('tab', { name: '高级设置' }))
    expect(screen.getByDisplayValue('1+1')).not.toBeDisabled()
    expect(screen.getByDisplayValue('CONVERT(X)')).not.toBeDisabled()
    expect(screen.getByDisplayValue('SELECT 1')).not.toBeDisabled()
    const validateButtons = screen.getAllByRole('button', { name: '校验' })
    expect(validateButtons).toHaveLength(3)
    const previewButtons = screen.getAllByRole('button', { name: '预览' })
    expect(previewButtons).toHaveLength(3)
    // 发布按钮在值未变化时禁用
    expect(screen.getAllByRole('button', { name: '发布' }).every((button) => button.hasAttribute('disabled'))).toBe(true)
  })

  it('表单布局分区展示并保存 FORM_* 值', async () => {
    const save = vi.fn().mockResolvedValue(undefined)
    const { container } = renderModal(true, 'edit', { load: vi.fn().mockResolvedValue(meta()), save })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('tab', { name: '表单布局' }))
    expect(screen.getByText('页签序号（FORM_TAB_NO）')).toBeInTheDocument()
    expect(screen.getByText('跨列宽度（FORM_SPAN）')).toBeInTheDocument()
    expect(screen.getByText('下拉选项（FORM_OPTIONS）')).toBeInTheDocument()
    // 修改跨列宽度为整行，保存后 payload 应携带 span=2
    const spanSelect = container.querySelector<HTMLSelectElement>('select.form-select')
    fireEvent.change(spanSelect!, { target: { value: '2' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(save).toHaveBeenCalled())
    expect(save.mock.calls[0][0]).toEqual(expect.objectContaining({ span: 2 }))
  })

  it('数据来源编辑与返回值映射', async () => {
    renderModal(true, 'edit', { load: vi.fn().mockResolvedValue(meta()), save: vi.fn() })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('tab', { name: '权限与备注' }))
    fireEvent.click(screen.getByLabelText('数据来源 1'))
    const tableInput = screen.getAllByText('来源表')[0].closest('.col-md-6')!.querySelector('input') as HTMLInputElement
    fireEvent.change(tableInput, { target: { value: 'PRODUCT' } })
    expect(screen.getByDisplayValue('PRODUCT')).toBeInTheDocument()
  })

  it('关闭按钮触发 onClose', async () => {
    const { onClose } = renderModal(true, 'edit', { load: vi.fn().mockResolvedValue(meta()), save: vi.fn() })
    fireEvent.click(screen.getByLabelText('关闭'))
    expect(onClose).toHaveBeenCalled()
  })

  it('列宽越界禁用保存', async () => {
    renderModal(true, 'edit', { load: vi.fn().mockResolvedValue(meta({ width: 500 })), save: vi.fn() })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    expect(screen.getByRole('button', { name: '保存' })).toBeDisabled()
  })

  it('load 返回 null 时显示未找到元数据', async () => {
    renderModal(true, 'edit', { load: vi.fn().mockResolvedValue(null), save: vi.fn() })
    await waitFor(() => expect(screen.getByText('未找到该字段的元数据。')).toBeInTheDocument())
  })

  it('提供 tables/modules 端点时渲染数据源选项', async () => {
    renderModal(true, 'edit', {
      load: vi.fn().mockResolvedValue(meta()),
      save: vi.fn(),
      tables: vi.fn().mockResolvedValue([{ value: 'PRODUCT', label: '产品 (PRODUCT)' }]),
      modules: vi.fn().mockResolvedValue([{ value: '1305', label: '库存仓别' }]),
    })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    await waitFor(() => expect(screen.getByText('产品 (PRODUCT)')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('tab', { name: '权限与备注' }))
    await waitFor(() => expect(screen.getByRole('option', { name: /库存仓别 \(1305\)/ })).toBeInTheDocument())
  })

  it('保存失败展示错误消息', async () => {
    renderModal(true, 'edit', {
      load: vi.fn().mockResolvedValue(meta()),
      save: vi.fn().mockRejectedValue(new Error('保存被拒绝')),
    })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(screen.getByText('保存被拒绝')).toBeInTheDocument())
  })

  it('新增模式携带 load 数据时重置主键与表名', async () => {
    const save = vi.fn().mockResolvedValue(undefined)
    renderModal(true, 'new', { load: vi.fn().mockResolvedValue(meta({ key: 'OLD', tableId: 'OLD_TABLE' })), save })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    const inputs = Array.from(document.querySelectorAll<HTMLInputElement>('input.form-control'))
    expect(inputs[1].value).toBe('')
    expect(inputs[0].value).toBe('T1')
  })

  it('校验分区切换与只读/必填开关', async () => {
    renderModal(true, 'edit', { load: vi.fn().mockResolvedValue(meta()), save: vi.fn() })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('tab', { name: '录入与校验' }))
    expect(screen.getByLabelText('不能为空')).toBeChecked()
    fireEvent.click(screen.getByLabelText('不能为空'))
    fireEvent.click(screen.getByLabelText('只读'))
    expect(screen.getByLabelText('不能为空')).not.toBeChecked()
    expect(screen.getByLabelText('只读')).toBeChecked()
  })

  it('显示分区可编辑格式与对齐', async () => {
    const { container } = renderModal(true, 'edit', { load: vi.fn().mockResolvedValue(meta()), save: vi.fn() })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    const inputs = Array.from(container.querySelectorAll<HTMLInputElement>('input.form-control'))
    fireEvent.change(inputs[4], { target: { value: 'yyyy-MM-dd' } })
    expect(screen.getByDisplayValue('yyyy-MM-dd')).toBeInTheDocument()
    const align = container.querySelectorAll('select')[1] as HTMLSelectElement
    fireEvent.change(align, { target: { value: 'right' } })
    expect(align.value).toBe('right')
  })

  it('全分区控件均可编辑并保存', async () => {
    const save = vi.fn().mockResolvedValue(undefined)
    renderModal(true, 'edit', {
      load: vi.fn().mockResolvedValue(meta()),
      save,
      modules: vi.fn().mockResolvedValue([{ value: '1305', label: '库存仓别' }]),
    })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    const dialog = screen.getByRole('dialog')
    const inputs = Array.from(dialog.querySelectorAll<HTMLInputElement>('input.form-control'))

    fireEvent.change(inputs[3], { target: { value: '150' } })
    fireEvent.change(dialog.querySelectorAll('select')[1], { target: { value: 'right' } })
    fireEvent.change(dialog.querySelectorAll('select')[2], { target: { value: 'left' } })
    fireEvent.change(inputs[4], { target: { value: 'yyyy' } })
    fireEvent.click(screen.getByLabelText('可见'))
    fireEvent.click(screen.getByLabelText('默认字段'))
    fireEvent.click(screen.getByLabelText('允许查询'))

    fireEvent.click(screen.getByRole('tab', { name: '录入与校验' }))
    const validationInputs = Array.from(dialog.querySelectorAll<HTMLInputElement>('input.form-control'))
    fireEvent.change(validationInputs[0], { target: { value: 'ABC' } })
    fireEvent.change(validationInputs[1], { target: { value: '3' } })
    fireEvent.change(validationInputs[2], { target: { value: '^\\d+$' } })
    fireEvent.click(screen.getByLabelText('不能为空'))
    fireEvent.click(screen.getByLabelText('只读'))

    fireEvent.click(screen.getByRole('tab', { name: '权限与备注' }))
    const securityInputs = Array.from(dialog.querySelectorAll<HTMLInputElement>('input.form-control'))
    fireEvent.click(screen.getByLabelText('成本字段'))
    fireEvent.click(screen.getByLabelText('保密字段'))
    fireEvent.change(screen.getByPlaceholderText('仅允许站内相对路径'), { target: { value: '/detail' } })
    await waitFor(() => expect(screen.getByRole('option', { name: /库存仓别 \(1305\)/ })).toBeInTheDocument())
    const selects = dialog.querySelectorAll('select')
    fireEvent.change(selects[selects.length - 1], { target: { value: '1305' } })
    fireEvent.click(screen.getByLabelText('数据仅可选入'))
    fireEvent.click(screen.getByLabelText('支持多笔选入'))
    fireEvent.change(securityInputs[1], { target: { value: '/chooser' } })
    fireEvent.click(screen.getByLabelText('数据来源 1'))
    fireEvent.change(screen.getAllByText('来源表')[0].closest('.col-md-6')!.querySelector('input')!, { target: { value: 'PRODUCT' } })
    fireEvent.change(screen.getAllByText('来源说明')[0].closest('.col-md-6')!.querySelector('input')!, { target: { value: '产品资料' } })
    fireEvent.change(screen.getAllByText('过滤条件')[0].closest('.col-12')!.querySelector('textarea')!, { target: { value: '1=1' } })
    fireEvent.change(screen.getAllByText('返回值映射')[0].closest('.col-12')!.querySelector('textarea')!, { target: { value: 'txt_PRO_NO=PRO_NO' } })

    fireEvent.click(screen.getByRole('tab', { name: '高级设置' }))
    fireEvent.click(screen.getByLabelText('数据可复制'))
    const advancedTextareas = dialog.querySelectorAll('textarea')
    fireEvent.change(advancedTextareas[2], { target: { value: '备注内容' } })

    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(save).toHaveBeenCalled())
    const [input] = save.mock.calls[0]
    expect(input).toEqual(expect.objectContaining({
      width: 150, align: 'right', headerAlign: 'left', format: 'yyyy',
      isVisible: false, isDefault: false, isQueryable: false,
      defaultValue: 'ABC', verifyIndex: 3, regex: '^\\d+$', isRequired: false, isReadonly: true,
      isCost: true, isSecrecy: true, browseUrl: '/detail', browseModuleId: 1305,
      onlyChoose: true, chooseMultiple: true, choosePage: '/chooser',
      canCopy: false, remark: '备注内容',
    }))
    expect(input.choosers[0]).toEqual(expect.objectContaining({
      active: true, table: 'PRODUCT', description: '产品资料', filter: '1=1', returnMapping: 'txt_PRO_NO=PRO_NO',
    }))
  })

  it('正则表达式无效时禁用保存并提示', async () => {
    renderModal(true, 'edit', { load: vi.fn().mockResolvedValue(meta({ regex: '[' })), save: vi.fn() })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('tab', { name: '录入与校验' }))
    expect(screen.getByText('正则表达式无法编译，请检查语法。')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '保存' })).toBeDisabled()
  })

  it('显示主键与类型不一致状态', async () => {
    renderModal(true, 'edit', {
      load: vi.fn().mockResolvedValue(meta({ isPrimaryKey: true, physicalExists: true, physicalType: 'float', typeMatches: false })),
      save: vi.fn(),
    })
    await waitFor(() => expect(screen.getByText('主键：是')).toBeInTheDocument())
    expect(screen.getByText('类型不一致（元数据 nvarchar / 物理 float）')).toBeInTheDocument()
  })

  it('幽灵字段显示元数据警告', async () => {
    renderModal(true, 'edit', { load: vi.fn().mockResolvedValue(meta({ physicalExists: false })), save: vi.fn() })
    await waitFor(() => expect(screen.getByText(/该字段元数据引用的物理列不存在/)).toBeInTheDocument())
    expect(screen.getByText('物理列：不存在')).toBeInTheDocument()
  })
})
