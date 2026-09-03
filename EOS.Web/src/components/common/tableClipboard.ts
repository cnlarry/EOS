/**
 * 表格复制到剪贴板工具：生成 TSV（可直接粘贴进 Excel）。
 */
export function rowsToTsv(headers: string[], rows: string[][]): string {
  const escape = (cell: string) =>
    cell.includes('\t') || cell.includes('\n') || cell.includes('"') ? `"${cell.replace(/"/g, '""')}"` : cell
  const line = (cells: string[]) => cells.map(escape).join('\t')
  return [line(headers), ...rows.map(line)].join('\r\n')
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
