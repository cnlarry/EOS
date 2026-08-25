import {
  IconArrowUp,
  IconDots,
  IconLayoutSidebar,
  IconPlayerStop,
  IconPlus,
  IconRobot,
  IconTrash,
  IconX,
} from '@tabler/icons-react'
import { useCallback, useEffect, useRef, useState } from 'react'
import { useLocation } from 'react-router-dom'
import { createSession, deleteSession, listMessages, listSessions } from './api'
import { extractPageContext } from './pageContext'
import { useChatStream } from './useChatStream'
import type { AssistantMessage, AssistantSession } from './types'

const OPEN_KEY = 'erp-assistant-open'
const WIDTH_KEY = 'erp-assistant-width'

interface Bubble {
  key: string
  role: 1 | 2
  text: string
  streaming?: boolean
  tools?: Array<{ name: string; digest: string }>
}

export function AssistantDock() {
  const location = useLocation()
  const [open, setOpen] = useState(() => readBool(OPEN_KEY))
  const [wide, setWide] = useState(() => readWidth() === 520)
  const [sessions, setSessions] = useState<AssistantSession[]>([])
  const [sessionId, setSessionId] = useState<string | null>(null)
  const [bubbles, setBubbles] = useState<Bubble[]>([])
  const [input, setInput] = useState('')
  const [errorText, setErrorText] = useState<string | null>(null)
  const [menuOpen, setMenuOpen] = useState(false)
  const scrollRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLTextAreaElement>(null)
  const { send, stop, streaming } = useChatStream()

  // Ctrl+/ 全局唤起/收起（ADR-007 §2）
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.ctrlKey && event.key === '/') {
        event.preventDefault()
        setOpen((value) => !value)
      }
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [])

  useEffect(() => localStorage.setItem(OPEN_KEY, String(open)), [open])
  useEffect(() => localStorage.setItem(WIDTH_KEY, wide ? '520' : '360'), [wide])

  // 打开抽屉时加载会话列表并恢复最近会话
  useEffect(() => {
    if (!open) return
    void listSessions().then((items) => {
      setSessions(items)
      setSessionId((current) => current ?? items[0]?.id ?? null)
    }).catch(() => undefined)
  }, [open])

  // 切换会话时拉取历史（历史恢复）
  useEffect(() => {
    if (!open || !sessionId) {
      setBubbles([])
      return
    }
    setErrorText(null)
    void listMessages(sessionId).then((messages) => {
      setBubbles(messages.filter(m => m.role === 1 || m.role === 2).map(toBubble))
    }).catch(() => setErrorText('会话历史加载失败，请重试。'))
  }, [open, sessionId])

  useEffect(() => {
    const el = scrollRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [bubbles])

  const refreshSessions = useCallback(async () => {
    try {
      const items = await listSessions()
      setSessions(items)
      return items
    } catch {
      return []
    }
  }, [])

  const handleNewSession = useCallback(async () => {
    setErrorText(null)
    try {
      const session = await createSession()
      setSessionId(session.id)
      setBubbles([])
      setInput('')
      inputRef.current?.focus()
      await refreshSessions()
    } catch {
      setErrorText('创建会话失败，请重试。')
    }
  }, [refreshSessions])

  const handleSend = useCallback(async () => {
    const content = input.trim()
    if (!content || streaming) return
    let target = sessionId
    if (!target) {
      try {
        const session = await createSession()
        target = session.id
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
    setBubbles(prev => [
      ...prev,
      { key: `u-${Date.now()}`, role: 1, text: content },
      { key: draftKey, role: 2, text: '', streaming: true },
    ])

    const outcome = await send({
      sessionId: target,
      content,
      pageContext: extractPageContext(location.pathname, location.search),
      onDelta: (text) => {
        setBubbles(prev => prev.map(b => b.key === draftKey ? { ...b, text: b.text + text } : b))
      },
      onDone: (_message, toolCalls) => {
        setBubbles(prev => prev.map(b => b.key === draftKey
          ? { ...b, streaming: false, tools: toolCalls && toolCalls.length > 0 ? toolCalls : undefined }
          : b))
        void refreshSessions()
      },
      onError: (code, message) => {
        setBubbles(prev => prev.map(b => b.key === draftKey && !b.text ? { ...b, text: `⚠ ${message}`, streaming: false } : b))
        setErrorText(code === 'AI_MODEL_NOT_CONFIGURED' ? message : null)
      },
    })

    if (outcome === 'aborted') {
      // 用户主动停止：保留已收到的增量，去掉流式标记；服务端不落库该回复
      setBubbles(prev => prev.map(b => b.key === draftKey ? { ...b, streaming: false } : b))
    } else {
      setBubbles(prev => prev.map(b => b.key === draftKey ? { ...b, streaming: false } : b))
    }
  }, [input, sessionId, streaming, send, refreshSessions, location.pathname, location.search])

  const handleDeleteSession = useCallback(async () => {
    setMenuOpen(false)
    if (!sessionId) return
    try {
      await deleteSession(sessionId)
      const items = await refreshSessions()
      setSessionId(items[0]?.id ?? null)
      setBubbles([])
    } catch {
      setErrorText('删除会话失败。')
    }
  }, [sessionId, refreshSessions])

  const handleKeyDown = useCallback((event: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault()
      void handleSend()
    }
  }, [handleSend])

  return (
    <>
      {!open && (
        <button className="erp-assistant-fab" type="button" title="工作助手 (Ctrl+/)" aria-label="打开工作助手"
          onClick={() => setOpen(true)}>
          <IconRobot size={26} />
        </button>
      )}
      {open && (
        <aside className={`erp-assistant-drawer${wide ? ' erp-assistant-wide' : ''}`} aria-label="工作助手">
          <header className="erp-assistant-header">
            <IconRobot size={20} />
            <span className="erp-assistant-title">工作助手</span>
            <button className="btn btn-icon btn-sm btn-ghost-secondary" type="button"
              title="新对话" aria-label="新建会话"
              onClick={() => void handleNewSession()}>
              <IconPlus size={16} />
            </button>
            <select
              className="form-select form-select-sm erp-assistant-session-select"
              value={sessionId ?? ''}
              onChange={(event) => setSessionId(event.target.value || null)}
              aria-label="选择会话"
            >
              {sessions.length === 0 && <option value="">暂无会话</option>}
              {sessions.map(session => (
                <option key={session.id} value={session.id}>{session.title}</option>
              ))}
            </select>
            <div className="position-relative">
              <button className="btn btn-icon btn-sm btn-ghost-secondary" type="button"
                title="更多操作" aria-label="更多操作"
                onClick={() => setMenuOpen(value => !value)}>
                <IconDots size={16} />
              </button>
              {menuOpen && (
                <div className="dropdown-menu dropdown-menu-end show erp-assistant-menu">
                  <button className="dropdown-item text-danger" type="button" disabled={!sessionId}
                    onClick={() => void handleDeleteSession()}>
                    <IconTrash size={14} className="me-1" />删除当前会话
                  </button>
                </div>
              )}
            </div>
            <button className="btn btn-icon btn-sm btn-ghost-secondary" type="button"
              title={wide ? '收窄' : '加宽'} aria-label="调整宽度"
              onClick={() => setWide(value => !value)}>
              <IconLayoutSidebar size={16} />
            </button>
            <button className="btn btn-icon btn-sm btn-ghost-secondary" type="button"
              title="关闭 (Ctrl+/)" aria-label="关闭工作助手"
              onClick={() => setOpen(false)}>
              <IconX size={16} />
            </button>
          </header>

          <div className="erp-assistant-messages" ref={scrollRef}>
            {bubbles.length === 0 && (
              <div className="erp-assistant-empty">
                我是 EOS 工作助手，有什么可以帮你？
                <span className="text-secondary d-block mt-1">当前为对话骨架版（M2），业务数据感知将在后续版本接入。</span>
              </div>
            )}
            {bubbles.map(bubble => (
              <div key={bubble.key} className={`erp-assistant-bubble ${bubble.role === 1 ? 'is-user' : 'is-assistant'}`}>
                {bubble.text || (bubble.streaming ? '' : '(空回复)')}
                {bubble.streaming && <span className="erp-assistant-cursor" aria-hidden="true">▍</span>}
                {!bubble.streaming && bubble.tools && bubble.tools.length > 0 && (
                  <div className="erp-assistant-tool-chips">
                    {bubble.tools.map((tool, index) => (
                      <span key={index} className="erp-assistant-chip" title={tool.digest}>
                        🔍 {tool.name === 'search_records' ? '已查询业务数据' : `已调用 ${tool.name}`}
                      </span>
                    ))}
                  </div>
                )}
              </div>
            ))}
          </div>

          {errorText && (
            <div className="erp-assistant-error" role="alert">{errorText}</div>
          )}

          <footer className="erp-assistant-input">
            <textarea
              ref={inputRef}
              className="form-control form-control-sm"
              placeholder="输入问题，Enter 发送，Shift+Enter 换行"
              rows={2}
              value={input}
              onChange={(event) => setInput(event.target.value)}
              onKeyDown={handleKeyDown}
            />
            {streaming ? (
              <button className="btn btn-sm btn-secondary erp-assistant-send" type="button"
                title="停止生成" aria-label="停止生成" onClick={stop}>
                <IconPlayerStop size={16} />
              </button>
            ) : (
              <button className="btn btn-sm btn-primary erp-assistant-send" type="button"
                title="发送" aria-label="发送" disabled={!input.trim()}
                onClick={() => void handleSend()}>
                <IconArrowUp size={16} />
              </button>
            )}
          </footer>
        </aside>
      )}
    </>
  )
}

function toBubble(message: AssistantMessage): Bubble {
  return { key: `m-${message.id}`, role: message.role as 1 | 2, text: message.content }
}

function readBool(key: string): boolean {
  try {
    return localStorage.getItem(key) === 'true'
  } catch {
    return false
  }
}

function readWidth(): number {
  try {
    return Number(localStorage.getItem(WIDTH_KEY)) === 520 ? 520 : 360
  } catch {
    return 360
  }
}
