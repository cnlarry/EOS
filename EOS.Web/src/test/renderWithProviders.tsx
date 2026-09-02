import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, type RenderResult } from '@testing-library/react'
import type { ReactElement } from 'react'

/**
 * 统一测试渲染（代码质量批 3 C9 收编）：
 * 原 22 处 `new QueryClient` + QueryClientProvider 包装样板收敛到本函数。
 * 默认 queries.retry=false（测试不等待重试），可传入自定义 queryClient。
 */
export function renderWithProviders(
  ui: ReactElement,
  options: { queryClient?: QueryClient } = {},
): RenderResult & { queryClient: QueryClient } {
  const queryClient = options.queryClient ?? new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return { ...render(<QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>), queryClient }
}
