import { fireEvent } from '@testing-library/react'
import { vi } from 'vitest'

/**
 * jsdom 没有 PointerEvent：`fireEvent.pointerDown` 会退化成普通 Event 并丢掉
 * `clientX` / `clientY`，于是"按坐标算落点"的拖拽在测试里永远落不到列上。
 * 这里造一个带指针字段的事件：type 仍是 `pointerdown` / `pointermove`，
 * React 的合成事件与 window 上的原生监听都认它。
 */
export function firePointer(
  type: 'pointerdown' | 'pointermove' | 'pointerup' | 'pointercancel',
  target: Element | Window,
  init: { clientX: number; clientY: number; button?: number },
) {
  const event = Object.assign(new Event(type, { bubbles: true, cancelable: true }), { pointerId: 1, ...init })
  fireEvent(target, event)
}

/** jsdom 里所有元素的矩形都是 0：按坐标判断落点前，先给参与拖拽的列头造出位置 */
export function stubRect(el: Element, left: number, width: number, top = 0, height = 30) {
  vi.spyOn(el, 'getBoundingClientRect').mockReturnValue({
    left,
    right: left + width,
    top,
    bottom: top + height,
    width,
    height,
    x: left,
    y: top,
    toJSON: () => ({}),
  } as DOMRect)
}
