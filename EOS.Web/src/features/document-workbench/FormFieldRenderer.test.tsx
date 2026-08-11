import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { FormFieldDefinition } from './formDefinition'
import { FormFieldRenderer } from './FormFieldRenderer'
import { inputKind, isFullWidthField } from './formFieldKind'

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
    displayOnly: false,
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

  it('渲染成对网格结构：标签左侧 + 控件右侧', () => {
    const { container } = render(<FormFieldRenderer field={field({})} value="" onChange={() => undefined} />)
    const cell = container.querySelector('.erp-form-field')
    expect(cell).not.toBeNull()
    expect(container.querySelector('.erp-form-label')?.textContent).toBe('字段一')
    expect(cell?.className).not.toContain('is-full')
  })

  it('REMARK 字段带整行独占类名并渲染为多行文本框', () => {
    const { container } = render(<FormFieldRenderer field={field({ key: 'REMARK', label: '备注' })} value="说明" onChange={() => undefined} />)
    expect(container.querySelector('.erp-form-field')?.className).toContain('is-full')
    expect(container.querySelector('textarea')).not.toBeNull()
    expect(container.querySelector('textarea')).toHaveValue('说明')
  })

  it('只读字段带灰显类名', () => {
    const { container } = render(<FormFieldRenderer field={field({ isReadonly: true })} value="x" onChange={() => undefined} />)
    expect(container.querySelector('.erp-form-field')?.className).toContain('is-readonly')
  })

  it('按 dataType 渲染 text/number/date/checkbox', () => {
    expect(inputKind(field({}))).toBe('text')
    expect(inputKind(field({ dataType: 'int' }))).toBe('text')
    expect(inputKind(field({ dataType: 'datetime' }))).toBe('date')
    expect(inputKind(field({ dataType: 'bit' }))).toBe('checkbox')

    const { rerender, container } = render(<FormFieldRenderer field={field({})} value="" onChange={() => undefined} />)
    expect(container.querySelector('input')).toHaveAttribute('type', 'text')
    rerender(<FormFieldRenderer field={field({ dataType: 'money' })} value="" onChange={() => undefined} />)
    expect(container.querySelector('input')).toHaveAttribute('type', 'number')
    rerender(<FormFieldRenderer field={field({ dataType: 'date' })} value="" onChange={() => undefined} />)
    expect(container.querySelector('input')).toHaveAttribute('type', 'date')
    rerender(<FormFieldRenderer field={field({ dataType: 'bit' })} value="1" onChange={() => undefined} />)
    expect(container.querySelector('input')).toBeChecked()
  })

  it('只读与服务端填充字段禁用', () => {
    const { rerender, container } = render(<FormFieldRenderer field={field({ isReadonly: true })} value="x" onChange={() => undefined} />)
    expect(container.querySelector('input')).toBeDisabled()
    expect(screen.queryByText('由系统维护')).not.toBeInTheDocument()
    rerender(<FormFieldRenderer field={field({ serverFilled: true })} value="x" onChange={() => undefined} />)
    expect(container.querySelector('input')).toBeDisabled()
    // 服务端维护字段仅不可编辑，不再显示提示文字
    expect(screen.queryByText('由系统维护')).not.toBeInTheDocument()
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
    render(<FormFieldRenderer field={field({ choosers: [{ active: true, table: 'PRODUCT', description: null, moduleId: null, filter: null, returnMapping: 'txt_code=PRO_NO' }] })} value="" onChange={() => undefined} onChoose={onChoose} />)
    fireEvent.click(screen.getByRole('button', { name: '选择' }))
    expect(onChoose).toHaveBeenCalled()
  })

  it('无活跃数据来源时不显示选择按钮', () => {
    render(<FormFieldRenderer field={field({ choosers: [{ active: false, table: null, description: null, moduleId: null, filter: null, returnMapping: null }] })} value="" onChange={() => undefined} onChoose={vi.fn()} />)
    expect(screen.queryByRole('button', { name: '选择' })).not.toBeInTheDocument()
  })

  it('checkbox 变更映射为 1/0', () => {
    const onChange = vi.fn()
    const { container } = render(<FormFieldRenderer field={field({ dataType: 'bit' })} value="0" onChange={onChange} />)
    fireEvent.click(container.querySelector('input')!)
    expect(onChange).toHaveBeenCalledWith('1')
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
      field={field({ options: [{ value: 'O', label: 'O、外含税' }], choosers: [{ active: true, table: 'TAX', description: null, moduleId: null, filter: null, returnMapping: null }] })}
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
