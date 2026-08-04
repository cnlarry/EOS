import { useEffect, type RefObject } from 'react'

// 按表格元素缓存“初始默认宽度”（来自表头 min-width / DISPLAY_LENGTH）。
// 开发模式 React StrictMode 会双挂载：第一次 apply 会剥离表头 min-width，
// 第二次挂载时若重新捕获就会丢失来源，导致默认宽度失效。用 WeakMap 跨挂载保留。
const defaultsByTable = new WeakMap<HTMLTableElement, (number | null)[]>()

/**
 * 表格列宽拖拽调整（纯前端，结果存 localStorage，不写数据库）。
 *
 * - 向每个 `<th>` 注入拖拽手柄（`.erp-col-resizer`）；
 * - 通过 `<colgroup>` 控制列宽；列结构变化（如 DocumentWorkbench 的「选择列」）自动重应用；
 * - 宽度按 `localStorage['erp-table-cols:<storageKey>']` 持久化，按用户与表格隔离；
 * - 带 `min-width` 表头（DocumentWorkbench 主表的 DISPLAY_LENGTH）的表格会切换
 *   `table-layout: fixed` 并追加一个弹性末列，使列宽可自由缩小、且调整某列时其它列保持锁定。
 *
 * 注意：`tableRef` 需指向已挂载的 `<table>`；若表格是条件渲染，应使用
 * `ResizableTable` 组件（随表格一起挂载），而不是在此处持有 ref。
 */
export function useColumnResize(tableRef: RefObject<HTMLTableElement | null>, storageKey: string) {
  useEffect(() => {
    const table = tableRef.current
    if (!table || !storageKey) return
    const storeKey = `erp-table-cols:${storageKey}`
    const MIN_WIDTH = 48

    let colgroup: HTMLTableColElement | null = null
    let active: { index: number; startX: number; startWidth: number; minWidth: number; dragged: boolean } | null = null
    // 双击自动适配列宽的判定：记录上一次“点击（无位移）松开”的时间与列
    let lastClick: { time: number; index: number } | null = null
    const defaults = defaultsByTable.get(table) ?? (defaultsByTable.set(table, []), defaultsByTable.get(table)!)

    const thCount = () => table.tHead?.querySelector('tr')?.children.length ?? 0
    const colCount = () => thCount() + (table.style.tableLayout === 'fixed' ? 1 : 0)

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

    const loadWidths = (): (number | null)[] => {
      try {
        const raw = localStorage.getItem(storeKey)
        if (!raw) return []
        const parsed = JSON.parse(raw) as unknown
        return Array.isArray(parsed) ? parsed.map((value) => typeof value === 'number' ? value : null) : []
      } catch {
        return []
      }
    }

    /**
     * 把每列宽度落到 `<col>` 上。
     *
     * - 有已保存宽度则用已保存值；
     * - 否则用默认宽度（有 `min-width`/DISPLAY_LENGTH 用其值，否则用表头实测宽度）。
     *
     * 统一启用 `table-layout: fixed` + 追加一个弹性末列：
     * - fixed 布局下列宽由 `<col>` 权威决定，拖拽稳定且可自由缩小（auto 布局下单元格
     *   `min-width` 会压制 `<col>` 宽度导致拖拽无效、也无法缩小）；
     * - 弹性末列（无宽度）吸收「填满表格」的余量，使调整某列时其它列宽度被锁定，
     *   不再被余量再分配挤动（主表、子表、字段维护等所有表格统一生效）。
     */
    const apply = () => {
      const saved = loadWidths()
      const ths = Array.from(table.tHead?.querySelectorAll('th') ?? [])

      ths.forEach((th, index) => {
        if (defaults[index] !== undefined) return
        const minWidth = parseFloat(th.style.minWidth) || 0
        defaults[index] = minWidth > 0 ? minWidth : Math.round(th.getBoundingClientRect().width) || null
      })

      if (table.style.tableLayout !== 'fixed') table.style.tableLayout = 'fixed'
      ths.forEach((th) => { th.style.minWidth = '' })

      const cols = ensureCols()
      ths.forEach((_th, index) => {
        const width = typeof saved[index] === 'number' && saved[index] > 0
          ? saved[index]
          : (defaults[index] ?? 0)
        if (cols[index]) cols[index].style.width = width > 0 ? `${width}px` : ''
      })
      // 弹性末列（最后一个 <col>）保持无宽度，吸收余量，锁定其它列
    }

    const attachHandles = () => {
      Array.from(table.tHead?.querySelectorAll('th') ?? []).forEach((th, index) => {
        th.style.position = 'relative'
        if (th.querySelector('.erp-col-resizer')) return
        const handle = document.createElement('div')
        handle.className = 'erp-col-resizer'
        handle.dataset.col = String(index)
        handle.title = '拖动调整列宽'
        th.appendChild(handle)
      })
    }

    /**
     * 表头内容自然宽度（不含当前列宽、不含拖拽手柄），用于计算列宽下限：
     * - 有内容元素（如 DocumentWorkbench 的排序按钮）→ 测该元素宽度；
     * - 纯文字表头 → 用隐藏 span 按当前字体测量文字宽度。
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
     * 不能用 td.scrollWidth——在 table-layout:fixed 下它会被单元格宽度钳制而低估
     * （实测内容 230px+内边距 10px 需 240px，scrollWidth 只给 234px），导致自动适配后
     * 最宽单元格仍换行。
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

    /**
     * 双击手柄时自动适配列宽：取「表头内容宽度」与「当前分页该列所有单元格中
     * 最宽的一个（内容 + 内边距 + 边框）」的较大值，使最宽内容不换行，并持久化。
     */
    const autoFitColumn = (index: number) => {
      const ths = Array.from(table.tHead?.querySelectorAll('th') ?? [])
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
    }

    apply()
    attachHandles()

    const observer = new MutationObserver(() => {
      apply()
      attachHandles()
    })
    if (table.tHead) observer.observe(table.tHead, { childList: true, subtree: true })

    const saveWidths = () => {
      const ths = thCount()
      const widths = ensureCols().slice(0, ths).map((col) => col.style.width ? Number.parseInt(col.style.width, 10) : null)
      try { localStorage.setItem(storeKey, JSON.stringify(widths)) } catch { /* ignore */ }
    }

    const endDrag = (event: PointerEvent) => {
      const handle = event.currentTarget as HTMLElement
      if (active) {
        const { index, dragged } = active
        active = null
        if (dragged) {
          // 真实拖拽：持久化列宽
          saveWidths()
          lastClick = null
        } else if (lastClick && Date.now() - lastClick.time < 500 && lastClick.index === index) {
          // 无位移的第二次点击（双击）：自动适配列宽
          lastClick = null
          autoFitColumn(index)
        } else {
          lastClick = { time: Date.now(), index }
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
      const index = Number(target.dataset.col)
      const th = target.closest('th')
      if (!th) return
      event.preventDefault()
      // 以 `<col>` 的权威宽度为拖拽起点：fixed 布局下列的渲染宽会叠加“填满表格”的余量，
      // 若按渲染宽起手，首次移动会把余量再算进去导致列宽瞬间跳变。
      // auto 布局（字段维护、子表）的 `<col>` 可能为空，回退到表头实测宽度。
      const cols = ensureCols()
      const colWidth = cols[index] ? Number.parseFloat(cols[index].style.width) : NaN
      // 该列最小宽度 = 表头内容自然宽度（排序按钮/文字）+ 左右内边距 + 边框。
      // 不能直接用 th.scrollWidth：它只会 >= 当前列宽（未溢出时等于当前宽），
      // 且会被拖拽手柄悬出部分撑大，导致“无法调小、左拖反而变大”。
      const style = getComputedStyle(th)
      const padding = (parseFloat(style.paddingLeft) || 0) + (parseFloat(style.paddingRight) || 0)
      const border = th.offsetWidth - th.clientWidth
      const minWidth = Math.max(MIN_WIDTH, measureHeaderContent(th) + padding + border)
      active = {
        index,
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
  }, [tableRef, storageKey])
}
