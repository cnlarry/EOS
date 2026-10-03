import { useLayoutEffect, useRef, useState, type RefObject } from 'react'

/** 浮层与视口边缘的最小留白。 */
const VIEWPORT_MARGIN = 8

export interface MenuPlacement {
  /** 挂到菜单根元素上：量测真实尺寸用 */
  ref: RefObject<HTMLDivElement | null>
  /** 定稿后的 left（fixed 定位）；量测完成前回落到期望位置 */
  left: number
  /** 定稿后的 top（fixed 定位）；量测完成前回落到期望位置 */
  top: number
}

/**
 * 自定义右键/浮层菜单的视口定位。
 *
 * 期望位置（通常是鼠标落点或锚点，fixed 坐标）默认向右下展开；右/下越界时向内收，
 * 下方放不下且上方更宽裕时**整体翻到锚点上方**——菜单贴在屏幕底部时不再被窗口裁掉。
 *
 * 翻转要按真实尺寸判断，故用 useLayoutEffect 在同一帧内量测并校正：首帧即定稿，不会先闪一下错位。
 * 菜单用 `{open && <div ref={placement.ref} style={{ left: placement.left, top: placement.top }} />}` 渲染；
 * open 为 false 时不量测，返回期望位置。
 */
export function useMenuPlacement(x: number, y: number, open: boolean): MenuPlacement {
  const ref = useRef<HTMLDivElement | null>(null)
  const [placed, setPlaced] = useState<{ left: number; top: number } | null>(null)

  useLayoutEffect(() => {
    if (!open) {
      setPlaced(null)
      return
    }
    const node = ref.current
    if (!node) return
    const width = node.offsetWidth
    const height = node.offsetHeight
    const maxLeft = Math.max(VIEWPORT_MARGIN, window.innerWidth - VIEWPORT_MARGIN - width)
    const maxTop = Math.max(VIEWPORT_MARGIN, window.innerHeight - VIEWPORT_MARGIN - height)
    const left = Math.min(Math.max(VIEWPORT_MARGIN, x), maxLeft)
    // 下方放得下 → 向下；放不下且上方放得下 → 翻到上方（菜单底边贴住落点）
    const openUp = y + height > window.innerHeight - VIEWPORT_MARGIN && y - height >= VIEWPORT_MARGIN
    const top = openUp ? y - height : Math.min(Math.max(VIEWPORT_MARGIN, y), maxTop)
    setPlaced(previous => previous && previous.left === left && previous.top === top
      ? previous
      : { left, top })
  }, [x, y, open])

  return { ref, left: placed?.left ?? x, top: placed?.top ?? y }
}
