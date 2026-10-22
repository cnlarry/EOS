import { render } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { ErpDataTable } from './ErpDataTable'

describe('ErpDataTable', () => {
  it('手写标准表格不领取 JS 隔行标记，交给 nth-child 兜底', () => {
    const { container } = render(
      <ErpDataTable>
        <thead>
          <tr><th>名称</th></tr>
        </thead>
        <tbody>
          <tr><td>第一行</td></tr>
          <tr><td>第二行</td></tr>
        </tbody>
      </ErpDataTable>,
    )
    const table = container.querySelector('table')!
    expect(table).toHaveClass('erp-data-table')
    // ErpTable 才会挂这个标记；手写表格由 CSS 的 tbody tr:nth-child(even) 兜底
    expect(table).not.toHaveClass('erp-table-js-striped')
  })
})
