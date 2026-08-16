import { IconSearch, IconX } from '@tabler/icons-react'
import { useCallback, useEffect, useRef, useState } from 'react'

interface ErpSearchBoxProps {
  value: string
  onChange: (value: string) => void
  placeholder?: string
  ariaLabel?: string
  /** 防抖毫秒数；0 表示输入即触发 */
  debounceMs?: number
  /** 是否支持 Ctrl/Cmd+K 快捷聚焦（默认开启） */
  shortcut?: boolean
}

/**
 * 标准 ERP 列表全局搜索框（`.erp-search.erp-list-global-search`）。
 *
 * - 受控组件，内部维护输入草稿；防抖后把 trim 后的值提交给 onChange；
 * - 回车立即提交、非空时显示清除按钮、Ctrl/Cmd+K 或 / 聚焦并全选、Esc 清空或失焦；
 * - 提交值由页面写入 URL/Query Key，本组件不感知业务。
 */
export function ErpSearchBox({
  value,
  onChange,
  placeholder = '搜索…',
  ariaLabel = '搜索',
  debounceMs = 0,
  shortcut = true,
}: ErpSearchBoxProps) {
  const inputRef = useRef<HTMLInputElement>(null)
  const [draft, setDraft] = useState(value)
  // 记录最后一次提交给父级的值，避免父级回写 value 时把输入中的草稿冲掉
  const lastEmitted = useRef(value)

  const emit = useCallback((next: string) => {
    const normalized = next.trim()
    lastEmitted.current = normalized
    onChange(normalized)
  }, [onChange])

  useEffect(() => {
    if (value !== lastEmitted.current) {
      lastEmitted.current = value
      setDraft(value)
    }
  }, [value])

  useEffect(() => {
    if (debounceMs <= 0) return
    const timer = window.setTimeout(() => {
      if (draft.trim() !== value) emit(draft)
    }, debounceMs)
    return () => window.clearTimeout(timer)
  }, [draft, debounceMs, emit, value])

  useEffect(() => {
    if (!shortcut) return
    const handler = (event: globalThis.KeyboardEvent) => {
      if (event.key === '/' && !isEditableTarget(event.target)) {
        event.preventDefault()
        inputRef.current?.focus()
        inputRef.current?.select()
        return
      }
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault()
        inputRef.current?.focus()
        inputRef.current?.select()
      }
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [shortcut])

  return (
    <div className="erp-search erp-list-global-search">
      <IconSearch size={18} aria-hidden="true" />
      <input
        ref={inputRef}
        aria-label={ariaLabel}
        placeholder={placeholder}
        type="search"
        value={draft}
        onChange={(event) => {
          setDraft(event.target.value)
          if (debounceMs <= 0) emit(event.target.value)
        }}
        onKeyDown={(event) => {
          if (event.key === 'Enter') emit(draft)
          if (event.key === 'Escape') {
            if (draft) {
              setDraft('')
              emit('')
            } else {
              event.currentTarget.blur()
            }
          }
        }}
      />
      {draft && (
        <button
          type="button"
          className="btn btn-ghost-danger btn-sm p-0 border-0"
          aria-label="清除搜索"
          onClick={() => {
            setDraft('')
            emit('')
          }}
        >
          <IconX size={14} />
        </button>
      )}
      {shortcut && <kbd>Ctrl K</kbd>}
    </div>
  )
}

function isEditableTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false
  const tag = target.tagName
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || target.isContentEditable
}
