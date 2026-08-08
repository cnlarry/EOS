import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { FormFieldDefinition } from './formDefinition'
import { FormFieldRenderer } from './FormFieldRenderer'
import { inputKind } from './formFieldKind'

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
    ...overrides,
  }
}

describe('FormFieldRenderer', () => {
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
    expect(screen.getByText('由系统维护')).toBeInTheDocument()
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
})
