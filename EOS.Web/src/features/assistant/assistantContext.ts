import { createContext, useContext } from 'react'
import type { AssistantDraft } from './useChatStream'
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
}

export const AssistantContext = createContext<AssistantContextValue | null>(null)

/** 取助手共享状态。半屏抽屉与全屏标签页都从这里读，**同一份会话**。 */
export function useAssistant(): AssistantContextValue {
  const value = useContext(AssistantContext)
  if (!value) throw new Error('useAssistant 必须在 AssistantProvider 内使用')
  return value
}
