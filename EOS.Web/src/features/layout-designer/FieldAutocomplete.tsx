import { useMemo, useRef, useState } from 'react'

interface FieldAutocompleteProps {
  value: string
  suggestions: string[]
  onChange: (value: string) => void
  placeholder?: string
  rows?: number
}

/** 文本内容字段补全：输入 {{ 后弹出候选（MASTER.* / SYS.*），选择自动闭合花括号。 */
export function FieldAutocomplete({
  value, suggestions, onChange, placeholder, rows = 3,
}: FieldAutocompleteProps) {
  const ref = useRef<HTMLTextAreaElement | null>(null)
  const [candidates, setCandidates] = useState<{ prefix: string; items: string[] } | null>(null)
  const [popup, setPopup] = useState<{ top: number; left: number } | null>(null)

  const all = useMemo(
    () => [...suggestions].sort((a, b) => a.localeCompare(b)),
    [suggestions],
  )

  const detect = (text: string, caret: number) => {
    const before = text.slice(0, caret)
    const lastOpen = before.lastIndexOf('{{')
    const lastClose = before.lastIndexOf('}}')
    if (lastOpen >= 0 && lastClose <= lastOpen) {
      const typed = before.slice(lastOpen + 2)
      const items = all
        .filter((item) => item.toLowerCase().includes(typed.toLowerCase()))
        .slice(0, 20)
      return { prefix: typed, items }
    }
    return null
  }

  const handleChange = (text: string) => {
    onChange(text)
    const caret = ref.current?.selectionStart ?? text.length
    const result = detect(text, caret)
    if (result && result.items.length > 0 && ref.current) {
      const pos = ref.current.selectionStart
      const lineStart = text.lastIndexOf('\n', pos - 1) + 1
      const line = text.slice(lineStart, pos)
      const approx = ref.current.getBoundingClientRect()
      setPopup({ top: approx.top + 8, left: approx.left + 8 + line.length * 7 })
    }
    setCandidates(result && result.items.length > 0 ? result : null)
  }

  const applyCandidate = (item: string) => {
    if (!candidates || !ref.current) return
    const caret = ref.current.selectionStart
    const before = value.slice(0, caret)
    const lastOpen = before.lastIndexOf('{{')
    const next = value.slice(0, lastOpen) + `{{${item}}}` + value.slice(caret)
    onChange(next)
    setCandidates(null)
    setPopup(null)
    requestAnimationFrame(() => {
      const pos = lastOpen + item.length + 4
      ref.current?.setSelectionRange(pos, pos)
      ref.current?.focus()
    })
  }

  return (
    <div className="position-relative">
      <textarea
        ref={ref}
        className="form-control form-control-sm mt-1"
        rows={rows}
        value={value}
        placeholder={placeholder}
        onChange={(e) => handleChange(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Escape') {
            setCandidates(null)
            setPopup(null)
          }
          if (e.key === 'Tab' && candidates) {
            e.preventDefault()
            applyCandidate(candidates.items[0])
          }
          if (e.key === 'Enter' && candidates) {
            e.preventDefault()
            applyCandidate(candidates.items[0])
          }
        }}
        onBlur={() => {
          setTimeout(() => {
            setCandidates(null)
            setPopup(null)
          }, 150)
        }}
      />
      {candidates && popup && (
        <div className="position-fixed bg-white shadow border rounded overflow-auto"
          style={{ top: popup.top, left: popup.left, zIndex: 1400, maxHeight: 220, minWidth: 220 }}>
          <div className="px-2 py-1 text-secondary small border-bottom">字段引用（Tab/Enter 选择）</div>
          {candidates.items.map((item) => (
            <button
              key={item}
              type="button"
              className="d-block w-100 text-start px-2 py-1 small border-0 bg-transparent"
              style={{ cursor: 'pointer' }}
              onMouseDown={(e) => e.preventDefault()}
              onClick={() => applyCandidate(item)}
            >
              {item}
            </button>
          ))}
        </div>
      )}
    </div>
  )
}
