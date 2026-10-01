import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { createSession, getSituation, listMessages, listSessions, saveMemory } from './api'
import { extractPageContext } from './pageContext'
import { buildChatSituation } from './situationSource'
import { useChatStream, type AssistantFormDraft } from './useChatStream'
import { isSituationSnapshot } from './assistantText'
import {
  ASSISTANT_OPEN_KEY,
  ASSISTANT_PATH,
  AssistantContext,
  type AssistantContextValue,
  type Bubble,
} from './assistantContext'
import { assistantPrefillKey } from '../../lib/storageKeys'
import { workbenchNew } from '../document-workbench/workbenchPath'
import type { AssistantMessage, AssistantSession, SituationDigestItem } from './types'

function readBool(key: string): boolean {
  try {
    return localStorage.getItem(key) === 'true'
  } catch {
    return false
  }
}

function toBubble(message: AssistantMessage): Bubble {
  return {
    key: `m-${message.id}`,
    role: message.role as 1 | 2,
    text: message.content,
    tools: message.toolCalls && message.toolCalls.length > 0 ? message.toolCalls : undefined,
  }
}

/**
 * 工作助手的**共享状态容器**：会话、气泡、流式请求都活在这里。
 *
 * <para>
 * 抽屉（半屏）与全屏标签页只是两个**壳**——它们渲染同一份状态，所以来回切换形态时
 * 会话内容与**正在流式生成的回答**都不受影响（流式请求跑在这个 Provider 里，
 * 与壳的挂载、卸载无关）。
 * </para>
 */
export function AssistantProvider({ children }: { children: ReactNode }) {
  const location = useLocation()
  const navigate = useNavigate()
  const [open, setOpen] = useState(() => readBool(ASSISTANT_OPEN_KEY))
  const [sessions, setSessions] = useState<AssistantSession[]>([])
  const [sessionId, setSessionId] = useState<string | null>(null)
  const [bubbles, setBubbles] = useState<Bubble[]>([])
  const [input, setInput] = useState('')
  const [errorText, setErrorText] = useState<string | null>(null)
  const [showArchived, setShowArchived] = useState(false)
  const [remembered, setRemembered] = useState<ReadonlySet<string>>(new Set())
  const [situation, setSituation] = useState<AssistantContextValue['situation']>(null)
  // 新建会话的 id：那一刻服务端历史确定为空，回读只会把刚送出去的本地气泡盖掉，故记一次"跳过"。
  const skipHistoryRef = useRef<string | null>(null)
  // 历史加载代次：本地一开始新一轮（发送）就 +1，让在途的旧回读结果作废。
  // 否则"打开后立刻发问"会被迟到的空历史清空气泡——用户消息与正在流出的回答一起消失。
  const historyLoadRef = useRef(0)
  const { send, stop, streaming } = useChatStream()

  useEffect(() => {
    localStorage.setItem(ASSISTANT_OPEN_KEY, String(open))
  }, [open])

  // 站在全屏助手页上时把抽屉收起来：两种形态同一时刻只该有一种。
  // 深链或刷新进来时 localStorage 里可能还留着 open=true，靠这一条把状态对齐
  // （界面侧 AssistantDock 也有同样的判断，避免这一帧闪出抽屉）。
  useEffect(() => {
    if (location.pathname === ASSISTANT_PATH) setOpen(false)
  }, [location.pathname])

  // Ctrl+/ 唤起/收起抽屉。全屏标签页里这个键由 AssistantPage 接管（它要同时关标签、展开抽屉），
  // 这里按路径让位，避免两边同时响应把开关又翻回去。
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (!event.ctrlKey || event.key !== '/') return
      if (location.pathname === ASSISTANT_PATH) return
      event.preventDefault()
      setOpen((value) => !value)
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [location.pathname])

  const refreshSessions = useCallback(async () => {
    try {
      const items = await listSessions(50, showArchived)
      setSessions(items)
      return items
    } catch {
      return []
    }
  }, [showArchived])

  // 会话列表：Provider 常驻，挂载时拉一次；「显示已归档」一开一关再拉（过滤的真源在服务端查询里）
  useEffect(() => {
    void listSessions(50, showArchived).then((items) => {
      setSessions(items)
      setSessionId((current) => current ?? items[0]?.id ?? null)
    }).catch(() => undefined)
  }, [showArchived])

  // 打开即见：拉取结构化处境（服务端规则引擎产出，零模型调用），随路由变化刷新
  useEffect(() => {
    const route = extractPageContext(location.pathname)
    void getSituation({
      moduleId: route?.moduleId,
      pageType: route?.pageType,
      docNo: route?.docNo,
    }).then(data => setSituation(isSituationSnapshot(data) ? data : null)).catch(() => setSituation(null))
  }, [location.pathname])

  // 切换会话时拉取历史（历史恢复）。**不依赖抽屉开合**：切换半屏/全屏只是换壳，内容不该被重置。
  // 新建会话走"跳过"分支：此时回读是空的，而它落地比 sendMessage 里刚 append 的气泡更晚。
  useEffect(() => {
    if (!sessionId) {
      setBubbles([])
      return
    }
    setErrorText(null)
    if (skipHistoryRef.current === sessionId) {
      skipHistoryRef.current = null
      return
    }
    const token = ++historyLoadRef.current
    void listMessages(sessionId).then((messages) => {
      // 期间用户已经开了新一轮：这次回读不再是"当前真相"，套上去会把本地气泡抹掉
      if (historyLoadRef.current !== token) return
      setBubbles(messages.filter(m => m.role === 1 || m.role === 2).map(toBubble))
    }).catch(() => setErrorText('会话历史加载失败，请重试。'))
  }, [sessionId])

  const newSession = useCallback(async () => {
    setErrorText(null)
    try {
      const session = await createSession()
      skipHistoryRef.current = session.id
      setSessionId(session.id)
      setBubbles([])
      setInput('')
      await refreshSessions()
    } catch {
      setErrorText('创建会话失败，请重试。')
    }
  }, [refreshSessions])

  const sendMessage = useCallback(async (raw: string) => {
    const content = raw.trim()
    if (!content || streaming) return
    let target = sessionId
    if (!target) {
      try {
        const session = await createSession()
        target = session.id
        skipHistoryRef.current = target
        setSessionId(target)
        await refreshSessions()
      } catch {
        setErrorText('创建会话失败，请重试。')
        return
      }
    }

    setErrorText(null)
    setInput('')
    const draftKey = `draft-${Date.now()}`
    historyLoadRef.current += 1 // 在途的历史回读就此作废（见该 ref 的说明）
    setBubbles(prev => [
      ...prev,
      { key: `u-${Date.now()}`, role: 1, text: content },
      { key: draftKey, role: 2, text: '', streaming: true },
    ])

    await send({
      sessionId: target,
      content,
      // 处境在发送瞬间从状态总线读取：路由 + 筛选 + 选中 + 脏值 + 最近拒绝（服务端再截断与校验）
      pageContext: buildChatSituation(location.pathname),
      onDelta: (text) => {
        setBubbles(prev => prev.map(b => b.key === draftKey ? { ...b, text: b.text + text } : b))
      },
      onDone: (_message, toolCalls, drafts) => {
        setBubbles(prev => prev.map(b => b.key === draftKey
          ? {
              ...b,
              streaming: false,
              tools: toolCalls && toolCalls.length > 0 ? toolCalls : undefined,
              drafts: drafts && drafts.length > 0 ? drafts : undefined,
            }
          : b))
        void refreshSessions()
      },
      onError: (code, message) => {
        setBubbles(prev => prev.map(b => b.key === draftKey && !b.text ? { ...b, text: `⚠ ${message}`, streaming: false } : b))
        setErrorText(code === 'AI_MODEL_NOT_CONFIGURED' ? message : null)
      },
    })

    // 用户主动停止：保留已收到的增量，去掉流式标记；服务端不落库该回复
    setBubbles(prev => prev.map(b => b.key === draftKey ? { ...b, streaming: false } : b))
  }, [sessionId, streaming, send, refreshSessions, location.pathname])

  /** 摘要条目可点：就地追问（模型按需调用，不发一言不烧额度）。 */
  const askAbout = useCallback((item: SituationDigestItem) => {
    const question = item.kind === 'overdue'
      ? `为什么 ${item.moduleTitle || `模块 ${item.moduleId}`} 的 ${item.key} 一直没批核？我该怎么办？`
      : item.kind === 'blocked-now'
        ? `${item.moduleTitle ? `${item.moduleTitle}的` : ''}${item.key} 现在为什么办不下去？${item.reason}`
        : `${item.moduleTitle ? `${item.moduleTitle}的` : ''}这条操作为什么被拒绝了？${item.reason}`
    setInput(question)
    void sendMessage(question)
  }, [sendMessage])

  const remember = useCallback(async (bubble: Bubble) => {
    const text = bubble.text.trim()
    if (!text || remembered.has(bubble.key)) return
    try {
      await saveMemory({
        memoryType: 'fact',
        memoryKey: text.slice(0, 20),
        memoryValue: text.slice(0, 2000),
      })
      setRemembered(prev => new Set(prev).add(bubble.key))
    } catch {
      setErrorText('记住失败，请重试。')
    }
  }, [remembered])

  // 草稿确认后"带入表单"：经 sessionStorage 一次性通道预填，执行仍走既有统一保存管线。
  const openInForm = useCallback((draft: AssistantFormDraft) => {
    sessionStorage.setItem(assistantPrefillKey(draft.moduleId), JSON.stringify(draft.values))
    setOpen(false)
    navigate(workbenchNew(draft.moduleId))
  }, [navigate])

  const value: AssistantContextValue = {
    open, setOpen, sessions, sessionId, setSessionId, bubbles, input, setInput,
    errorText, setErrorText, situation, remembered, showArchived, setShowArchived,
    streaming, stop, refreshSessions, newSession, sendMessage, askAbout, remember, openInForm,
  }

  return <AssistantContext.Provider value={value}>{children}</AssistantContext.Provider>
}
