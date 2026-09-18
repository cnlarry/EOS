import { createContext, useContext, useEffect, useRef } from 'react'

/** 页面为"关闭/离开时如何处置未保存改动"提供的两个动作。 */
export interface TabDirtyHandlers {
  /** 保存并返回保存结果；失败应抛出，调用方据此中止关闭/离开 */
  save: () => Promise<unknown> | unknown
  /** 丢弃改动（页面本身无需清理，随标签关闭或导航卸载） */
  discard: () => void
}

export interface TabDirtyEntry extends TabDirtyHandlers {
  dirty: boolean
}

export interface WorkspaceDirtyValue {
  /** 登记某标签的处置动作，返回注销函数 */
  register: (tabId: string, handlers: TabDirtyHandlers) => () => void
  /** 上报某标签的"已改未保存"状态 */
  setDirty: (tabId: string, dirty: boolean) => void
}

export const WorkspaceDirtyContext = createContext<WorkspaceDirtyValue | null>(null)

/** 当前面板所属的标签标识：页面据此把脏位登记到自己的标签上。 */
export const WorkspaceTabContext = createContext<string | null>(null)

/**
 * 页面把"已改未保存"登记给外壳：关闭标签、离开当前标签时的确认与保存统一由外壳处理。
 *
 * 页面不自行拦截导航——站点同一时刻只允许一个导航拦截器生效，多标签常驻时各页各自拦截
 * 会互相覆盖，只有最后挂载的那个页面的保护会生效。
 */
export function useTabDirty(dirty: boolean, handlers: TabDirtyHandlers) {
  const tabId = useContext(WorkspaceTabContext)
  const registry = useContext(WorkspaceDirtyContext)
  // 动作每次渲染都会变（闭包最新值），登记时用 ref 代理，避免反复注销再登记
  const handlersRef = useRef(handlers)
  handlersRef.current = handlers

  useEffect(() => {
    if (!tabId || !registry) return
    return registry.register(tabId, {
      save: () => handlersRef.current.save(),
      discard: () => handlersRef.current.discard(),
    })
  }, [tabId, registry])

  useEffect(() => {
    if (!tabId || !registry) return
    registry.setDirty(tabId, dirty)
    return () => registry.setDirty(tabId, false)
  }, [tabId, registry, dirty])
}
