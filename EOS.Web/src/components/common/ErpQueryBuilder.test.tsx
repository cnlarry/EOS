import { fireEvent, render, screen } from '@testing-library/react'
import { useState } from 'react'
import { describe, expect, it, vi } from 'vitest'
import { emptyQueryCondition, ErpQueryBuilder, type QueryCondition } from './ErpQueryBuilder'

const fields = [
  { key: 'APPLY_NO', label: '申请单号' },
  { key: 'APPLY_DATE', label: '申请日期' },
]

function Harness({ onApply = vi.fn(), onClear = vi.fn(), onClose = vi.fn() }) {
  const [conditions, setConditions] = useState<QueryCondition[]>([emptyQueryCondition()])
  return (
    <ErpQueryBuilder
      open
      fields={fields}
      conditions={conditions}
      onChange={setConditions}
      onApply={onApply}
      onClear={onClear}
      onClose={onClose}
    />
  )
}

describe('ErpQueryBuilder', () => {
  it('渲染字段白名单与单个条件行', () => {
    render(<Harness />)
    const fieldSelect = screen.getByLabelText('条件1字段')
    expect(Array.from(fieldSelect.querySelectorAll('option')).map((option) => option.textContent)).toEqual([
      '选择字段', '申请单号', '申请日期',
    ])
    expect(screen.getByLabelText('条件1运算符')).toBeInTheDocument()
    expect(screen.getByLabelText('条件1值')).toBeInTheDocument()
  })

  it('添加条件新增一行并显示逻辑关系', () => {
    render(<Harness />)
    fireEvent.click(screen.getByRole('button', { name: '添加条件' }))
    expect(screen.getByLabelText('条件1字段')).toBeInTheDocument()
    expect(screen.getByLabelText('条件2字段')).toBeInTheDocument()
    expect(screen.getByLabelText('条件2逻辑')).toHaveValue('and')
  })

  it('单行时删除禁用，多行可删除', () => {
    render(<Harness />)
    expect(screen.getByRole('button', { name: '删除' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: '添加条件' }))
    fireEvent.click(screen.getAllByRole('button', { name: '删除' })[0])
    expect(screen.queryByLabelText('条件2字段')).not.toBeInTheDocument()
  })

  it('empty/notempty 运算符禁用值输入，between 显示值上限', () => {
    render(<Harness />)
    const operator = screen.getByLabelText('条件1运算符')
    fireEvent.change(operator, { target: { value: 'empty' } })
    expect(screen.getByLabelText('条件1值')).toBeDisabled()
    expect(screen.queryByLabelText('条件1值上限')).not.toBeInTheDocument()
    fireEvent.change(operator, { target: { value: 'between' } })
    expect(screen.getByLabelText('条件1值')).toBeEnabled()
    expect(screen.getByLabelText('条件1值上限')).toBeInTheDocument()
  })

  it('未选择字段时应用查询禁用，选择后可应用', () => {
    const onApply = vi.fn()
    render(<Harness onApply={onApply} />)
    expect(screen.getByRole('button', { name: '应用查询' })).toBeDisabled()
    fireEvent.change(screen.getByLabelText('条件1字段'), { target: { value: 'APPLY_NO' } })
    expect(screen.getByRole('button', { name: '应用查询' })).toBeEnabled()
    fireEvent.click(screen.getByRole('button', { name: '应用查询' }))
    expect(onApply).toHaveBeenCalledTimes(1)
  })

  it('清空与取消触发对应回调', () => {
    const onClear = vi.fn()
    const onClose = vi.fn()
    render(<Harness onClear={onClear} onClose={onClose} />)
    fireEvent.click(screen.getByRole('button', { name: '清空' }))
    expect(onClear).toHaveBeenCalledTimes(1)
    fireEvent.click(screen.getByLabelText('关闭'))
    expect(onClose).toHaveBeenCalledTimes(1)
  })
})
