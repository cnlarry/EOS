import { useCallback, useRef, useState } from 'react'
import { useBlocker } from 'react-router-dom'
import { DirtyConfirmDialog } from './DirtyConfirmDialog'

interface WorkspaceNavGuardProps {
  /** 判断该次导航是否会丢弃活动标签的未保存改动 */
  shouldBlock: (nextUrl: string) => boolean
  /** 活动标签的保存动作；无登记时直接放行 */
  getSave: () => (() => Promise<unknown> | unknown) | null
  /** 涉及未保存改动的标签标题 */
  labels: string[]
}

/**
 * 全站唯一的导航拦截器：脏页保护汇聚到外壳。
 * 站点同一时刻只允许一个拦截器生效（注册多个时只有最后注册者被采用），因此页面不得各自拦截。
 * 仅在数据路由（createBrowserRouter）下渲染——其他路由形态不支持拦截，退化为无拦截运行。
 */
export function WorkspaceNavGuard({ shouldBlock, getSave, labels }: WorkspaceNavGuardProps) {
  const shouldBlockRef = useRef(shouldBlock)
  shouldBlockRef.current = shouldBlock
  const blocker = useBlocker(useCallback(({ nextLocation }) => shouldBlockRef.current(
    `${nextLocation.pathname}${nextLocation.search}${nextLocation.hash}`,
  ), []))
  const [busy, setBusy] = useState(false)

  if (blocker.state !== 'blocked') return null
  const save = () => {
    const doSave = getSave()
    if (!doSave) {
      blocker.proceed()
      return
    }
    setBusy(true)
    void Promise.resolve()
      .then(doSave)
      .then(
        () => {
          setBusy(false)
          blocker.proceed()
        },
        // 保存失败：留在当前页，由页面自身呈现错误
        () => setBusy(false),
      )
  }
  return (
    <DirtyConfirmDialog
      labels={labels}
      mode="leave"
      busy={busy}
      onSave={save}
      onDiscard={() => blocker.proceed()}
      onCancel={() => blocker.reset()}
    />
  )
}
