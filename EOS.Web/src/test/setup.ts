import '@testing-library/jest-dom/vitest'
import { afterEach } from 'vitest'
import { cleanup, configure } from '@testing-library/react'

/**
 * 异步等待超时：Testing Library 默认 1000ms。本仓并行跑 115 个测试文件、每个文件各建一次
 * jsdom 环境，机器繁忙时 1000ms 余量不足，findBy* / waitFor 会偶发超时（表现为随机一条红）。
 * 统一放宽到 5s；个别用例需要更严的等待，可显式传 timeout 覆盖。
 */
configure({ asyncUtilTimeout: 5000 })

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
