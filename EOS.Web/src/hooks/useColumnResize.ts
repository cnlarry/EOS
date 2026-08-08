import { useEffect, type RefObject } from 'react'

export interface ColumnResizeOptions {
  /**
   * 拖拽/双击自适应结束后是否持久化到 localStorage（storageKey 非空时默认 true）。
   * 工作台列宽已写回服务端 FIELDS.DISPLAY_LENGTH，应传 false，避免本地旧宽度覆盖服务端数值。
   */
  persist?: boolean
  /** 拖拽结束或双击自适应时回调（列键 + 新宽度），用于写回服务端字段 */
  onColumnResize?: (columnKey: string, width: number) => void
}

// 按表格元素缓存“默认列宽”（来源：表头 data-col-min-width / DISPLAY_LENGTH 或首次实测）。
// 开发模式 React StrictMode 会双挂载；用 WeakMap 跨挂载保留。
// 以列键（data-col-key）为键，避免模块切换/选择列变化导致列索引错位。
const defaultsByTable = new WeakMap<HTMLTableElement, Map<string, number>>()

/**
 * 表格列宽拖拽调整。
 *
 * - 向每个 `<th>` 注入拖拽手柄（`.erp-col-resizer`），表头需带 `data-col-key`；
 * - 通过 `<colgroup>` 控制列宽；列结构变化（选择列、模块切换、列顺序变化）自动重应用，
 *   宽度始终按列键对齐，不再依赖列索引；
 * - 默认宽度来源：`data-col-min-width`（DISPLAY_LENGTH）变化时自动跟随，否则取表头实测宽度；
 * - 宽度持久化到 `localStorage['erp-table-cols:<storageKey>']`（可选，按列键存储）；
 * - `onColumnResize` 可把拖拽结果写回服务端字段元数据。
 *
 * 注意：`tableRef` 需指向已挂载的 `<table>`；若表格是条件渲染，应使用
 * `ResizableTable` 组件（随表格一起挂载），而不是在此处持有 ref。
 */
export function useColumnResize(tableRef: RefObject<HTMLTableElement | null>, storageKey: string, options?: ColumnResizeOptions) {
  const persist = options?.persist ?? true
  const onColumnResize = options?.onColumnResize

  useEffect(() => {
    const table = tableRef.current
    if (!table || !storageKey) return
    const storeKey = `erp-table-cols:${storageKey}`
    const MIN_WIDTH = 48

    let colgroup: HTMLTableColElement | null = null
    let active: { index: number; columnKey: string; startX: number; startWidth: number; minWidth: number; dragged: boolean } | null = null
    // 双击自动适配列宽的判定：记录上一次“点击（无位移）松开”的时间与列
    let lastClick: { time: number; columnKey: string } | null = null
    const defaults = defaultsByTable.get(table) ?? (defaultsByTable.set(table, new Map<string, number>()), defaultsByTable.get(table)!)

    const thsOf = () => Array.from(table.tHead?.querySelectorAll('th') ?? [])
    const colKeys = () => thsOf().map((th, index) => th.dataset.colKey ?? String(index))
    const colCount = () => thsOf().length + 1 // +1 弹性末列

    const ensureCols = (): HTMLTableColElement[] => {
      if (!colgroup) {
        colgroup = table.querySelector('colgroup')
        if (!colgroup) {
          colgroup = document.createElement('colgroup')
          table.insertBefore(colgroup, table.tHead)
        }
      }
      const count = colCount()
      while (colgroup.children.length < count) colgroup.appendChild(document.createElement('col'))
      const cols = Array.from(colgroup.children) as HTMLTableColElement[]
      while (cols.length > count) {
        const col = cols.pop()
        if (col) colgroup.removeChild(col)
      }
      return cols
    }

    const loadWidths = (): Record<string, number> => {
      if (!persist) return {}
      try {
        const raw = localStorage.getItem(storeKey)
        if (!raw) return {}
        const parsed = JSON.parse(raw) as unknown
        if (Array.isArray(parsed)) {
          // 旧版按列索引存储：迁移为按列键
          const result: Record<string, number> = {}
          colKeys().forEach((key, index) => {
            const width = parsed[index]
            if (typeof width === 'number' && width > 0) result[key] = width
          })
          return result
        }
        if (parsed && typeof parsed === 'object') {
          return Object.fromEntries(
            Object.entries(parsed as Record<string, unknown>).filter(([, value]) => typeof value === 'number' && (value as number) > 0),
          ) as Record<string, number>
        }
        return {}
      } catch {
        return {}
      }
    }

    /**
     * 把每列宽度落到 `<col>` 上，宽度按列键对齐。
     * - 有已保存宽度则用已保存值（persist=false 时跳过，服务端为唯一来源）；
     * - 否则用默认宽度：`data-col-min-width`（DISPLAY_LENGTH）优先，其次表头实测宽度。
     *
     * 统一启用 `table-layout: fixed` + 追加一个弹性末列：
     * - fixed 布局下列宽由 `<col>` 权威决定，拖拽稳定且可自由缩小；
     * - 弹性末列吸收「填满表格」的余量，调整某列时其它列宽度被锁定。
     */
    const apply = () => {
      const ths = thsOf()
      const keys = colKeys()
      if (ths.length === 0) return

      // 清除已不存在的列键默认值（模块切换/选择列变化时防止错位）
      for (const key of [...defaults.keys()]) {
        if (!keys.includes(key)) defaults.delete(key)
      }

      ths.forEach((th, index) => {
        const key = keys[index]
        const minWidth = parseFloat(th.dataset.colMinWidth ?? '') || 0
        if (minWidth > 0) {
          // DISPLAY_LENGTH 是服务端来源：变化时跟随（列宽写回后自动更新）
          if (defaults.get(key) !== minWidth) defaults.set(key, minWidth)
        } else if (!defaults.has(key)) {
          defaults.set(key, Math.round(th.getBoundingClientRect().width) || MIN_WIDTH)
        }
      })

      if (table.style.tableLayout !== 'fixed') table.style.tableLayout = 'fixed'
      ths.forEach((th) => { th.style.minWidth = '' })

      const cols = ensureCols()
      const saved = loadWidths()
      ths.forEach((_th, index) => {
        const key = keys[index]
        const width = (typeof saved[key] === 'number' && saved[key] > 0) ? saved[key] : (defaults.get(key) ?? 0)
        if (cols[index]) cols[index].style.width = width > 0 ? `${width}px` : ''
      })
      // 弹性末列（最后一个 <col>）保持无宽度，吸收余量，锁定其它列
    }

    const attachHandles = () => {
      thsOf().forEach((th) => {
        if (th.querySelector('.erp-col-resizer')) return
        const handle = document.createElement('div')
        handle.className = 'erp-col-resizer'
        handle.title = '拖动调整列宽'
        th.appendChild(handle)
      })
    }

    /**
     * 表头内容自然宽度（不含当前列宽、不含拖拽手柄），用于计算列宽下限。
     */
    const measureHeaderContent = (th: HTMLTableCellElement): number => {
      const resizer = th.querySelector('.erp-col-resizer')
      const contentEl = Array.from(th.children).find((el) => el !== resizer) as HTMLElement | undefined
      if (contentEl) return Math.ceil(contentEl.getBoundingClientRect().width)
      const style = getComputedStyle(th)
      const span = document.createElement('span')
      span.style.cssText = 'position:absolute;visibility:hidden;white-space:nowrap;top:0;left:0;'
      span.style.fontSize = style.fontSize
      span.style.fontFamily = style.fontFamily
      span.style.fontWeight = style.fontWeight
      span.style.fontStyle = style.fontStyle
      span.style.letterSpacing = style.letterSpacing
      span.textContent = th.textContent ?? ''
      document.body.appendChild(span)
      const width = Math.ceil(span.getBoundingClientRect().width)
      span.remove()
      return width
    }

    /**
     * 单元格文本内容宽度：用隐藏 span 按单元格字体测量（返回纯内容宽，不含内边距）。
     */
    const measureCellContent = (td: HTMLTableCellElement): number => {
      const style = getComputedStyle(td)
      const span = document.createElement('span')
      span.style.cssText = 'position:absolute;visibility:hidden;white-space:nowrap;top:0;left:0;'
      span.style.fontSize = style.fontSize
      span.style.fontFamily = style.fontFamily
      span.style.fontWeight = style.fontWeight
      span.style.fontStyle = style.fontStyle
      span.style.letterSpacing = style.letterSpacing
      span.textContent = td.textContent ?? ''
      document.body.appendChild(span)
      const width = Math.ceil(span.getBoundingClientRect().width)
      span.remove()
      return width
    }

    const saveWidths = () => {
      if (!persist) return
      const ths = thsOf()
      const keys = colKeys()
      const widths: Record<string, number> = {}
      ensureCols().slice(0, ths.length).forEach((col, index) => {
        if (col.style.width) widths[keys[index]] = Number.parseInt(col.style.width, 10)
      })
      try { localStorage.setItem(storeKey, JSON.stringify(widths)) } catch { /* ignore */ }
    }

    const notify = (columnKey: string, width: number) => {
      onColumnResize?.(columnKey, Math.round(width))
    }

    /**
     * 双击手柄时自动适配列宽：取「表头内容宽度」与「当前分页该列所有单元格中
     * 最宽的一个（内容 + 内边距 + 边框）」的较大值，使最宽内容不换行。
     */
    const autoFitColumn = (index: number) => {
      const ths = thsOf()
      const th = ths[index]
      if (!th) return
      const thStyle = getComputedStyle(th)
      const thPadding = (parseFloat(thStyle.paddingLeft) || 0) + (parseFloat(thStyle.paddingRight) || 0)
      const thBorder = th.offsetWidth - th.clientWidth
      const headerWidth = measureHeaderContent(th) + thPadding + thBorder
      let dataWidth = 0
      Array.from(table.tBodies).forEach((tbody) => {
        Array.from(tbody.rows).forEach((row) => {
          const cell = row.cells[index]
          if (!cell) return
          const cellStyle = getComputedStyle(cell)
          const cellPadding = (parseFloat(cellStyle.paddingLeft) || 0) + (parseFloat(cellStyle.paddingRight) || 0)
          const cellBorder = cell.offsetWidth - cell.clientWidth
          dataWidth = Math.max(dataWidth, measureCellContent(cell) + cellPadding + cellBorder)
        })
      })
      const cols = ensureCols()
      const width = Math.max(MIN_WIDTH, headerWidth, dataWidth)
      if (cols[index]) cols[index].style.width = `${width}px`
      saveWidths()
      const key = colKeys()[index]
      if (key) notify(key, width)
    }

    apply()
    attachHandles()

    const observer = new MutationObserver(() => {
      apply()
      attachHandles()
    })
    if (table.tHead) {
      observer.observe(table.tHead, {
        childList: true,
        subtree: true,
        attributes: true,
        attributeFilter: ['data-col-key', 'data-col-min-width'],
      })
    }

    const endDrag = (event: PointerEvent) => {
      const handle = event.currentTarget as HTMLElement
      if (active) {
        const { index, columnKey, dragged } = active
        active = null
        if (dragged) {
          // 真实拖拽：持久化列宽并通知调用方
          saveWidths()
          const col = ensureCols()[index]
          if (col && col.style.width) notify(columnKey, Number.parseInt(col.style.width, 10))
          lastClick = null
        } else if (lastClick && Date.now() - lastClick.time < 500 && lastClick.columnKey === columnKey) {
          // 无位移的第二次点击（双击）：自动适配列宽
          lastClick = null
          autoFitColumn(index)
        } else {
          lastClick = { time: Date.now(), columnKey }
        }
      }
      document.body.classList.remove('erp-col-resizing')
      handle.removeEventListener('pointermove', onPointerMove)
      handle.removeEventListener('pointerup', endDrag)
      handle.removeEventListener('pointercancel', endDrag)
      try { handle.releasePointerCapture?.(event.pointerId) } catch { /* ignore */ }
    }
    const onPointerMove = (event: PointerEvent) => {
      if (!active) return
      if (Math.abs(event.clientX - active.startX) > 4) active.dragged = true
      const width = Math.max(active.minWidth, active.startWidth + (event.clientX - active.startX))
      ensureCols()[active.index].style.width = `${width}px`
    }
    const onPointerDown = (event: PointerEvent) => {
      const target = (event.target as HTMLElement).closest('.erp-col-resizer') as HTMLElement | null
      if (!target) return
      const th = target.closest('th')
      if (!th) return
      const index = thsOf().indexOf(th)
      if (index < 0) return
      event.preventDefault()
      // 以 `<col>` 的权威宽度为拖拽起点：fixed 布局下列的渲染宽会叠加“填满表格”的余量
      const cols = ensureCols()
      const colWidth = cols[index] ? Number.parseFloat(cols[index].style.width) : NaN
      const style = getComputedStyle(th)
      const padding = (parseFloat(style.paddingLeft) || 0) + (parseFloat(style.paddingRight) || 0)
      const border = th.offsetWidth - th.clientWidth
      const minWidth = Math.max(MIN_WIDTH, measureHeaderContent(th) + padding + border)
      active = {
        index,
        columnKey: colKeys()[index],
        startX: event.clientX,
        startWidth: Number.isFinite(colWidth) && colWidth > 0 ? colWidth : th.getBoundingClientRect().width,
        minWidth,
        dragged: false,
      }
      document.body.classList.add('erp-col-resizing')
      try { target.setPointerCapture(event.pointerId) } catch { /* ignore */ }
      target.addEventListener('pointermove', onPointerMove)
      target.addEventListener('pointerup', endDrag)
      target.addEventListener('pointercancel', endDrag)
    }

    table.addEventListener('pointerdown', onPointerDown)
    return () => {
      table.removeEventListener('pointerdown', onPointerDown)
      observer.disconnect()
    }
  }, [tableRef, storageKey, persist, onColumnResize])
}
