import { useCallback, useEffect, useMemo, useRef, useState } from 'react'

/**
 * 「左导航 + 右配置」两栏设置页的**滚动联动**（scroll spy）。
 *
 * <p>
 * 右栏滚到哪一组、哪一项，左栏就高亮到哪；点左栏则平滑跳到对应分组。设置页动辄上百项参数，
 * "我在哪一组的哪一项"必须始终看得见——靠回头找就不成其为导航。
 * </p>
 *
 * <p>
 * 两页（助手设置 3105、系统参数 110111）共用这一份实现，而不是各写一遍：这里的判定线与容差是
 * 调出来的经验值，抄两份之后只有一处会被修好，另一处继续"点了没定位"，而且极难看出差别。
 * </p>
 *
 * <h3>锚点契约</h3>
 * 右侧内容里每个分组标 `data-settings-anchor="group"` + `data-group-code`，每个可定位的单项标
 * `data-settings-anchor="item"` + `data-group-code` + `data-item-label`。顶部那行"当前…"的类名是
 * `.erp-settings-split-now`，它的**实际下沿**就是判定线（见下）。
 */

/** 分组的最小形状：只要有 code 与 label 就能参与联动。 */
export interface ScrollSpySection {
  code: string
  label: string
}

/** 判定容差（px）。 */
export interface ScrollSpyOptions {
  /**
   * 判定容差（默认 2px）。点导航跳转的落点就是"节的顶端压在判定线上"，两者相差零点几像素——
   * 不宽容差时 `top <= line` 会把刚到的那一节判成"还没到"，于是左侧停在上一组、右侧标题却换了组。
   */
  tolerance?: number
  /** 分组标签（默认 `.erp-settings-split-now`）。 */
  readLineSelector?: string
}

export interface ScrollSpyResult {
  /** 挂到右侧滚动容器上。 */
  paneRef: React.RefObject<HTMLDivElement | null>
  /** 当前分组；还没滚动过时回落到第一节。 */
  activeGroupCode: string | null
  /** 当前读到的单项标签（未越过任何单项时为 null）。 */
  activeItemLabel: string | null
  /** 挂到滚动容器的 `onScroll`。 */
  onScroll: () => void
  /** 点导航：先认下这一组，再把该组滚到判定线下方。 */
  jumpTo: (code: string) => void
}

/** 下一帧执行。rAF 在测试环境可能缺席，缺席时直接执行——只是少一帧节流，结果不变。 */
function nextFrame(run: () => void) {
  if (typeof window.requestAnimationFrame !== 'function') {
    run()
    return -1
  }
  return window.requestAnimationFrame(run)
}

export function useSectionScrollSpy(
  sections: ScrollSpySection[],
  options: ScrollSpyOptions = {},
): ScrollSpyResult {
  const { tolerance = 2, readLineSelector = '.erp-settings-split-now' } = options
  const paneRef = useRef<HTMLDivElement | null>(null)
  const frameRef = useRef<number | null>(null)
  /** 程序性滚动的目标位置与冻结截止时间：期间不按判定线改选中项（见 onScroll）。 */
  const jumpRef = useRef<{ top: number; until: number } | null>(null)
  /** "滚动已停"的消抖计时器：停了才解冻，解冻这一下不重算。 */
  const settleRef = useRef<number | null>(null)
  const [active, setActive] = useState<{ group: string | null; item: string | null }>(
    { group: null, item: null })

  /**
   * 判定线取**顶部 sticky 行的实际下沿**，不写死数字。
   *
   * 写死等于把 CSS 里那行的高度抄进 JS：字体、内边距一改就错位，而且是"读数偏低/偏高"这种
   * 看不出错在哪的错位——内容明明被盖住却算"到了"，或有一截看得见却不算"到了"。
   */
  const readLineOf = useCallback((pane: HTMLElement) => {
    const bar = pane.querySelector<HTMLElement>(readLineSelector)
    return (bar ?? pane).getBoundingClientRect().bottom
  }, [readLineSelector])

  const updateFromScroll = useCallback(() => {
    const pane = paneRef.current
    if (!pane) return
    const anchors = Array.from(pane.querySelectorAll<HTMLElement>('[data-settings-anchor]'))
    const line = readLineOf(pane) + tolerance
    let group: string | null = null
    let item: string | null = null
    // 锚点按文档顺序排好，**最后一个越过判定线**的就是当前位置：
    // 用"最后一个"而不是"最近的"，是因为一行可能比判定线还高，就近取会把已经读过的项算成当前项。
    for (const anchor of anchors) {
      if (anchor.getBoundingClientRect().top > line) break
      if (anchor.dataset.settingsAnchor === 'group') {
        group = anchor.dataset.groupCode ?? group
        item = null
      } else {
        item = anchor.dataset.itemLabel ?? item
      }
    }

    // 滚到底：最后一节的标题可能永远到不了判定线（内容不够高），于是右侧明明在看最后一节、
    // 左侧却停在上一节。判据是"还能不能再滚"，而不是"滚到了某个位置"。
    const scrollable = pane.scrollHeight > pane.clientHeight + 4
    if (scrollable && pane.scrollTop + pane.clientHeight >= pane.scrollHeight - 4) {
      const groups = anchors.filter(anchor => anchor.dataset.settingsAnchor === 'group')
      const lastGroup = groups[groups.length - 1]
      if (lastGroup) {
        group = lastGroup.dataset.groupCode ?? group
        const lastItem = anchors
          .filter(anchor => anchor.dataset.settingsAnchor === 'item' && anchor.dataset.groupCode === group)
          .pop()
        item = lastItem?.dataset.itemLabel ?? null
      }
    }

    setActive(current => (current.group === group && current.item === item ? current : { group, item }))
  }, [readLineOf, tolerance])

  /**
   * 到位之后再等一拍：**没有新的 scroll 事件**了才解冻（`jumpRef` 置空）。
   * 解冻只恢复"下一次手动滚动会重新判定"，本身**不重算**——用户点的那一项因此一直保持选中。
   */
  const scheduleSettle = useCallback(() => {
    if (settleRef.current !== null) window.clearTimeout(settleRef.current)
    settleRef.current = window.setTimeout(() => {
      settleRef.current = null
      jumpRef.current = null
    }, 160)
  }, [])

  const onScroll = useCallback(() => {
    const pane = paneRef.current
    if (!pane) return
    const jump = jumpRef.current
    if (jump) {
      // 程序性滚动（点了导航）期间**不重算**：判定线此刻还压在旧位置，照算会把选中项
      // 临时切回上一组——表现就是"点了没选中，滚完才选上"。
      const arrived = Math.abs(pane.scrollTop - jump.top) <= 2
      // 目标被夹住（滚不到那一组顶着顶部）时永远到不了，按"已到边界"收尾
      const atEdge = pane.scrollTop <= 0
        || pane.scrollTop + pane.clientHeight >= pane.scrollHeight - 2
      if (!arrived && !atEdge && Date.now() < jump.until) return
      // 到位后**不重算**，而是等滚动真正停下来再解冻。只跳过一帧是不够的：动画尾段还会再来
      // 几个 scroll 事件，那时 jumpRef 已清空，判定线立刻把选中项改成"此刻压在顶上的那一组"，
      // 用户点的那一项照样被抢走（实测：点倒数第二组，最终高亮跑到了最后一组）。
      scheduleSettle()
      return
    }
    // 一帧算一次：滚动事件比帧密，逐条 getBoundingClientRect 会把长页面拖慢
    if (frameRef.current !== null) return
    frameRef.current = nextFrame(() => {
      frameRef.current = null
      updateFromScroll()
    })
  }, [scheduleSettle, updateFromScroll])

  useEffect(() => () => {
    if (frameRef.current !== null && frameRef.current > 0) cancelAnimationFrame(frameRef.current)
    if (settleRef.current !== null) window.clearTimeout(settleRef.current)
  }, [])

  const jumpTo = useCallback((code: string) => {
    const pane = paneRef.current
    // 先认下这一组：滚动是平滑的，等到滚动事件把高亮刷上来会有"点了没反应"的一瞬
    setActive({ group: code, item: null })
    if (!pane) return
    const target = pane.querySelector<HTMLElement>(
      `[data-settings-anchor="group"][data-group-code="${code}"]`)
    if (!target) return
    // 落点 = 让那一节的顶端正好贴在 sticky 行下沿（同一处分界，滚动判定不会与它打架）
    const top = Math.max(0, Math.min(
      pane.scrollTop + (target.getBoundingClientRect().top - readLineOf(pane)),
      pane.scrollHeight - pane.clientHeight))
    // 动画期间冻结滚动判定（见 onScroll）；1.2s 是兜底——平滑滚动被系统打断时不能一直冻着
    if (settleRef.current !== null) window.clearTimeout(settleRef.current)
    settleRef.current = null
    jumpRef.current = { top, until: Date.now() + 1200 }
    // jsdom 没有 Element.scrollTo：故可选调用（与仓库里 scrollIntoView 的写法同口径）
    pane.scrollTo?.({ top, behavior: 'smooth' })
  }, [readLineOf])

  const firstCode = sections[0]?.code ?? null
  const activeGroupCode = useMemo(
    () => active.group ?? firstCode,
    [active.group, firstCode])

  return { paneRef, activeGroupCode, activeItemLabel: active.item, onScroll, jumpTo }
}
