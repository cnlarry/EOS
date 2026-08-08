import { Suspense, type ReactNode } from 'react'
import { LoadingState } from '../components/common/AsyncState'

export function withSuspense(node: ReactNode) {
  return <Suspense fallback={<LoadingState label="正在加载页面…" />}>{node}</Suspense>
}
