import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { renderMarkdown, type MarkdownOptions } from './markdown'

function renderMd(source: string, options?: MarkdownOptions) {
  return render(<div data-testid="root">{renderMarkdown(source, options)}</div>)
}

describe('助手消息的 Markdown 渲染', () => {
  it('段落按段呈现，段内单换行渲染成换行（聊天习惯，等价 GFM breaks）', () => {
    const { container } = renderMd('第一句\n第二句\n\n新的一段')
    const paragraphs = container.querySelectorAll('p.erp-assistant-md-p')
    expect(paragraphs).toHaveLength(2)
    expect(paragraphs[0].querySelectorAll('br')).toHaveLength(1)
    expect(paragraphs[0].textContent).toBe('第一句第二句')
  })

  it('标题按层级渲染', () => {
    const { container } = renderMd('## 二级标题\n\n### 三级标题')
    expect(container.querySelector('h2.erp-assistant-md-heading.is-h2')?.textContent).toBe('二级标题')
    expect(container.querySelector('h3.erp-assistant-md-heading.is-h3')?.textContent).toBe('三级标题')
  })

  it('无序与有序列表都能渲染，且按缩进折成嵌套层级', () => {
    const { container } = renderMd('- 一级\n  - 二级\n    - 三级\n- 同级第二项')
    const top = container.querySelector('ul.erp-assistant-md-list')
    expect(top?.children).toHaveLength(2)
    const nested = top?.children[0].querySelector('ul')
    expect(nested?.children).toHaveLength(1)
    expect(nested?.querySelector('ul')?.textContent).toBe('三级')

    const ordered = renderMd('1. 甲\n2. 乙')
    expect(ordered.container.querySelectorAll('ol.erp-assistant-md-list > li')).toHaveLength(2)
  })

  it('列表项的续行并入同一项，不会被拆成新段落', () => {
    const { container } = renderMd('- 第一行\n  续行内容')
    const items = container.querySelectorAll('li')
    expect(items).toHaveLength(1)
    expect(items[0].textContent).toContain('续行内容')
  })

  it('GFM 表格渲染成带表头与对齐的表格', () => {
    const { container } = renderMd('| 料号 | 数量 |\n|:--|--:|\n| A001 | 12 |\n| A002 | 3 |')
    const table = container.querySelector('table.erp-assistant-md-table')
    expect(table).not.toBeNull()
    const headers = container.querySelectorAll('th')
    expect(headers).toHaveLength(2)
    expect(headers[0].style.textAlign).toBe('left')
    expect(headers[1].style.textAlign).toBe('right')
    expect(container.querySelectorAll('tbody tr')).toHaveLength(2)
    // 数据行补空格子也不能崩：少一列时按空串渲染
    const ragged = renderMd('| a | b |\n|---|---|\n| 只有一列 |')
    expect(ragged.container.querySelectorAll('tbody td')).toHaveLength(2)
  })

  it('围栏代码块保留原文，并给出语言标签；未闭合时也照原样显示（流式容错）', () => {
    const { container } = renderMd('```ts\nconst a = 1\n```')
    const pre = container.querySelector('pre.erp-assistant-md-code')
    expect(pre?.querySelector('.erp-assistant-md-code-lang')?.textContent).toBe('ts')
    expect(pre?.querySelector('code')?.textContent).toBe('const a = 1')

    const streaming = renderMd('```sql\nSELECT 1\n')
    expect(streaming.container.querySelector('pre.erp-assistant-md-code code')?.textContent).toBe('SELECT 1')
  })

  it('行内语法：代码、粗体、斜体、删除线', () => {
    const { container } = renderMd('`code` 与 **粗** 与 *斜* 与 ~~删~~')
    expect(container.querySelector('code.erp-assistant-md-inline-code')?.textContent).toBe('code')
    expect(container.querySelector('strong')?.textContent).toBe('粗')
    expect(container.querySelector('em')?.textContent).toBe('斜')
    expect(container.querySelector('del')?.textContent).toBe('删')
  })

  it('链接只放行安全 scheme，其它 scheme 按纯文本显示', () => {
    const { container } = renderMd('[文档](https://example.com/a) 和 [点我](javascript:steal)')
    const anchor = container.querySelector('a.erp-assistant-md-link')
    expect(anchor?.getAttribute('href')).toBe('https://example.com/a')
    expect(anchor?.getAttribute('rel')).toBe('noopener noreferrer')
    expect(container.querySelectorAll('a')).toHaveLength(1)
    expect(container.textContent).toContain('[点我](javascript:steal)')
  })

  it('不注入 HTML：标签按纯文本显示，不会变成元素', () => {
    const { container } = renderMd('<img src=x onerror=alert(1)> 与 <script>alert(2)</script>')
    expect(container.querySelector('img')).toBeNull()
    expect(container.querySelector('script')).toBeNull()
    expect(container.textContent).toContain('<img src=x onerror=alert(1)>')
  })

  it('引用与分隔线', () => {
    const { container } = renderMd('> 引用第一行\n> 引用第二行\n\n---\n\n正文')
    expect(container.querySelector('blockquote.erp-assistant-md-quote')?.textContent).toBe('引用第一行引用第二行')
    expect(container.querySelector('hr.erp-assistant-md-hr')).not.toBeNull()
  })

  it('renderText 钩子接管行内纯文本，且深入粗体等内层（粗体里的来源引用同样能变按钮）', () => {
    const options: MarkdownOptions = {
      renderText: segment => <span data-testid="seg">{segment.toUpperCase()}</span>,
    }
    const { container } = renderMd('看 **这里** 的来源', options)
    expect(screen.getAllByTestId('seg').map(node => node.textContent)).toEqual(['看 ', '这里', ' 的来源'])
    expect(container.querySelector('strong')?.textContent).toBe('这里')
  })

  it('空文本不产生任何节点', () => {
    const { container } = renderMd('')
    expect(container.querySelector('[data-testid="root"]')?.childNodes).toHaveLength(0)
  })
})
