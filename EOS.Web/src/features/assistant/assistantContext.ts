import { createContext, useContext } from 'react'
import type { AssistantDraft } from './useChatStream'
import type { SituationItem } from './situationSource'
import type { AssistantSession, SituationDigestItem, SituationSnapshot } from './types'
import type { AssistantFormDraft } from './useChatStream'

export const ASSISTANT_OPEN_KEY = 'erp-assistant-open'
/** 助手全屏形态的路由：它是普通工作区标签页，所以能像统一工作台那样开成一个标签。 */
export const ASSISTANT_PATH = '/assistant'
/** 会话管理页的路由：同样是普通工作区标签页，与助手全屏页可并存、可深链。 */
export const SESSION_ADMIN_PATH = '/assistant/sessions'

/** 一条已渲染的消息。drafts / tools 由服务端或流式事件带上，不在前端派生。 */
export interface Bubble {
  key: string
  role: 1 | 2
  text: string
  streaming?: boolean
  tools?: Array<{ name: string; digest: string }>
  drafts?: AssistantDraft[]
  /**
   * 推理内容（模型有才有）。**不落库**——它不进后续上下文，只帮用户理解"这句话是怎么来的"，
   * 所以刷新会话后不再显示。
   */
  reasoning?: string
  /** 正在执行中的工具（tool_start 已到、tool_result 未到）：让工具轮有可见的等待状态。 */
  runningTools?: string[]
  /** 被输出上限截断（服务端 finishReason = length）。 */
  truncated?: boolean
  /** 本轮随消息发出的处境（逐项摘要）。**用户侧消息专有**：他要知道自己发了什么出去。 */
  situation?: SituationItem[]
  /** 该消息在库里的 id（反馈按 id 提交；本轮新建的气泡在 done 之前没有）。 */
  messageId?: number
  /** 用户反馈：1 赞 / -1 踩。 */
  feedback?: number
}

export interface AssistantContextValue {
  /** 抽屉是否展开。全屏标签页不由它控制（那是路由的事），两者共用下面这份会话状态。 */
  open: boolean
  setOpen: (value: boolean | ((current: boolean) => boolean)) => void
  sessions: AssistantSession[]
  sessionId: string | null
  setSessionId: (value: string | null) => void
  bubbles: Bubble[]
  input: string
  setInput: (value: string) => void
  errorText: string | null
  setErrorText: (value: string | null) => void
  situation: SituationSnapshot | null
  remembered: ReadonlySet<string>
  showArchived: boolean
  setShowArchived: (value: boolean | ((current: boolean) => boolean)) => void
  streaming: boolean
  stop: () => void
  refreshSessions: () => Promise<AssistantSession[]>
  newSession: () => Promise<void>
  sendMessage: (raw: string) => Promise<void>
  askAbout: (item: SituationDigestItem) => void
  remember: (bubble: Bubble) => Promise<void>
  /** 把草稿预填进统一表单：经 sessionStorage 一次性通道，不新增写路径。 */
  openInForm: (draft: AssistantFormDraft) => void
  /**
   * 给一条助手回复点赞 / 点踩 / 取消（`feedback` 传 0 表示取消）。
   * 反馈是**用户自己的标注**，不改变回答内容，也不构成任何授权。
   */
  rateMessage: (bubble: Bubble, feedback: 0 | 1 | -1, reason?: string) => Promise<void>
}

export const AssistantContext = createContext<AssistantContextValue | null>(null)

/** 取助手共享状态。半屏抽屉与全屏标签页都从这里读，**同一份会话**。 */
export function useAssistant(): AssistantContextValue {
  const value = useContext(AssistantContext)
  if (!value) throw new Error('useAssistant 必须在 AssistantProvider 内使用')
  return value
}
