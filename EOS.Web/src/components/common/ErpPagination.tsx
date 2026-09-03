import { useEffect, useState } from 'react'
import { Button } from '../ui/Button'

interface ErpPaginationProps {
  total: number
  page: number
  pageSize: number
  onPageChange: (page: number) => void
  onPageSizeChange?: (size: number) => void
  pageSizes?: number[]
  /** 页码输入直接跳转 */
  showQuickJumper?: boolean
}

/**
 * 标准 ERP 列表分页脚（`.erp-pagination-footer` 内容）。
 *
 * 统一承载「共 N 条，第 X/Y 页」、每页条数、页码输入跳转与首/上/下/尾页按钮；
 * 页码为 1-based，由页面持有并写入 URL/Query Key。
 */
export function ErpPagination({
  total,
  page,
  pageSize,
  onPageChange,
  onPageSizeChange,
  pageSizes,
  showQuickJumper = true,
}: ErpPaginationProps) {
  const totalPages = Math.max(1, Math.ceil(total / pageSize))
  const [jump, setJump] = useState('')

  useEffect(() => {
    setJump('')
  }, [page])

  const jumpTo = () => {
    const target = Number.parseInt(jump, 10)
    if (Number.isInteger(target) && target > 0) {
      onPageChange(Math.min(target, totalPages))
      setJump('')
    }
  }

  return (
    <>
      <div className="text-secondary small">共 {total} 条，第 {page}/{totalPages} 页</div>
      {pageSizes && onPageSizeChange && (
        <select
          className="form-select form-select-sm erp-page-size"
          value={pageSize}
          aria-label="每页数量"
          onChange={(event) => onPageSizeChange(Number(event.target.value))}
        >
          {pageSizes.map((size) => (
            <option key={size} value={size}>
              每页 {size} 条
            </option>
          ))}
        </select>
      )}
      {showQuickJumper && (
        <input
          className="form-control form-control-sm erp-page-jump"
          aria-label="跳转到页码"
          placeholder="页码"
          inputMode="numeric"
          value={jump}
          onChange={(event) => setJump(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === 'Enter') jumpTo()
          }}
        />
      )}
      <div className="btn-group">
        <Button size="sm" disabled={page <= 1} onClick={() => onPageChange(1)}>
          首页
        </Button>
        <Button size="sm" disabled={page <= 1} onClick={() => onPageChange(page - 1)}>
          上一页
        </Button>
        <Button size="sm" disabled={page >= totalPages} onClick={() => onPageChange(page + 1)}>
          下一页
        </Button>
        <Button size="sm" disabled={page >= totalPages} onClick={() => onPageChange(totalPages)}>
          尾页
        </Button>
      </div>
    </>
  )
}
