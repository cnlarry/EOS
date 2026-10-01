import { createContext, useContext, type MouseEvent } from 'react'
import { useNavigate } from 'react-router-dom'

export interface WorkspaceNavValue {
  /** 打开目标地址：已开标签则聚焦，未开则新建标签（撞顶时只提示不新建） */
  openTab: (url: string) => void
  /** 关闭当前活动标签（助手全屏形态用它"回到半屏"）。首页标签常驻，不会被关掉。 */
  closeActiveTab: () => void
}

export const WorkspaceNavContext = createContext<WorkspaceNavValue | null>(null)

/**
 * 打开标签的统一入口：外壳之外的页面（首页快捷入口、跨模块关联浏览等）通过它请求新标签。
 * 无 Provider 时（如独立渲染页面或单测）退化为普通导航，行为与单标签一致。
 */
export function useOpenTab(): (url: string) => void {
  const workspace = useContext(WorkspaceNavContext)
  const navigate = useNavigate()
  return workspace ? workspace.openTab : (url: string) => navigate(url)
}

/**
 * 关闭当前标签：供"某个页面自己请求退场"的场景使用（助手全屏切回半屏）。
 * 无 Provider 时退化为回首页，行为与单标签一致。
 */
export function useCloseTab(): () => void {
  const workspace = useContext(WorkspaceNavContext)
  const navigate = useNavigate()
  return workspace ? workspace.closeActiveTab : () => navigate('/dashboard')
}

/**
 * 链接上的点击处理：左键单击走标签打开，修饰键（Ctrl/Cmd/Shift/Alt）与中键保留浏览器默认行为，
 * 使「新窗口打开」仍然可用。
 */
export function tabLinkHandler(openTab: (url: string) => void, url: string) {
  return (event: MouseEvent<HTMLAnchorElement>) => {
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return
    event.preventDefault()
    openTab(url)
  }
}
