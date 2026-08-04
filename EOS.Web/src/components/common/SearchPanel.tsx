import type { FormEvent, ReactNode } from 'react'

interface SearchPanelProps {
  keyword: string
  onKeywordChange: (value: string) => void
  onSubmit: () => void
  children?: ReactNode
}

export function SearchPanel({ keyword, onKeywordChange, onSubmit, children }: SearchPanelProps) {
  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    onSubmit()
  }

  return (
    <form className="erp-search-panel" onSubmit={handleSubmit}>
      <div className="input-icon flex-fill">
        <span className="input-icon-addon" aria-hidden="true">⌕</span>
        <input
          className="form-control"
          value={keyword}
          onChange={(event) => onKeywordChange(event.target.value)}
          placeholder="搜索订单号或供应商"
          aria-label="搜索订单号或供应商"
        />
      </div>
      {children}
      <button className="btn btn-primary" type="submit">查询</button>
    </form>
  )
}
