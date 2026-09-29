import { useState } from 'react'
import { MutationCache, QueryCache, QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { RouterProvider } from 'react-router-dom'
import { AppErrorBoundary } from './AppErrorBoundary'
import { ToastProvider } from '../components/ui/Toast'
import { useToast } from '../components/ui/toastContext'
import { AuthProvider } from '../features/auth/AuthProvider'
import { copyDiagnostics, currentAppInfo, reportIdOf } from '../lib/diagnostics'
import { describeApiError } from '../lib/errors'
import { ApiError } from '../types/api'
import { router } from './router'

/**
 * 全局错误出口（前端）：
 * - 变更失败（保存/删除/批动作）默认弹一条带「复制报障编号」的提示——这类失败必然面向用户；
 *   页面各自写了 onError 的仍以页面内提示为准；
 * - 查询失败不弹提示（页面有自己的 ErrorState），仅在 5xx 时留一条控制台线索。
 * 两者都从服务端错误体里取 correlationId，与服务端日志、审计里的值同源。
 */
function AppShellProviders() {
  const { notify } = useToast()
  const [queryClient] = useState(() => new QueryClient({
    defaultOptions: {
      queries: {
        refetchOnWindowFocus: false,
        staleTime: 30_000,
      },
    },
    mutationCache: new MutationCache({
      onError: (error) => {
        const reportId = reportIdOf(error)
        notify({
          variant: 'danger',
          message: describeApiError(error, '操作失败，请稍后重试。'),
          action: reportId
            ? { label: '复制报障编号', onClick: () => copyDiagnostics(currentAppInfo(), reportId) }
            : undefined,
        })
      },
    }),
    queryCache: new QueryCache({
      onError: (error) => {
        if (error instanceof ApiError && error.status >= 500) {
          console.warn('[EOS] 查询失败', error.body.code, error.body.correlationId ?? '(无报障编号)')
        }
      },
    }),
  }))

  return (
    <QueryClientProvider client={queryClient}>
      <AuthProvider><RouterProvider router={router} /></AuthProvider>
    </QueryClientProvider>
  )
}

export function AppProviders() {
  return (
    // 轻提示放在最外层：登录页与工作区都要能用，且它的浮层不该被路由切换卸载。
    // 错误边界放在轻提示之内：崩溃时界面仍能渲染诊断信息（不依赖路由是否可用）。
    <ToastProvider>
      <AppErrorBoundary>
        <AppShellProviders />
      </AppErrorBoundary>
    </ToastProvider>
  )
}
