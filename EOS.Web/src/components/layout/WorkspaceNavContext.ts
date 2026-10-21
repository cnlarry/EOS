import { createContext, useContext, type MouseEvent } from 'react'
import { useNavigate } from 'react-router-dom'

/** 打开标签的选项 */
export interface OpenTabOptions {
  /**
   * 跨模块**定位**：同身份的标签已存在时，把它带到目标地址（如"待办 → 指定的那一张单"）。
   * 缺省是**只聚焦**（菜单语义：去那个模块，但不挪动它停在那一页——见 ADR-040 §2.2 规则 5）。
   */
  locate?: boolean
  /** 随标签一起带过去的导航 state（浏览顺序、窗体提示这类跨页上下文） */
  state?: unknown
  /** 以**对照窗口**打开（固定承载这一张单）：同模块两单并排用，见 ADR-040 §2.6 */
  asRecord?: boolean
  /** 开它的来源标签：关闭该窗口时回到这里 */
  openedFrom?: string
}

export interface WorkspaceNavValue {
  /** 打开目标地址：命中同一身份即聚焦；未开则新建（撞顶时只提示不新建） */
  openTab: (url: string, options?: OpenTabOptions) => void
  /** 关闭当前活动标签（助手全屏形态用它"回到半屏"）。首页标签常驻，不会被关掉。 */
  closeActiveTab: () => void
}

export const WorkspaceNavContext = createContext<WorkspaceNavValue | null>(null)

/**
 * 打开标签的统一入口：外壳之外的页面（首页快捷入口、跨模块跳转等）通过它请求标签。
 * 无 Provider 时（如独立渲染页面或单测）退化为普通导航，行为与单标签一致。
 *
 * **跨模块跳转一律走这里**（带 `locate`），不要用 `navigate()`：后者只改当前标签的地址，
 * 会把来源页顶掉，正是 ADR-040 要消除的行为。
 */
export function useOpenTab(): (url: string, options?: OpenTabOptions) => void {
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
export function tabLinkHandler(openTab: (url: string, options?: OpenTabOptions) => void, url: string, options?: OpenTabOptions) {
  return (event: MouseEvent<HTMLAnchorElement>) => {
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return
    event.preventDefault()
    openTab(url, options)
  }
}
