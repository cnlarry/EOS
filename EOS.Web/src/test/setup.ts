import '@testing-library/jest-dom/vitest'
import { afterEach } from 'vitest'
import { cleanup } from '@testing-library/react'

/**
 * jsdom 无 IntersectionObserver：提供默认空实现，避免滚动加载组件（ErpTable）挂载即抛错。
 * 需要主动触发触底的测试（如 UnifiedChooser/ErpTable）可再 vi.stubGlobal 覆盖并暴露 trigger。
 */
class TestIntersectionObserver {
  readonly root: Element | Document | null
  readonly rootMargin: string
  readonly thresholds: ReadonlyArray<number>

  constructor(_callback: IntersectionObserverCallback, options?: IntersectionObserverInit) {
    this.root = options?.root ?? null
    this.rootMargin = options?.rootMargin ?? '0px'
    this.thresholds = options?.threshold == null
      ? []
      : Array.isArray(options.threshold)
        ? options.threshold
        : [options.threshold]
  }

  observe = () => undefined
  unobserve = () => undefined
  disconnect = () => undefined
  takeRecords = () => []
}

Object.defineProperty(globalThis, 'IntersectionObserver', {
  writable: true,
  configurable: true,
  value: TestIntersectionObserver,
})

afterEach(() => {
  cleanup()
})
