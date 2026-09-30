import { Fragment, type ReactNode } from 'react'

/**
 * 助手消息的 Markdown 渲染（GFM 子集）。
 *
 * 设计取舍：**只产出 React 元素、不做任何 HTML 注入**——文本里的 `<script>`、`<img onerror=…>`
 * 一律按纯文本显示，因此不需要 sanitizer，也不引入 Markdown 依赖（与仓库"依赖极简 + 不造第二真源"一致）。
 *
 * 支持：标题、段落（段内单换行按换行渲染，贴近聊天习惯，等价 GFM 的 breaks 选项）、
 * 有序/无序列表（含缩进嵌套）、围栏代码块、行内代码、粗体/斜体/删除线、链接、引用、分隔线、
 * GFM 表格（含对齐）。**流式容错**：未闭合的 ```` ``` ```` 与只写了一半的表格行都能渲染，不抛错。
 *
 * 链接只允许 `http` / `https` / `mailto` / 站内相对路径；其余 scheme（含 `javascript:`）按纯文本显示。
 */

export interface MarkdownOptions {
  /** 行内纯文本钩子：把一段纯文本转成节点（用于把 kb:// 来源引用变成按钮）；缺省原样输出。 */
  renderText?: (text: string, key: string) => ReactNode
}

type Align = 'left' | 'center' | 'right'

const FENCE = /^```(.*)$/
const HEADING = /^(#{1,6})\s+(.*)$/
const HR = /^(?:-{3,}|\*{3,}|_{3,})$/
const QUOTE = /^>\s?(.*)$/
const UL = /^(\s*)[-*+]\s+(.*)$/
const OL = /^(\s*)(\d{1,3})[.)]\s+(.*)$/
const TABLE_ROW = /^\s*\|.*\|\s*$/
const TABLE_SEP = /^\s*\|(\s*:?-{2,}:?\s*\|)+\s*$/

const INLINE_PATTERN = /(`[^`]+`|\*\*[^*]+\*\*|__[^_]+__|~~[^~]+~~|\*[^*\n]+\*|_[^_\n]+_|\[[^\]\n]+\]\([^)\s]+\))/
const LINK_PATTERN = /^\[([^\]]+)\]\(([^)\s]+)\)$/
const SAFE_SCHEMES = ['http:', 'https:', 'mailto:', 'tel:']

/** 渲染一段 Markdown，返回可直接放进气泡的节点。 */
export function renderMarkdown(source: string, options: MarkdownOptions = {}): ReactNode {
  const lines = source.replace(/\r\n?/g, '\n').split('\n')
  // 源以换行结尾时，split 会多出一个空元素——它是切分产物而非真实空行，
  // 留着会让"流式中未闭合的代码块"末尾多一个空行。
  if (lines.length > 1 && lines[lines.length - 1] === '') lines.pop()
  return <Fragment>{renderBlocks(lines, options)}</Fragment>
}

function renderBlocks(lines: string[], options: MarkdownOptions): ReactNode[] {
  const nodes: ReactNode[] = []
  let i = 0

  while (i < lines.length) {
    const line = lines[i]

    if (!line.trim()) {
      i++
      continue
    }

    // 围栏代码块：未闭合时一直吃到末尾（流式过程中的半截代码块也照原样显示）
    const fence = FENCE.exec(line)
    if (fence) {
      const lang = fence[1].trim()
      const code: string[] = []
      i++
      while (i < lines.length && !FENCE.test(lines[i])) {
        code.push(lines[i])
        i++
      }
      if (i < lines.length) i++
      nodes.push(
        <pre key={`code-${i}`} className="erp-assistant-md-code">
          {lang ? <span className="erp-assistant-md-code-lang">{lang}</span> : null}
          <code>{code.join('\n')}</code>
        </pre>,
      )
      continue
    }

    const heading = HEADING.exec(line)
    if (heading) {
      const level = heading[1].length
      const Tag = `h${level}` as 'h1' | 'h2' | 'h3' | 'h4' | 'h5' | 'h6'
      nodes.push(
        <Tag key={`h-${i}`} className={`erp-assistant-md-heading is-h${level}`}>
          {renderInline(heading[2], options, `h${i}`)}
        </Tag>,
      )
      i++
      continue
    }

    if (HR.test(line.trim())) {
      nodes.push(<hr key={`hr-${i}`} className="erp-assistant-md-hr" />)
      i++
      continue
    }

    // 表格：表头行 + 对齐行 + 若干数据行
    if (TABLE_ROW.test(line) && i + 1 < lines.length && TABLE_SEP.test(lines[i + 1])) {
      const head = splitRow(line)
      const align = alignOf(lines[i + 1])
      i += 2
      const rows: string[][] = []
      while (i < lines.length && TABLE_ROW.test(lines[i])) {
        rows.push(splitRow(lines[i]))
        i++
      }
      nodes.push(<Fragment key={`t-${i}`}>{renderTable(head, align, rows, options, `t${i}`)}</Fragment>)
      continue
    }

    if (QUOTE.test(line)) {
      const inner: string[] = []
      while (i < lines.length && QUOTE.test(lines[i])) {
        inner.push(QUOTE.exec(lines[i])![1])
        i++
      }
      nodes.push(
        <blockquote key={`q-${i}`} className="erp-assistant-md-quote">
          {renderBlocks(inner, options)}
        </blockquote>,
      )
      continue
    }

    if (UL.test(line) || OL.test(line)) {
      const collected = collectList(lines, i)
      const tree = buildTree(collected.items)
      nodes.push(<Fragment key={`l-${i}`}>{renderListNodes(tree, options, `l${i}`)}</Fragment>)
      i = collected.next
      continue
    }

    // 段落：吃到空行或下一个块的起点
    const para: string[] = []
    while (i < lines.length && isParagraphLine(lines, i)) {
      para.push(lines[i])
      i++
    }
    nodes.push(
      <p key={`p-${i}`} className="erp-assistant-md-p">
        {para.map((text, index) => (
          <Fragment key={`p${i}-${index}`}>
            {index > 0 ? <br /> : null}
            {renderInline(text, options, `p${i}-${index}`)}
          </Fragment>
        ))}
      </p>,
    )
  }

  return nodes
}

function isParagraphLine(lines: string[], index: number): boolean {
  const line = lines[index]
  if (!line.trim()) return false
  if (FENCE.test(line) || HEADING.test(line) || HR.test(line.trim()) || QUOTE.test(line)) return false
  if (UL.test(line) || OL.test(line)) return false
  if (TABLE_ROW.test(line) && index + 1 < lines.length && TABLE_SEP.test(lines[index + 1])) return false
  return true
}

interface RawItem {
  ordered: boolean
  marker: string
  content: string
  indent: number
}

interface ListNode {
  ordered: boolean
  marker: string
  content: string
  children: ListNode[]
}

/** 收集一个列表块（含缩进更深的续行）；空行后若仍是列表行则视为同一块。 */
function collectList(lines: string[], start: number): { items: RawItem[]; next: number } {
  const items: RawItem[] = []
  let i = start

  while (i < lines.length) {
    const line = lines[i]
    if (!line.trim()) {
      if (i + 1 < lines.length && (UL.test(lines[i + 1]) || OL.test(lines[i + 1]))) {
        i++
        continue
      }
      break
    }

    const ul = UL.exec(line)
    const ol = OL.exec(line)
    if (ul) {
      items.push({ ordered: false, marker: '-', content: ul[2], indent: ul[1].length })
    } else if (ol) {
      items.push({ ordered: true, marker: `${ol[2]}.`, content: ol[3], indent: ol[1].length })
    } else if (items.length > 0) {
      const last = items[items.length - 1]
      last.content += `\n${line.trim()}`
    } else {
      break
    }
    i++
  }

  return { items, next: i }
}

/** 按缩进把扁平列表项折成树：缩进更深的项归入上一项的子级。 */
function buildTree(items: RawItem[]): ListNode[] {
  if (items.length === 0) return []
  const base = Math.min(...items.map(item => item.indent))
  const nodes: ListNode[] = []
  let i = 0

  while (i < items.length) {
    if (items[i].indent > base) {
      const nested: RawItem[] = []
      while (i < items.length && items[i].indent > base) {
        nested.push(items[i])
        i++
      }
      const parent = nodes[nodes.length - 1]
      if (parent) parent.children.push(...buildTree(nested))
      continue
    }
    const item = items[i]
    nodes.push({ ordered: item.ordered, marker: item.marker, content: item.content, children: [] })
    i++
  }

  return nodes
}

function renderListNodes(nodes: ListNode[], options: MarkdownOptions, keyPrefix: string): ReactNode {
  if (nodes.length === 0) return null
  const ordered = nodes[0].ordered
  const items = nodes.map((node, index) => (
    <li key={`${keyPrefix}-${index}`} className="erp-assistant-md-li">
      {renderInline(node.content, options, `${keyPrefix}-${index}`)}
      {node.children.length > 0 ? renderListNodes(node.children, options, `${keyPrefix}-${index}c`) : null}
    </li>
  ))
  return ordered
    ? <ol className="erp-assistant-md-list is-ordered">{items}</ol>
    : <ul className="erp-assistant-md-list">{items}</ul>
}

/** 表格横向可滚：列多时先把抽屉撑破之前滚动，也不丢内容。 */
function renderTable(head: string[], align: Align[], rows: string[][], options: MarkdownOptions, id: string): ReactNode {
  return (
    <div className="erp-assistant-md-table-wrap">
      <table className="erp-assistant-md-table">
        <thead>
          <tr>
            {head.map((cell, index) => (
              <th key={`${id}-h${index}`} style={{ textAlign: align[index] ?? 'left' }}>
                {renderInline(cell, options, `${id}-h${index}`)}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((row, rowIndex) => (
            <tr key={`${id}-r${rowIndex}`}>
              {head.map((_, cellIndex) => (
                <td key={`${id}-r${rowIndex}c${cellIndex}`} style={{ textAlign: align[cellIndex] ?? 'left' }}>
                  {renderInline(row[cellIndex] ?? '', options, `${id}-r${rowIndex}c${cellIndex}`)}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function splitRow(line: string): string[] {
  const trimmed = line.trim().replace(/^\|/, '').replace(/\|$/, '')
  return trimmed.split('|').map(cell => cell.trim())
}

function alignOf(separator: string): Align[] {
  return splitRow(separator).map(cell => {
    const left = cell.startsWith(':')
    const right = cell.endsWith(':')
    if (left && right) return 'center'
    if (right) return 'right'
    return 'left'
  })
}

function renderInline(text: string, options: MarkdownOptions, keyPrefix: string): ReactNode[] {
  return text
    .split(INLINE_PATTERN)
    .filter(part => part !== '')
    .map((part, index) => renderInlinePart(part, options, `${keyPrefix}-${index}`))
}

function renderInlinePart(part: string, options: MarkdownOptions, key: string): ReactNode {
  if (part.length > 2 && part.startsWith('`') && part.endsWith('`')) {
    return <code key={key} className="erp-assistant-md-inline-code">{part.slice(1, -1)}</code>
  }
  if (part.length > 4 && part.startsWith('**') && part.endsWith('**')) {
    return <strong key={key}>{renderInline(part.slice(2, -2), options, key)}</strong>
  }
  if (part.length > 4 && part.startsWith('__') && part.endsWith('__')) {
    return <strong key={key}>{renderInline(part.slice(2, -2), options, key)}</strong>
  }
  if (part.length > 4 && part.startsWith('~~') && part.endsWith('~~')) {
    return <del key={key}>{renderInline(part.slice(2, -2), options, key)}</del>
  }
  if (part.length > 2 && ((part.startsWith('*') && part.endsWith('*')) || (part.startsWith('_') && part.endsWith('_')))) {
    return <em key={key}>{renderInline(part.slice(1, -1), options, key)}</em>
  }

  const link = LINK_PATTERN.exec(part)
  if (link) {
    const href = link[2]
    if (isSafeHref(href)) {
      return (
        <a key={key} className="erp-assistant-md-link" href={href}
          target="_blank" rel="noopener noreferrer">
          {renderInline(link[1], options, key)}
        </a>
      )
    }
    return <Fragment key={key}>{part}</Fragment>
  }

  return <Fragment key={key}>{(options.renderText ?? ((value: string) => value))(part, key)}</Fragment>
}

function isSafeHref(href: string): boolean {
  const lower = href.toLowerCase()
  if (lower.startsWith('/') || lower.startsWith('#')) return true
  return SAFE_SCHEMES.some(scheme => lower.startsWith(scheme))
}
