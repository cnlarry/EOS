import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { FormFieldDefinition } from './formDefinition'
import { FormFieldRenderer } from './FormFieldRenderer'
import { fieldVariant, isFullWidthField } from './formFieldKind'

function field(overrides: Partial<FormFieldDefinition>): FormFieldDefinition {
  return {
    key: 'f1',
    label: '字段一',
    dataType: 'nvarchar',
    displayLength: 100,
    displayFormat: null,
    isRequired: false,
    verifyIndex: null,
    regex: null,
    defaultValue: null,
    isReadonly: false,
    isVisible: true,
    onlyChoose: false,
    chooseMultiple: false,
    choosePage: null,
    choosers: [],
    isPrimaryKey: false,
    isAutoIncrement: false,
    isVirtual: false,
    isCost: false,
    isSecrecy: false,
    serverFilled: false,
    maxLength: null,
    tabNo: 1,
    formOrder: null,
    span: 1,
    newLine: false,
    cellGroup: null,
    cellRole: 0,
    options: [],
    displayOnly: false, canCopy: true,
    ...overrides,
  }
}

describe('FormFieldRenderer', () => {
  it('REMARK 类与 text/ntext 字段判定整行独占', () => {
    expect(isFullWidthField(field({ key: 'REMARK' }))).toBe(true)
    expect(isFullWidthField(field({ key: 'PRODUCE_REMARK' }))).toBe(true)
    expect(isFullWidthField(field({ dataType: 'ntext' }))).toBe(true)
    expect(isFullWidthField(field({ key: 'TEL', displayLength: 220 }))).toBe(false)
  })

  it('变体判定：date/datetime 分流、数值系归 decimal、FORM_OPTIONS 归 select', () => {
    expect(fieldVariant(field({}))).toBe('text')
    expect(fieldVariant(field({ dataType: 'int' }))).toBe('decimal')
    expect(fieldVariant(field({ dataType: 'money' }))).toBe('decimal')
    expect(fieldVariant(field({ dataType: 'date' }))).toBe('date')
    expect(fieldVariant(field({ dataType: 'datetime' }))).toBe('datetime')
    expect(fieldVariant(field({ dataType: 'smalldatetime' }))).toBe('datetime')
    expect(fieldVariant(field({ dataType: 'bit' }))).toBe('checkbox')
    expect(fieldVariant(field({ key: 'REMARK' }))).toBe('textarea')
    expect(fieldVariant(field({ options: [{ value: 'O', label: 'O' }] }))).toBe('select')
  })

  it('渲染成对网格结构：标签左侧 + 控件右侧，标签带 title 全文', () => {
    const { container } = render(<FormFieldRenderer field={field({})} value="" onChange={() => undefined} />)
    const cell = container.querySelector('.erp-form-field')
    expect(cell).not.toBeNull()
    const label = container.querySelector('.erp-form-label')
    expect(label?.textContent).toBe('字段一')
    expect(label).toHaveAttribute('title', '字段一')
    expect(cell?.className).not.toContain('is-full')
  })

  it('REMARK 字段带整行独占类名并渲染为多行文本框', () => {
    const { container } = render(<FormFieldRenderer field={field({ key: 'REMARK', label: '备注' })} value="说明" onChange={() => undefined} />)
    expect(container.querySelector('.erp-form-field')?.className).toContain('is-full')
    expect(container.querySelector('textarea')).not.toBeNull()
    expect(container.querySelector('textarea')).toHaveValue('说明')
  })

  it('按变体渲染控件类型', () => {
    const { rerender, container } = render(<FormFieldRenderer field={field({})} value="" onChange={() => undefined} />)
    expect(container.querySelector('input')).toHaveAttribute('type', 'text')
    rerender(<FormFieldRenderer field={field({ dataType: 'money' })} value="" onChange={() => undefined} />)
    // decimal 变体：type=text + inputmode=decimal（不再用原生 number）
    expect(container.querySelector('input')).toHaveAttribute('type', 'text')
    expect(container.querySelector('input')).toHaveAttribute('inputMode', 'decimal')
    rerender(<FormFieldRenderer field={field({ dataType: 'datetime' })} value="2026-08-23 14:30:00" onChange={() => undefined} />)
    expect(container.querySelector('input')).toHaveAttribute('type', 'datetime-local')
    expect(container.querySelector('input')).toHaveAttribute('step', '1')
    // jsdom 会把 datetime-local 值规范化到分钟；秒分量的保留由 dateTimeValue 单测覆盖
    expect((container.querySelector('input') as HTMLInputElement).value).toMatch(/^2026-08-23T14:30/)
    rerender(<FormFieldRenderer field={field({ dataType: 'smalldatetime' })} value="2026-08-23 14:30" onChange={() => undefined} />)
    expect((container.querySelector('input') as HTMLInputElement).value).toMatch(/^2026-08-23T14:30/)
    rerender(<FormFieldRenderer field={field({ dataType: 'bit' })} value="1" onChange={() => undefined} />)
    expect(container.querySelector('input')).toBeChecked()
  })

  it('decimal 失焦规范化与 DISPLAY_FORMAT 展示格式化', () => {
    const onChange = vi.fn()
    const { container } = render(
      <FormFieldRenderer field={field({ dataType: 'decimal', displayFormat: '#,##0.00' })} value="1234.5" onChange={onChange} />,
    )
    const input = container.querySelector('input')!
    // React 19 onBlur 绑定原生 focusout（jsdom 的 blur 不触发）
    fireEvent(input, new FocusEvent('focusout', { bubbles: true }))
    expect(onChange).toHaveBeenCalledWith('1,234.50')
  })

  it('浏览态（viewing）全量渲染只读文本，不渲染输入框', () => {
    const { container } = render(<FormFieldRenderer field={field({})} value="内容" onChange={() => undefined} viewing />)
    expect(container.querySelector('input')).toBeNull()
    expect(container.querySelector('.erp-form-static')?.textContent).toBe('内容')
  })

  it('浏览态 bit 字段渲染只读复选框（勾选反映状态）', () => {
    const { container, rerender } = render(<FormFieldRenderer field={field({ dataType: 'bit' })} value="1" onChange={() => undefined} viewing />)
    const checkedBox = container.querySelector('input[type="checkbox"]')
    expect(checkedBox).not.toBeNull()
    expect(checkedBox).toBeDisabled()
    expect(checkedBox).toBeChecked()
    expect(container.querySelector('.erp-form-static-badge')).toBeNull()
    rerender(<FormFieldRenderer field={field({ dataType: 'bit' })} value="0" onChange={() => undefined} viewing />)
    expect(container.querySelector('input[type="checkbox"]')).not.toBeChecked()
  })

  it('编辑态 serverFilled 无选择器渲染只读文本；带选择器保留只读框+可用按钮', () => {
    const onChoose = vi.fn()
    const { container, rerender } = render(<FormFieldRenderer field={field({ serverFilled: true })} value="x" onChange={() => undefined} />)
    expect(container.querySelector('input')).toBeNull()
    expect(container.querySelector('.erp-form-static')).not.toBeNull()

    const chooserSource = [{ active: true, table: 'CURR', description: null, moduleId: null, filter: null, returnMapping: '[{"target":"cur","column":"ID"}]', serialNo: 1 }]
    rerender(
      <FormFieldRenderer
        field={field({ serverFilled: true, choosers: chooserSource })}
        value="USD"
        onChange={() => undefined}
        onChoose={onChoose}
      />,
    )
    // 只读联动字段（如 CURR_ID）：输入框禁用但选择按钮仍可用（对齐既有交互）
    expect(container.querySelector('input.form-control')).toBeDisabled()
    const button = screen.getByRole('button', { name: '选择' })
    expect(button).not.toBeDisabled()
    fireEvent.click(button)
    expect(onChoose).toHaveBeenCalled()
  })

  it('必填非布尔字段显示星号，布尔字段不显示', () => {
    render(<FormFieldRenderer field={field({ isRequired: true })} value="" onChange={() => undefined} />)
    expect(screen.getByText('字段一 *')).toBeInTheDocument()
  })

  it('错误信息与正则提示展示', () => {
    const { container } = render(<FormFieldRenderer field={field({ regex: '^\\d+$' })} value="x" error="内容不符合格式要求。" onChange={() => undefined} />)
    expect(screen.getByText('内容不符合格式要求。')).toBeInTheDocument()
    expect(screen.getByText('格式校验：^\\d+$')).toBeInTheDocument()
    expect(container.querySelector('input')).toHaveClass('is-invalid')
  })

  it('有活跃数据来源时显示选择按钮并触发 onChoose', () => {
    const onChoose = vi.fn()
    render(<FormFieldRenderer field={field({ choosers: [{ active: true, table: 'PRODUCT', description: null, moduleId: null, filter: null, returnMapping: '[{"target":"code","column":"PRO_NO"}]', serialNo: 1 }] })} value="" onChange={() => undefined} onChoose={onChoose} />)
    fireEvent.click(screen.getByRole('button', { name: '选择' }))
    expect(onChoose).toHaveBeenCalled()
  })

  it('无活跃数据来源时不显示选择按钮', () => {
    render(<FormFieldRenderer field={field({ choosers: [{ active: false, table: null, description: null, moduleId: null, filter: null, returnMapping: null, serialNo: null }] })} value="" onChange={() => undefined} onChoose={vi.fn()} />)
    expect(screen.queryByRole('button', { name: '选择' })).not.toBeInTheDocument()
  })

  it('checkbox 变更映射为 1/0', () => {
    const onChange = vi.fn()
    const { container } = render(<FormFieldRenderer field={field({ dataType: 'bit' })} value="0" onChange={onChange} />)
    fireEvent.click(container.querySelector('input')!)
    expect(onChange).toHaveBeenCalledWith('1')
  })

  it('datetime 控件变更提交 yyyy-MM-ddTHH:mm:ss 规范串', () => {
    const onChange = vi.fn()
    const { container } = render(<FormFieldRenderer field={field({ dataType: 'datetime' })} value="" onChange={onChange} />)
    fireEvent.change(container.querySelector('input')!, { target: { value: '2026-08-23T14:30' } })
    expect(onChange).toHaveBeenCalledWith('2026-08-23T14:30:00')
  })

  it('有 FORM_OPTIONS 时渲染 select 下拉', () => {
    const onChange = vi.fn()
    const { container } = render(<FormFieldRenderer field={field({ options: [{ value: 'O', label: 'O、外含税' }, { value: 'I', label: 'I、内含税' }] })} value="O" onChange={onChange} />)
    const select = container.querySelector('select')
    expect(select).not.toBeNull()
    expect(Array.from(select?.options ?? []).map(option => option.textContent)).toEqual(['O、外含税', 'I、内含税'])
    fireEvent.change(select!, { target: { value: 'I' } })
    expect(onChange).toHaveBeenCalledWith('I')
  })

  it('下拉字段即使配置了选择器也不显示选择按钮', () => {
    const { container } = render(<FormFieldRenderer
      field={field({ options: [{ value: 'O', label: 'O、外含税' }], choosers: [{ active: true, table: 'TAX', description: null, moduleId: null, filter: null, returnMapping: null, serialNo: 1 }] })}
      value="O"
      onChange={() => undefined}
      onChoose={vi.fn()}
    />)
    expect(container.querySelector('select')).not.toBeNull()
    expect(screen.queryByRole('button', { name: '选择' })).not.toBeInTheDocument()
  })

  it('bare 模式不渲染标签与格线包装', () => {
    const { container } = render(<FormFieldRenderer field={field({})} value="" bare onChange={() => undefined} />)
    expect(container.querySelector('.erp-form-field')).toBeNull()
    expect(container.querySelector('.erp-form-label')).toBeNull()
    expect(container.querySelector('.erp-form-control')).not.toBeNull()
  })
})
