import { renderWithProviders } from '../../test/renderWithProviders'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { FieldEditorForm, type FieldEditorEndpoints, type FieldMeta } from './FieldEditorForm'


vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

function meta(overrides: Partial<FieldMeta> = {}): FieldMeta {
  return {
    key: 'CODE', tableId: 'T1', label: '编号', dataType: 'nvarchar', width: 120, align: 'left', headerAlign: 'center',
    format: null, isVisible: true, isDefault: true, isQueryable: true, isReadonly: false, isRequired: true,
    isCost: false, isSecrecy: false, defaultValue: null, verifyIndex: null, regex: null, remark: null,
    browseUrl: null, browseModuleId: null, onlyChoose: false, chooseMultiple: false, choosePage: null,
    choosers: [
      { active: false, table: null, description: null, moduleId: null, filter: null, returnMapping: null, serialNo: 1 },
      { active: false, table: null, description: null, moduleId: null, filter: null, returnMapping: null, serialNo: 2 },
      { active: false, table: null, description: null, moduleId: null, filter: null, returnMapping: null, serialNo: 3 },
      { active: false, table: null, description: null, moduleId: null, filter: null, returnMapping: null, serialNo: 4 },
    ],
    isVirtual: false, virtualExpression: null, canCopy: true, isAutoIncrement: false,
    convertFunction: null, dataSourceSql: null, lastUpdatedBy: 'admin', lastUpdatedAt: '2026-08-01T00:00:00Z',
    options: null,
    ...overrides,
  }
}

function renderForm(
  mode: 'new' | 'edit',
  endpoints: FieldEditorEndpoints,
  onSaved = vi.fn(),
  onCancel = vi.fn(),
  onStateChange?: (state: { canSave: boolean; saving: boolean; dirty: boolean }) => void,
) {
  return {
    ...renderWithProviders(
      <MemoryRouter>
        <FieldEditorForm
          mode={mode}
          tableId="T1"
          fieldKey="CODE"
          endpoints={endpoints}
          onCancel={onCancel}
          onSaved={onSaved}
          onStateChange={onStateChange}
          renderActions={({ canSave, onSave, onCancel: onFormCancel }) => (
            <div>
              <button disabled={!canSave} onClick={onSave}>保存</button>
              <button onClick={onFormCancel}>取消</button>
            </div>
          )}
        />
      </MemoryRouter>,
    ),
    onSaved,
    onCancel,
  }
}

describe('FieldEditorForm', () => {
  afterEach(() => {
    vi.clearAllMocks()
  })

  it('新增模式渲染空草稿且必填校验禁用保存', async () => {
    const save = vi.fn().mockResolvedValue(undefined)
    const { container } = renderForm('new', { load: vi.fn(), save })
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
  })

  it('新增模式保存成功后触发 onSaved', async () => {
    const save = vi.fn().mockResolvedValue(undefined)
    const { onSaved } = renderForm('new', { load: vi.fn(), save })
    const saveButton = screen.getByRole('button', { name: '保存' })
    const inputs = Array.from(document.querySelectorAll<HTMLInputElement>('input.form-control'))
    fireEvent.change(inputs[0], { target: { value: 'T2' } })
    fireEvent.change(inputs[1], { target: { value: 'CODE2' } })
    fireEvent.change(inputs[2], { target: { value: '新字段' } })
    fireEvent.change(document.querySelector<HTMLSelectElement>('select.form-select')!, { target: { value: 'decimal' } })
    await waitFor(() => expect(saveButton).toBeEnabled())
    fireEvent.click(saveButton)
    await waitFor(() => expect(onSaved).toHaveBeenCalled())
  })

  it('编辑模式加载元数据并保存（含 original）', async () => {
    const loaded = meta()
    const save = vi.fn().mockResolvedValue(undefined)
    const { container, onSaved } = renderForm('edit', { load: vi.fn().mockResolvedValue(loaded), save })
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
    renderForm('edit', { load: vi.fn().mockRejectedValue(new Error('boom')), save: vi.fn() })
    await waitFor(() => expect(screen.getByText('无法加载该字段的元数据，请确认当前账号具有字段设置权限。')).toBeInTheDocument())
  })

  it('编辑加载中显示 LoadingState', () => {
    renderForm('edit', { load: vi.fn().mockReturnValue(new Promise(() => undefined)), save: vi.fn() })
    expect(screen.getByText('正在加载字段元数据…')).toBeInTheDocument()
  })

  it('分区切换展示对应控件', async () => {
    const { container } = renderForm('edit', { load: vi.fn().mockResolvedValue(meta()), save: vi.fn() })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('tab', { name: '基本信息' }))
    expect(container.querySelector('input.form-control')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('tab', { name: '权限与行为' }))
    expect(container.querySelector('input[type="checkbox"]')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('tab', { name: '高级设置' }))
    expect(screen.getByLabelText('虚拟字段')).toBeDisabled()
    expect(screen.getByText('最后修改人')).toBeInTheDocument()
    expect(screen.getByDisplayValue('admin')).toBeInTheDocument()
  })

  it('高级表达式受控编辑（校验/预览/发布）', async () => {
    renderForm('edit', {
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
    expect(screen.getAllByRole('button', { name: '发布' }).every((button) => button.hasAttribute('disabled'))).toBe(true)
  })

  it('字段维护页不含任何表单设计入口，下拉选项归基本信息', async () => {
    const save = vi.fn().mockResolvedValue(undefined)
    const loaded = meta({ options: 'O=外含税;I=内含税' })
    renderForm('edit', { load: vi.fn().mockResolvedValue(loaded), save })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    // 形态（排布 / 占位 / 复合格 / 页签）不在字段维护页编辑：既无「表单布局」页签，也无跳转设计态的入口
    expect(screen.queryByRole('tab', { name: '表单布局' })).toBeNull()
    expect(screen.queryByText(/已归模块级版式/)).toBeNull()
    expect(screen.queryByText('页签序号（FORM_TAB_NO）')).toBeNull()
    expect(screen.queryByText('跨列宽度（FORM_SPAN）')).toBeNull()
    expect(screen.queryByText('复合格组（FORM_CELL_GROUP）')).toBeNull()
    expect(screen.queryByRole('button', { name: '打开表单设计' })).toBeNull()
    expect(screen.queryByRole('link', { name: '打开表单设计' })).toBeNull()
    // 下拉选项是字段取值语义，随「基本信息」一起编辑
    expect(screen.getByText('下拉选项（FORM_OPTIONS）')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(save).toHaveBeenCalled())
    expect(save.mock.calls[0][0]).toEqual(expect.objectContaining({ options: loaded.options }))
  })

  it('数据来源编辑与返回值映射', async () => {
    renderForm('edit', { load: vi.fn().mockResolvedValue(meta()), save: vi.fn() })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('tab', { name: '数据来源' }))
    fireEvent.click(screen.getAllByRole('button', { name: '编辑' })[0])
    await waitFor(() => expect(screen.getByText('编辑数据源：未命名数据源')).toBeInTheDocument())
    fireEvent.click(screen.getAllByLabelText('关闭')[0])
    await waitFor(() => expect(screen.queryByText('编辑数据源：未命名数据源')).not.toBeInTheDocument())
  })

  it('编辑已有数据源时弹窗回读已保存的过滤/回填行（构建器按列表序号对齐）', async () => {
    const loaded = meta({
      choosers: [
        { active: true, table: 'CLIENT', description: '客户资料', moduleId: null, filter: '{"logic":"AND","items":[{"field":"CLIENT.CREDIT_LIMIT","operator":"GT","value":"0","nullSafe":null}]}', returnMapping: '[{"target":"CLIENT_NAME","column":"CLIENT_NAME"}]', serialNo: 1 },
      ],
    })
    renderForm('edit', { load: vi.fn().mockResolvedValue(loaded), save: vi.fn() })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('tab', { name: '数据来源' }))
    fireEvent.click(screen.getAllByRole('button', { name: '编辑' })[0])
    await waitFor(() => expect(screen.getByText('编辑数据源：客户资料')).toBeInTheDocument())
    await waitFor(() => expect(screen.getByDisplayValue('0')).toBeInTheDocument())
    expect(screen.getByRole('button', { name: '回填来源列' })).toBeInTheDocument()
  })

  it('删除数据源需确认，取消则保留，确认后移除', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false)
    renderForm('edit', { load: vi.fn().mockResolvedValue(meta()), save: vi.fn() })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('tab', { name: '数据来源' }))
    expect(screen.getAllByRole('button', { name: '删除' })).toHaveLength(4)
    fireEvent.click(screen.getAllByRole('button', { name: '删除' })[0])
    expect(confirmSpy).toHaveBeenCalled()
    expect(screen.getAllByRole('button', { name: '删除' })).toHaveLength(4)
    confirmSpy.mockReturnValue(true)
    fireEvent.click(screen.getAllByRole('button', { name: '删除' })[0])
    expect(screen.getAllByRole('button', { name: '删除' })).toHaveLength(3)
    confirmSpy.mockRestore()
  })

  it('编辑内容后上报 dirty，保存成功后归零', async () => {
    const states: { canSave: boolean; saving: boolean; dirty: boolean }[] = []
    const save = vi.fn().mockResolvedValue(undefined)
    renderForm('edit', { load: vi.fn().mockResolvedValue(meta()), save }, vi.fn(), vi.fn(), state => states.push(state))
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    expect(states.at(-1)?.dirty).toBe(false)
    fireEvent.change(screen.getByDisplayValue('编号'), { target: { value: '编号2' } })
    await waitFor(() => expect(states.at(-1)?.dirty).toBe(true))
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(save).toHaveBeenCalled())
    await waitFor(() => expect(states.at(-1)?.dirty).toBe(false))
  })

  it('取消按钮触发 onCancel', async () => {
    const { onCancel } = renderForm('edit', { load: vi.fn().mockResolvedValue(meta()), save: vi.fn() })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '取消' }))
    expect(onCancel).toHaveBeenCalled()
  })

  it('列宽越界禁用保存', async () => {
    renderForm('edit', { load: vi.fn().mockResolvedValue(meta({ width: 500 })), save: vi.fn() })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    expect(screen.getByRole('button', { name: '保存' })).toBeDisabled()
  })

  it('load 返回 null 时显示未找到元数据', async () => {
    renderForm('edit', { load: vi.fn().mockResolvedValue(null), save: vi.fn() })
    await waitFor(() => expect(screen.getByText('未找到该字段的元数据。')).toBeInTheDocument())
  })

  it('提供 modules 端点时渲染权限模块选项', async () => {
    renderForm('edit', {
      load: vi.fn().mockResolvedValue(meta()),
      save: vi.fn(),
      modules: vi.fn().mockResolvedValue([{ value: '1305', label: '库存仓别' }]),
    })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('tab', { name: '权限与行为' }))
    await waitFor(() => expect(screen.getAllByText('库存仓别 (1305)').length).toBeGreaterThan(0))
  })

  it('保存失败展示错误消息', async () => {
    renderForm('edit', {
      load: vi.fn().mockResolvedValue(meta()),
      save: vi.fn().mockRejectedValue(new Error('保存被拒绝')),
    })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(screen.getByText('保存被拒绝')).toBeInTheDocument())
  })

  it('新增模式携带 load 数据时重置主键与表名', async () => {
    const save = vi.fn().mockResolvedValue(undefined)
    renderForm('new', { load: vi.fn().mockResolvedValue(meta({ key: 'OLD', tableId: 'OLD_TABLE' })), save })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    const inputs = Array.from(document.querySelectorAll<HTMLInputElement>('input.form-control'))
    expect(inputs[1].value).toBe('')
    expect(inputs[0].value).toBe('T1')
  })

  it('校验分区切换与只读/必填开关', async () => {
    renderForm('edit', { load: vi.fn().mockResolvedValue(meta()), save: vi.fn() })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('tab', { name: '基本信息' }))
    expect(screen.getByLabelText('不能为空')).toBeChecked()
    fireEvent.click(screen.getByLabelText('不能为空'))
    fireEvent.click(screen.getByLabelText('只读'))
    expect(screen.getByLabelText('不能为空')).not.toBeChecked()
    expect(screen.getByLabelText('只读')).toBeChecked()
  })

  it('显示分区可编辑格式与对齐', async () => {
    const { container } = renderForm('edit', { load: vi.fn().mockResolvedValue(meta()), save: vi.fn() })
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
    renderForm('edit', {
      load: vi.fn().mockResolvedValue(meta()),
      save,
      modules: vi.fn().mockResolvedValue([{ value: '1305', label: '库存仓别' }]),
    })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    const body = document.body
    const inputs = Array.from(body.querySelectorAll<HTMLInputElement>('input.form-control'))

    fireEvent.change(inputs[3], { target: { value: '150' } })
    fireEvent.change(body.querySelectorAll('select')[1], { target: { value: 'right' } })
    fireEvent.change(body.querySelectorAll('select')[2], { target: { value: 'left' } })
    fireEvent.change(inputs[4], { target: { value: 'yyyy' } })
    fireEvent.click(screen.getByLabelText('可见'))
    fireEvent.click(screen.getByLabelText('默认字段'))
    fireEvent.click(screen.getByLabelText('允许查询'))

    fireEvent.click(screen.getByRole('tab', { name: '基本信息' }))
    const defaultInput = screen.getAllByText('默认值')[0].closest('.col-md-4')!.querySelector('input') as HTMLInputElement
    fireEvent.change(defaultInput, { target: { value: 'ABC' } })
    const verifyInput = screen.getAllByText('检验顺序')[0].closest('.col-md-4')!.querySelector('input') as HTMLInputElement
    fireEvent.change(verifyInput, { target: { value: '3' } })
    fireEvent.change(screen.getByPlaceholderText('如 ^[A-Z0-9]{8}$'), { target: { value: '^\\d+$' } })
    fireEvent.click(screen.getByLabelText('不能为空'))
    fireEvent.click(screen.getByLabelText('只读'))

    fireEvent.click(screen.getByRole('tab', { name: '权限与行为' }))
    const securityInputs = Array.from(body.querySelectorAll<HTMLInputElement>('input.form-control'))
    fireEvent.click(screen.getByLabelText('成本字段'))
    fireEvent.click(screen.getByLabelText('保密字段'))
    fireEvent.change(screen.getByPlaceholderText('仅允许站内相对路径'), { target: { value: '/detail' } })
    await waitFor(() => expect(screen.getAllByText('库存仓别 (1305)').length).toBeGreaterThan(0))
    const browseModuleSelect = screen.getAllByText('浏览权限模块 ID')[0].closest('.col-md-4')!.querySelector('select') as HTMLSelectElement
    fireEvent.change(browseModuleSelect, { target: { value: '1305' } })
    fireEvent.click(screen.getByLabelText('数据仅可选入'))
    fireEvent.click(screen.getByLabelText('支持多笔选入'))
    fireEvent.change(securityInputs[1], { target: { value: '/chooser' } })
    fireEvent.click(screen.getByRole('tab', { name: '数据来源' }))
    fireEvent.click(screen.getByRole('button', { name: '新增数据源' }))
    await waitFor(() => expect(screen.getByRole('heading', { name: '新增数据源' })).toBeInTheDocument())
    fireEvent.click(screen.getAllByLabelText('关闭')[0])

    fireEvent.click(screen.getByRole('tab', { name: '高级设置' }))
    fireEvent.click(screen.getByLabelText('数据可复制'))
    const advancedTextareas = body.querySelectorAll('textarea')
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
      filter: '{"logic":"AND","items":[]}',
      returnMapping: '',
    }))
  })

  it('正则表达式无效时禁用保存并提示', async () => {
    renderForm('edit', { load: vi.fn().mockResolvedValue(meta({ regex: '[' })), save: vi.fn() })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('tab', { name: '基本信息' }))
    expect(screen.getByText('正则表达式无法编译，请检查语法。')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '保存' })).toBeDisabled()
  })

  it('显示主键与类型不一致状态', async () => {
    renderForm('edit', {
      load: vi.fn().mockResolvedValue(meta({ isPrimaryKey: true, physicalExists: true, physicalType: 'float', typeMatches: false })),
      save: vi.fn(),
    })
    await waitFor(() => expect(screen.getByText('主键：是')).toBeInTheDocument())
    expect(screen.getByText('类型不一致（元数据 nvarchar / 物理 float）')).toBeInTheDocument()
  })

  it('幽灵字段显示元数据警告', async () => {
    renderForm('edit', { load: vi.fn().mockResolvedValue(meta({ physicalExists: false })), save: vi.fn() })
    await waitFor(() => expect(screen.getByText(/该字段元数据引用的物理列不存在/)).toBeInTheDocument())
    expect(screen.getByText('物理列：不存在')).toBeInTheDocument()
  })

  it('系统列编辑态锁定结构控件', async () => {
    const { container } = renderForm('edit', {
      load: vi.fn().mockResolvedValue(meta({ key: 'CONFIRM_TAG', label: '批核状态', isSystemColumn: true })),
      save: vi.fn(),
    })
    await waitFor(() => expect(screen.getByDisplayValue('批核状态')).toBeInTheDocument())
    // 数据库类型下拉被锁定，标题仍可编辑
    expect(container.querySelectorAll<HTMLSelectElement>('select.form-select')[0]).toBeDisabled()
    expect(screen.getByDisplayValue('批核状态')).not.toBeDisabled()
  })

  it('高级设置：转换函数构建器按服务端注册表写入表达式', async () => {
    renderForm('edit', {
      load: vi.fn().mockResolvedValue(meta()),
      save: vi.fn(),
      parseExpression: vi.fn().mockResolvedValue({ kind: 'convert_function', mode: 'raw' }),
      expressionRegistry: vi.fn().mockResolvedValue({
        whiteListVersion: 1,
        convertFunctions: [{ name: 'f_get_emp_name_by_id', description: '员工编号 → 姓名' }],
      }),
    })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('tab', { name: '高级设置' }))
    // 文本为空 → 构建器以空模型起步，可直接选
    fireEvent.click(await screen.findByRole('button', { name: '转换函数（受控注册表）' }))
    fireEvent.click(await screen.findByRole('option', { name: /f_get_emp_name_by_id/ }))
    await waitFor(() => expect(screen.getByLabelText('转换函数文本')).toHaveValue('f_get_emp_name_by_id'))
  })

  it('高级设置：虚拟表达式构建器按本表列与关联白名单生成引用', async () => {
    const tableColumns = vi.fn().mockImplementation(async (table: string) => table === 'CLIENT'
      ? [{ name: 'CLIENT_NAME', dataType: 'nvarchar', description: '客户名称' }]
      : [
        { name: 'AMOUNT', dataType: 'decimal', description: '金额' },
        { name: 'VIRT_AMOUNT', dataType: 'decimal', description: '虚拟金额', isVirtual: true },
      ])
    renderForm('edit', {
      load: vi.fn().mockResolvedValue(meta()),
      save: vi.fn(),
      parseExpression: vi.fn().mockResolvedValue({ kind: 'virtual_exp', mode: 'raw' }),
      tables: vi.fn().mockResolvedValue([
        { value: 'T1', label: '订单主档 (T1)' },
        { value: 'CLIENT', label: '客户资料 (CLIENT)' },
      ]),
      tableRelations: vi.fn().mockResolvedValue({
        tableId: 'T1',
        ok: true,
        error: null,
        items: [{ table: 'CLIENT', alias: 'CLIENT_J', conditions: ['CLIENT_J.CLIENT_ID=T1.CLIENT_ID'] }],
      }),
      tableColumns,
    })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('tab', { name: '高级设置' }))
    fireEvent.click(await screen.findByRole('button', { name: '虚拟表达式引用来源' }))
    // 来源按「表」展示：本表 + 关联表（带别名时注明 AS 别名）
    expect(await screen.findByRole('option', { name: /订单主档（T1）/ })).toBeInTheDocument()
    expect(screen.getByRole('option', { name: /客户资料（CLIENT） AS CLIENT_J/ })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('option', { name: /客户资料（CLIENT） AS CLIENT_J/ }))
    // 关联条件来自 QUERY_RELATION，随所选段展示（不随字段保存）
    expect(await screen.findByText('关联条件：CLIENT_J.CLIENT_ID=T1.CLIENT_ID')).toBeInTheDocument()
    expect(screen.getByText(/该段在 QUERY_RELATION 里带别名/)).toBeInTheDocument()
    // 列清单按该段背后的物理表取
    await waitFor(() => expect(tableColumns).toHaveBeenCalledWith('CLIENT'))
    fireEvent.click(screen.getByRole('button', { name: '虚拟表达式引用列' }))
    // 受控虚拟列不进候选：引用列按物理存在性校验
    expect(screen.queryByRole('option', { name: /虚拟金额/ })).toBeNull()
    fireEvent.click(await screen.findByRole('option', { name: /客户名称\(CLIENT_NAME\)/ }))
    // 写入的仍是该段在 QUERY_RELATION 里的名字（别名），不是物理表名
    await waitFor(() => expect(screen.getByLabelText('虚拟表达式文本')).toHaveValue('CLIENT_J.CLIENT_NAME'))
    fireEvent.click(screen.getByRole('button', { name: '虚拟表达式引用来源' }))
    fireEvent.click(await screen.findByRole('option', { name: /订单主档（T1）/ }))
    expect(await screen.findByText('引用本表列，不需要关联条件。')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '虚拟表达式引用列' }))
    fireEvent.click(await screen.findByRole('option', { name: /金额\(AMOUNT\)/ }))
    await waitFor(() => expect(screen.getByLabelText('虚拟表达式文本')).toHaveValue('T1.AMOUNT'))
  })

  it('高级设置：来源不在 QUERY_RELATION 内时原样列出，不显示成未选择', async () => {
    renderForm('edit', {
      load: vi.fn().mockResolvedValue(meta({ virtualExpression: 'SYSDG.G_DESC' })),
      save: vi.fn(),
      parseExpression: vi.fn().mockResolvedValue({ kind: 'virtual_exp', mode: 'reference', table: 'SYSDG', column: 'G_DESC' }),
      tableRelations: vi.fn().mockResolvedValue({ tableId: 'T1', ok: true, error: null, items: [] }),
      tableColumns: vi.fn().mockResolvedValue([]),
    })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('tab', { name: '高级设置' }))
    await waitFor(() => expect(screen.getByLabelText('虚拟表达式文本')).toHaveValue('SYSDG.G_DESC'))
    fireEvent.click(await screen.findByRole('button', { name: '虚拟表达式引用来源' }))
    expect(await screen.findByRole('option', { name: /不在 QUERY_RELATION 内/ })).toBeInTheDocument()
  })

  it('高级设置：数据源 SQL 构建器生成受限 SELECT 文本', async () => {
    renderForm('edit', {
      load: vi.fn().mockResolvedValue(meta({ dataSourceSql: 'SELECT G_IDX FROM SYSDG' })),
      save: vi.fn(),
      parseExpression: vi.fn().mockResolvedValue({
        kind: 'datasource_sql',
        mode: 'tableSql',
        dataSource: {
          table: 'SYSDG',
          columns: ['G_IDX'],
          whereColumn: null,
          whereValue: null,
          whereValueIsString: true,
          orderColumn: null,
          orderDirection: null,
        },
      }),
      tableColumns: vi.fn().mockResolvedValue([
        { name: 'G_IDX', dataType: 'nvarchar', description: '代码' },
        { name: 'G_DESC', dataType: 'nvarchar', description: '名称' },
        { name: 'SORT_NO', dataType: 'int', description: '序号' },
      ]),
    })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('tab', { name: '高级设置' }))
    await waitFor(() => expect(screen.getByLabelText('数据源 SQL 文本')).toHaveValue('SELECT G_IDX FROM SYSDG'))
    fireEvent.click(await screen.findByRole('checkbox', { name: /名称\(G_DESC\)/ }))
    await waitFor(() => expect(screen.getByLabelText('数据源 SQL 文本')).toHaveValue('SELECT G_IDX,G_DESC FROM SYSDG'))
    fireEvent.change(screen.getByLabelText('数据源 SQL 过滤列'), { target: { value: 'G_IDX' } })
    fireEvent.change(screen.getByLabelText('数据源 SQL 过滤值'), { target: { value: 'A1' } })
    fireEvent.change(screen.getByLabelText('数据源 SQL 排序列'), { target: { value: 'SORT_NO' } })
    fireEvent.change(screen.getByLabelText('数据源 SQL 排序方向'), { target: { value: 'DESC' } })
    await waitFor(() => expect(screen.getByLabelText('数据源 SQL 文本'))
      .toHaveValue("SELECT G_IDX,G_DESC FROM SYSDG WHERE G_IDX = 'A1' ORDER BY SORT_NO DESC"))
  })

  it('高级设置：构建器不覆盖的形态提示后仍可编辑原始文本', async () => {
    renderForm('edit', {
      load: vi.fn().mockResolvedValue(meta({ virtualExpression: 'A.QTY+B.QTY' })),
      save: vi.fn(),
      parseExpression: vi.fn().mockResolvedValue({ kind: 'virtual_exp', mode: 'arithmetic' }),
      tableRelations: vi.fn().mockResolvedValue({ tableId: 'T1', ok: true, error: null, items: [] }),
      tableColumns: vi.fn().mockResolvedValue([]),
    })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('tab', { name: '高级设置' }))
    await waitFor(() => expect(screen.getByText(/请用下方原始文本编辑/)).toBeInTheDocument())
    expect(screen.getByLabelText('虚拟表达式文本')).toHaveValue('A.QTY+B.QTY')
    expect(screen.getByRole('button', { name: '虚拟表达式引用列' })).toBeDisabled()
  })

  it('高级设置：手工编辑原始文本后构建器等待显式载入', async () => {
    const parseExpression = vi.fn().mockResolvedValue({ kind: 'convert_function', mode: 'raw' })
    renderForm('edit', {
      load: vi.fn().mockResolvedValue(meta()),
      save: vi.fn(),
      parseExpression,
      expressionRegistry: vi.fn().mockResolvedValue({ whiteListVersion: 1, convertFunctions: [] }),
    })
    await waitFor(() => expect(screen.getByDisplayValue('编号')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('tab', { name: '高级设置' }))
    fireEvent.change(screen.getByLabelText('转换函数文本'), { target: { value: 'f_get_emp_name_by_id' } })
    await waitFor(() => expect(screen.getByText('表达式已手工编辑，构建器待同步。')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '载入构建器' }))
    await waitFor(() => expect(parseExpression).toHaveBeenCalledWith('convert_function', 'f_get_emp_name_by_id'))
  })
})