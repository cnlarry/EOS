/**
 * 表格复制/粘贴到剪贴板的工具：TSV 与 Excel 互通。
 */
export function rowsToTsv(headers: string[], rows: string[][]): string {
  const escape = (cell: string) =>
    cell.includes('\t') || cell.includes('\n') || cell.includes('"') ? `"${cell.replace(/"/g, '""')}"` : cell
  const line = (cells: string[]) => cells.map(escape).join('\t')
  return [line(headers), ...rows.map(line)].join('\r\n')
}

/** 二维数组 → TSV（区域复制用：不带表头，粘回 Excel 即一块数据）。 */
export function gridToTsv(rows: string[][]): string {
  const escape = (cell: string) =>
    cell.includes('\t') || cell.includes('\n') || cell.includes('"') ? `"${cell.replace(/"/g, '""')}"` : cell
  return rows.map(cells => cells.map(escape).join('\t')).join('\r\n')
}

/**
 * 解析剪贴板文本为二维数组（Excel 复制出来的是 TSV）。
 *
 * 必须按字符扫描而不是先 `split('\n')` 再 `split('\t')`：Excel 对含制表符/换行/引号的
 * 单元格会用双引号包裹、内部引号翻倍，先切行会把这类单元格拆坏——粘进来的数据就错位了。
 * 末尾换行不算一行（Excel 复制通常不带，手工文本常带）。
 */
export function parseClipboardGrid(text: string): string[][] {
  if (!text) return []
  const rows: string[][] = []
  let row: string[] = []
  let field = ''
  let quoted = false
  for (let index = 0; index < text.length; index += 1) {
    const char = text[index]
    if (quoted) {
      if (char === '"') {
        if (text[index + 1] === '"') {
          field += '"'
          index += 1
        } else {
          quoted = false
        }
      } else {
        field += char
      }
      continue
    }
    if (char === '"' && field === '') {
      quoted = true
      continue
    }
    if (char === '\t') {
      row.push(field)
      field = ''
      continue
    }
    if (char === '\r') continue
    if (char === '\n') {
      row.push(field)
      rows.push(row)
      row = []
      field = ''
      continue
    }
    field += char
  }
  row.push(field)
  rows.push(row)
  // 末尾空行：一行一空串（"a\n" / "a\r\n" 皆如此）
  if (rows.length > 1) {
    const last = rows[rows.length - 1]
    if (last.length === 1 && last[0] === '') rows.pop()
  }
  return rows
}

function fallbackCopy(text: string) {
  const area = document.createElement('textarea')
  area.value = text
  area.style.position = 'fixed'
  area.style.opacity = '0'
  document.body.appendChild(area)
  area.select()
  try {
    document.execCommand('copy')
  } catch {
    /* ignore */
  }
  area.remove()
}

export function writeClipboard(text: string) {
  if (navigator.clipboard?.writeText) {
    void navigator.clipboard.writeText(text).catch(() => fallbackCopy(text))
    return
  }
  fallbackCopy(text)
}
