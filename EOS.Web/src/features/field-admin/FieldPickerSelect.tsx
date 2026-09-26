import { useEffect, useRef, useState } from 'react'

export interface FieldPickerOption {
  value: string
  label: string
  /** 选项右侧补充信息（列类型 / 函数取值说明）。 */
  meta?: string
}

/**
 * 轻量下拉：主文本（名称）+ 右侧补充信息，原生 select 不支持富文本故自定义。
 * 键盘：按钮上 ↑/↓ 打开，列表内 ↑/↓ 移动、Esc 关闭并回到按钮；失焦即关闭。
 */
export function FieldPickerSelect({ options, value, onChange, placeholder, ariaLabel, disabled = false }: {
  options: FieldPickerOption[]
  value: string
  onChange: (value: string) => void
  placeholder: string
  ariaLabel: string
  disabled?: boolean
}) {
  const [open, setOpen] = useState(false)
  const containerRef = useRef<HTMLDivElement | null>(null)
  const selected = options.find(option => option.value === value)
  const close = () => setOpen(false)

  // 打开时聚焦当前选中项（无选中聚焦第一项），支持方向键在选项间移动
  useEffect(() => {
    if (!open) return
    const options = containerRef.current?.querySelectorAll<HTMLButtonElement>('[role="option"]')
    if (!options || options.length === 0) return
    const currentIndex = Array.from(options).findIndex(option => option.classList.contains('bg-primary-lt'))
    options[Math.max(currentIndex, 0)]?.focus()
  }, [open])

  const moveOptionFocus = (event: React.KeyboardEvent) => {
    const options = Array.from(event.currentTarget.querySelectorAll<HTMLButtonElement>('[role="option"]'))
    const current = options.indexOf(document.activeElement as HTMLButtonElement)
    const next = event.key === 'ArrowDown' ? Math.min(current + 1, options.length - 1) : Math.max(current - 1, 0)
    options[next]?.focus()
  }

  return (
    <div
      className="position-relative flex-grow-1"
      ref={containerRef}
      onBlur={event => {
        const next = event.relatedTarget as Node | null
        if (!next || !event.currentTarget.contains(next)) close()
      }}
    >
      <button
        type="button"
        className="form-select form-select-sm text-start"
        aria-label={ariaLabel}
        aria-expanded={open}
        aria-haspopup="listbox"
        disabled={disabled}
        onClick={() => setOpen(current => !current)}
        onKeyDown={event => {
          if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            event.preventDefault()
            setOpen(true)
          } else if (event.key === 'Escape') {
            close()
          }
        }}
      >
        {selected
          ? <><span className="text-truncate d-inline-block align-middle" style={{ maxWidth: 'calc(100% - 70px)' }}>{selected.label}</span><span className="text-secondary small ms-1">{selected.meta}</span></>
          : <span className="text-secondary">{placeholder}</span>}
      </button>
      {open && (
        <div
          className="position-absolute top-100 start-0 w-100 border rounded bg-white shadow-sm z-3"
          role="listbox"
          aria-label={ariaLabel}
          style={{ maxHeight: 240, overflowY: 'auto' }}
          onKeyDown={event => {
            if (event.key === 'Escape') {
              event.stopPropagation()
              close()
              containerRef.current?.querySelector<HTMLButtonElement>('button.form-select')?.focus()
            } else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
              event.preventDefault()
              moveOptionFocus(event)
            }
          }}
        >
          <button type="button" role="option" className="d-block w-100 text-start px-2 py-1 border-0 bg-transparent text-secondary" onClick={() => { onChange(''); close() }}>{placeholder}</button>
          {options.map(option => (
            <button
              key={option.value}
              type="button"
              role="option"
              className={`erp-picker-option d-flex w-100 align-items-center justify-content-between px-2 py-1 border-0 bg-transparent ${option.value === value ? 'bg-primary-lt' : ''}`}
              onClick={() => { onChange(option.value); close() }}
            >
              <span className="text-truncate">{option.label}</span>
              {option.meta && <span className="text-secondary small ms-2">{option.meta}</span>}
            </button>
          ))}
        </div>
      )}
    </div>
  )
}
