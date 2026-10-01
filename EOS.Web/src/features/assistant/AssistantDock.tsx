import {
  IconArchive,
  IconArrowUp,
  IconBookmark,
  IconDots,
  IconDownload,
  IconLayoutSidebar,
  IconPencil,
  IconPlayerStop,
  IconPlus,
  IconRobot,
  IconX,
} from '@tabler/icons-react'
import { Fragment, useCallback, useEffect, useRef, useState } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { archiveSession, createSession, getSituation, listMessages, listSessions, renameSession, saveMemory } from './api'
import { AssistantMemoryPanel } from './AssistantMemoryPanel'
import { KbDocDialog, KbSourceText } from './KbSource'
import { extractPageContext } from './pageContext'
import { buildChatSituation } from './situationSource'
import { useChatStream, type AssistantDraft, type AssistantFormDraft } from './useChatStream'
import { ActionCard, ActionResultCard } from './ActionCard'
import { AdminChangesetCard } from './AdminChangesetCard'
import { ApprovalRequestCard } from './ApprovalRequestCard'
import { ConfigApplyCard, ConfigDiffCard } from './ConfigDiffCard'
import { buildSessionMarkdown, downloadText } from './sessionExport'
import { assistantPrefillKey } from '../../lib/storageKeys'
import { workbenchNew } from '../document-workbench/workbenchPath'
import type { AssistantMessage, AssistantSession, SituationDigestItem, SituationSnapshot } from './types'

const OPEN_KEY = 'erp-assistant-open'
const WIDTH_KEY = 'erp-assistant-width'
const FAB_POS_KEY = 'erp-assistant-fab-pos'
const FAB_SIZE = 52
const FAB_MARGIN = 8

interface FabPos {
  x: number
  y: number
}

function readFabPos(): FabPos | null {
  try {
    const raw = localStorage.getItem(FAB_POS_KEY)
    if (!raw) return null
    const parsed = JSON.parse(raw) as FabPos
    if (typeof parsed.x === 'number' && typeof parsed.y === 'number') return parsed
    return null
  } catch {
    return null
  }
}

interface Bubble {
  key: string
  role: 1 | 2
  text: string
  streaming?: boolean
  tools?: Array<{ name: string; digest: string }>
  drafts?: AssistantDraft[]
}

export function AssistantDock() {
  const location = useLocation()
  const navigate = useNavigate()
  const [open, setOpen] = useState(() => readBool(OPEN_KEY))
  const [wide, setWide] = useState(() => readWidth() === 520)
  const [fabPos, setFabPos] = useState<FabPos | null>(readFabPos)
  const [sessions, setSessions] = useState<AssistantSession[]>([])
  const [sessionId, setSessionId] = useState<string | null>(null)
  const [bubbles, setBubbles] = useState<Bubble[]>([])
  const [input, setInput] = useState('')
  const [errorText, setErrorText] = useState<string | null>(null)
  const [menuOpen, setMenuOpen] = useState(false)
  // 会话管理：重命名走行内编辑；归档代替删除（默认列表不含已归档，开关打开才带出来）
  const [renaming, setRenaming] = useState(false)
  const [renameValue, setRenameValue] = useState('')
  const [showArchived, setShowArchived] = useState(false)
  const [memoryOpen, setMemoryOpen] = useState(false)
  const [openDocId, setOpenDocId] = useState<string | null>(null)
  const [remembered, setRemembered] = useState<ReadonlySet<string>>(new Set())
  const [situation, setSituation] = useState<SituationSnapshot | null>(null)
  const scrollRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLTextAreaElement>(null)
  const drawerRef = useRef<HTMLElement>(null)
  const menuRef = useRef<HTMLDivElement>(null)
  // Esc 取消重命名时不提交：元素卸载未必派发 blur，这里自己记一笔
  const cancelRenameRef = useRef(false)
  // 新建会话的 id：那一刻服务端历史确定为空，回读只会把刚送出去的本地气泡盖掉，故记一次"跳过"。
  const skipHistoryRef = useRef<string | null>(null)
  // 历史加载代次：本地一开始新一轮（发送）就 +1，让在途的旧回读结果作废。
  // 否则"打开抽屉后立刻发问"会被迟到的空历史清空气泡——用户消息与正在流出的回答一起消失。
  const historyLoadRef = useRef(0)
  const fabDragRef = useRef<{ startX: number; startY: number; originX: number; originY: number; moved: boolean } | null>(null)
  // 拖拽移动标记独立于指针会话：onPointerUp 清空会话，但移动事实保留到 onClick 消费，
  // 避免「拖拽后误触发打开」。
  const fabMovedRef = useRef(false)
  const { send, stop, streaming } = useChatStream()

  // Ctrl+/ 全局唤起/收起
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

  // 点抽屉以外收起（**不销毁**：气泡都在 state 里，再点浮球原样回来）；
  // 菜单是抽屉内的浮层，点它之外（含抽屉其它区域）就收起来。Esc 先收菜单、没有菜单时收抽屉。
  useEffect(() => {
    if (!open) return
    const onPointerDown = (event: PointerEvent) => {
      const target = event.target as Node
      if (menuRef.current && !menuRef.current.contains(target)) setMenuOpen(false)
      if (drawerRef.current && !drawerRef.current.contains(target)) setOpen(false)
    }
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return
      if (menuOpen) setMenuOpen(false)
      else setOpen(false)
    }
    document.addEventListener('pointerdown', onPointerDown)
    document.addEventListener('keydown', onKeyDown)
    return () => {
      document.removeEventListener('pointerdown', onPointerDown)
      document.removeEventListener('keydown', onKeyDown)
    }
  }, [open, menuOpen])

  useEffect(() => localStorage.setItem(OPEN_KEY, String(open)), [open])
  useEffect(() => localStorage.setItem(WIDTH_KEY, wide ? '520' : '360'), [wide])
  useEffect(() => {
    if (fabPos) localStorage.setItem(FAB_POS_KEY, JSON.stringify(fabPos))
  }, [fabPos])

  // 浮球拖拽：pointer 捕获 + 视口内钳制；位置按用户存 localStorage（纯展示偏好，不落库）
  const onFabPointerDown = (event: React.PointerEvent<HTMLButtonElement>) => {
    const current = fabPos ?? {
      x: window.innerWidth - 24 - FAB_SIZE,
      y: window.innerHeight - 24 - FAB_SIZE,
    }
    fabDragRef.current = { startX: event.clientX, startY: event.clientY, originX: current.x, originY: current.y, moved: false }
    fabMovedRef.current = false
    event.currentTarget.setPointerCapture?.(event.pointerId)
  }

  const onFabPointerMove = (event: React.PointerEvent<HTMLButtonElement>) => {
    const state = fabDragRef.current
    if (!state) return
    const dx = event.clientX - state.startX
    const dy = event.clientY - state.startY
    if (!state.moved && Math.hypot(dx, dy) > 4) {
      state.moved = true
      fabMovedRef.current = true
    }
    const nextX = Math.min(Math.max(state.originX + dx, FAB_MARGIN), window.innerWidth - FAB_SIZE - FAB_MARGIN)
    const nextY = Math.min(Math.max(state.originY + dy, FAB_MARGIN), window.innerHeight - FAB_SIZE - FAB_MARGIN)
    setFabPos({ x: nextX, y: nextY })
  }

  const endFabDrag = () => {
    fabDragRef.current = null
  }

  const openFab = () => {
    if (fabMovedRef.current) {
      fabMovedRef.current = false
      return
    }
    setOpen(true)
  }

  // 打开抽屉时加载会话列表并恢复最近会话
  useEffect(() => {
    if (!open) return
    void listSessions().then((items) => {
      setSessions(items)
      setSessionId((current) => current ?? items[0]?.id ?? null)
    }).catch(() => undefined)
  }, [open])

  // 打开即见：拉取结构化处境（服务端规则引擎产出，零模型调用），随路由变化刷新
  useEffect(() => {
    if (!open) return
    const route = extractPageContext(location.pathname)
    void getSituation({
      moduleId: route?.moduleId,
      pageType: route?.pageType,
      docNo: route?.docNo,
    }).then(data => setSituation(isSituationSnapshot(data) ? data : null)).catch(() => setSituation(null))
  }, [open, location.pathname])

  // 切换会话时拉取历史（历史恢复）。新建会话走"跳过"分支：此时回读是空的，
  // 而它落地比 sendMessage 里刚 append 的气泡更晚，会把用户消息与正在流式的回答一起抹掉。
  useEffect(() => {
    if (!open || !sessionId) {
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
  }, [open, sessionId])

  useEffect(() => {
    const el = scrollRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [bubbles])

  const refreshSessions = useCallback(async () => {
    try {
      const items = await listSessions(50, showArchived)
      setSessions(items)
      return items
    } catch {
      return []
    }
  }, [showArchived])

  // 「显示已归档」一开一关就重拉：过滤的真源在服务端查询里，前端不再自己筛一遍
  useEffect(() => {
    if (!open) return
    void refreshSessions()
  }, [open, showArchived, refreshSessions])

  const handleNewSession = useCallback(async () => {
    setErrorText(null)
    try {
      const session = await createSession()
      skipHistoryRef.current = session.id
      setSessionId(session.id)
      setBubbles([])
      setInput('')
      inputRef.current?.focus()
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

    const outcome = await send({
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

    if (outcome === 'aborted') {
      // 用户主动停止：保留已收到的增量，去掉流式标记；服务端不落库该回复
      setBubbles(prev => prev.map(b => b.key === draftKey ? { ...b, streaming: false } : b))
    } else {
      setBubbles(prev => prev.map(b => b.key === draftKey ? { ...b, streaming: false } : b))
    }
  }, [sessionId, streaming, send, refreshSessions, location.pathname])

  const handleSend = useCallback(() => {
    void sendMessage(input)
  }, [input, sendMessage])

  /** 摘要条目可点：就地追问（模型按需调用，不发一言不烧额度）。 */
  const handleAskAbout = useCallback((item: SituationDigestItem) => {
    const question = item.kind === 'overdue'
      ? `为什么 ${item.moduleTitle || `模块 ${item.moduleId}`} 的 ${item.key} 一直没批核？我该怎么办？`
      : item.kind === 'blocked-now'
        ? `${item.moduleTitle ? `${item.moduleTitle}的` : ''}${item.key} 现在为什么办不下去？${item.reason}`
        : `${item.moduleTitle ? `${item.moduleTitle}的` : ''}这条操作为什么被拒绝了？${item.reason}`
    setInput(question)
    void sendMessage(question)
  }, [sendMessage])

  // After a draft is confirmed, "open in form" pre-fills through a one-shot sessionStorage channel.
  // Execution reuses the existing unified form save pipeline; the assistant adds no new write path.
  const handleOpenInForm = useCallback((draft: AssistantFormDraft) => {
    sessionStorage.setItem(assistantPrefillKey(draft.moduleId), JSON.stringify(draft.values))
    setOpen(false)
    navigate(workbenchNew(draft.moduleId))
  }, [navigate])

  const handleAskDigest = useCallback(() => {
    setInput('请总结我的偏好和记住的事项')
    inputRef.current?.focus()
  }, [])

  const handleRememberBubble = useCallback(async (bubble: Bubble) => {
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

  /** 归档 / 取消归档。归档代替删除：会话从列表里收起来，历史一行不动，随时可取消。 */
  const handleArchiveSession = useCallback(async (archived: boolean) => {
    setMenuOpen(false)
    if (!sessionId) return
    try {
      await archiveSession(sessionId, archived)
      const items = await refreshSessions()
      // 归档后当前会话可能已不在可见列表里 → 落到第一个仍可见的会话
      if (!items.some(item => item.id === sessionId)) {
        setSessionId(items[0]?.id ?? null)
        setBubbles([])
      }
    } catch {
      setErrorText(archived ? '归档失败，请重试。' : '取消归档失败，请重试。')
    }
  }, [sessionId, refreshSessions])

  const startRename = useCallback(() => {
    setMenuOpen(false)
    const current = sessions.find(item => item.id === sessionId)
    if (!current) return
    cancelRenameRef.current = false
    setRenameValue(current.title)
    setRenaming(true)
  }, [sessions, sessionId])

  const commitRename = useCallback(async () => {
    if (cancelRenameRef.current) {
      cancelRenameRef.current = false
      return
    }
    setRenaming(false)
    const title = renameValue.trim()
    if (!sessionId || !title) return
    try {
      await renameSession(sessionId, title)
      await refreshSessions()
    } catch {
      setErrorText('重命名失败，请重试。')
    }
  }, [renameValue, sessionId, refreshSessions])

  /** 导出当前会话为 Markdown 文件（纯前端拼装：内容全在手里的消息里，不必再开一个端点）。 */
  const handleExportSession = useCallback(async () => {
    setMenuOpen(false)
    if (!sessionId) return
    try {
      const messages = await listMessages(sessionId)
      const title = sessions.find(item => item.id === sessionId)?.title ?? '工作助手会话'
      downloadText(`${title}.md`, buildSessionMarkdown(title, messages))
    } catch {
      setErrorText('导出失败，请重试。')
    }
  }, [sessionId, sessions])

  const handleKeyDown = useCallback((event: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault()
      void handleSend()
    }
  }, [handleSend])

  const currentArchived = sessions.find(item => item.id === sessionId)?.archivedAt != null

  return (
    <>
      {!open && (
        <button
          className={`erp-assistant-fab${fabPos ? ' erp-assistant-fab-dragged' : ''}`}
          type="button"
          title="工作助手 (Ctrl+/)（可拖拽调整位置）"
          aria-label="打开工作助手"
          style={fabPos ? { left: fabPos.x, top: fabPos.y } : undefined}
          onPointerDown={onFabPointerDown}
          onPointerMove={onFabPointerMove}
          onPointerUp={endFabDrag}
          onPointerCancel={endFabDrag}
          onClick={openFab}
        >
          <IconRobot size={26} />
        </button>
      )}
      {open && (
        <aside ref={drawerRef} className={`erp-assistant-drawer${wide ? ' erp-assistant-wide' : ''}`} aria-label="工作助手">
          <header className="erp-assistant-header">
            <IconRobot size={20} />
            <span className="erp-assistant-title">工作助手</span>
            <button className="btn btn-icon btn-sm btn-ghost-secondary" type="button"
              title="新对话" aria-label="新建会话"
              onClick={() => void handleNewSession()}>
              <IconPlus size={16} />
            </button>
            {renaming ? (
              <input
                className="form-control form-control-sm erp-assistant-session-select"
                value={renameValue}
                autoFocus
                aria-label="会话标题"
                onChange={(event) => setRenameValue(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter') {
                    event.preventDefault()
                    void commitRename()
                  }
                  if (event.key === 'Escape') {
                    cancelRenameRef.current = true
                    setRenaming(false)
                  }
                }}
                onBlur={() => void commitRename()}
              />
            ) : (
              <select
                className="form-select form-select-sm erp-assistant-session-select"
                value={sessionId ?? ''}
                onChange={(event) => setSessionId(event.target.value || null)}
                aria-label="选择会话"
              >
                {sessions.length === 0 && <option value="">暂无会话</option>}
                {sessions.map(session => (
                  <option key={session.id} value={session.id}>
                    {session.archivedAt ? `（已归档）${session.title}` : session.title}
                  </option>
                ))}
              </select>
            )}
            <div className="position-relative" ref={menuRef}>
              <button className="btn btn-icon btn-sm btn-ghost-secondary" type="button"
                title="更多操作" aria-label="更多操作"
                onClick={() => setMenuOpen(value => !value)}>
                <IconDots size={16} />
              </button>
              {menuOpen && (
                <div className="dropdown-menu dropdown-menu-end show erp-assistant-menu">
                  <button className="dropdown-item" type="button" disabled={!sessionId}
                    onClick={startRename}>
                    <IconPencil size={14} className="me-1" />重命名
                  </button>
                  <button className="dropdown-item" type="button" disabled={!sessionId}
                    onClick={() => void handleExportSession()}>
                    <IconDownload size={14} className="me-1" />导出为 Markdown
                  </button>
                  <button className="dropdown-item" type="button" disabled={!sessionId}
                    onClick={() => void handleArchiveSession(!currentArchived)}>
                    <IconArchive size={14} className="me-1" />{currentArchived ? '取消归档' : '归档会话'}
                  </button>
                  <div className="dropdown-divider" />
                  <button className="dropdown-item" type="button"
                    onClick={() => { setShowArchived(value => !value); setMenuOpen(false) }}>
                    <IconArchive size={14} className="me-1" />
                    {showArchived ? '✓ 显示已归档' : '显示已归档'}
                  </button>
                </div>
              )}
            </div>
            <button className="btn btn-icon btn-sm btn-ghost-secondary" type="button"
              title="我的记忆（仅本人可见）" aria-label="我的记忆"
              onClick={() => setMemoryOpen(value => !value)}>
              <IconBookmark size={16} />
            </button>
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

          {memoryOpen && (
            <AssistantMemoryPanel
              onClose={() => setMemoryOpen(false)}
              onAskDigest={handleAskDigest}
            />
          )}

          <div className="erp-assistant-messages" ref={scrollRef}>
            {bubbles.length === 0 && (
              <div className="erp-assistant-situation">
                <div className="erp-assistant-situation-head">我是 EOS 工作助手，先说你此刻的处境：</div>
                {situation ? (
                  <>
                    <div className="erp-assistant-situation-line">{describeWhere(situation)}</div>
                    <div className="erp-assistant-situation-line">{describePending(situation)}</div>
                    {situation.digest.items.length > 0 ? (
                      <ul className="erp-assistant-situation-list">
                        {situation.digest.items.map((item, index) => (
                          <li key={`${item.kind}-${item.moduleId}-${item.key}-${index}`}>
                            <button className="erp-assistant-situation-item" type="button"
                              title="点一下问助手这条为什么卡住"
                              onClick={() => handleAskAbout(item)}>
                              <span className="erp-assistant-chip">
                                {digestKindLabel(item.kind)}
                              </span>
                              <span>{item.reason}</span>
                              {item.moduleTitle && (
                                <span className="text-secondary">
                                  （{item.moduleTitle}{item.key ? ` ${item.key}` : ''}）
                                </span>
                              )}
                            </button>
                          </li>
                        ))}
                      </ul>
                    ) : (
                      <div className="erp-assistant-situation-line text-secondary">摘要里没有需要你处理的单据。</div>
                    )}
                    {situation.digest.caveats.map((caveat, index) => (
                      <div key={index} className="erp-assistant-situation-caveat">{caveat}</div>
                    ))}
                    {situation.where.dropped.length > 0 && (
                      <div className="erp-assistant-situation-caveat">
                        已按服务端白名单剔除 {situation.where.dropped.length} 项无法采纳的上报。
                      </div>
                    )}
                  </>
                ) : (
                  <div className="erp-assistant-situation-line text-secondary">正在读取你的处境…</div>
                )}
                <button className="btn btn-sm btn-ghost-secondary mt-2" type="button" onClick={handleAskDigest}>
                  查看我的偏好与记忆
                </button>
              </div>
            )}
            {bubbles.map(bubble => (
              <div key={bubble.key} className={`erp-assistant-bubble ${bubble.role === 1 ? 'is-user' : 'is-assistant'}`}>
                {bubble.role === 2 && bubble.text
                  ? <KbSourceText text={bubble.text} onOpen={setOpenDocId} />
                  : (bubble.text || (bubble.streaming ? '' : '(空回复)'))}
                {bubble.role === 1 && bubble.text && !bubble.streaming && (
                  <button className="btn btn-sm btn-ghost-secondary mt-1" type="button"
                    title="把这句话记下来（仅本人可见）"
                    aria-label={`记住这条消息：${bubble.text.slice(0, 12)}`}
                    disabled={remembered.has(bubble.key)}
                    onClick={() => void handleRememberBubble(bubble)}>
                    {remembered.has(bubble.key) ? '已记住 ✓' : '记住'}
                  </button>
                )}
                {bubble.streaming && <span className="erp-assistant-cursor" aria-hidden="true">▍</span>}
                {!bubble.streaming && bubble.tools && bubble.tools.length > 0 && (
                  <ToolCalls tools={bubble.tools} />
                )}
                {!bubble.streaming && bubble.drafts && bubble.drafts.length > 0 && (
                  <div className="erp-assistant-drafts">
                    {bubble.drafts.map((draft, index) => renderDraft(draft, index, handleOpenInForm))}
                  </div>
                )}
                {/* 操作按钮排在内容之后（工具卡/草稿卡之下）：先读完，再决定要不要复制 */}
                {bubble.role === 2 && bubble.text && !bubble.streaming && <CopyButton text={bubble.text} />}
              </div>
            ))}
          </div>

          {errorText && (
            <div className="erp-assistant-error" role="alert">{errorText}</div>
          )}

          {openDocId && (
            <KbDocDialog docId={openDocId} onClose={() => setOpenDocId(null)} />
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
  return {
    key: `m-${message.id}`,
    role: message.role as 1 | 2,
    text: message.content,
    tools: message.toolCalls && message.toolCalls.length > 0 ? message.toolCalls : undefined,
  }
}

/**
 * 确认卡按 kind 分派。操作卡与结果卡都在**助手内**渲染：
 * 用户改值、重算预演、确认执行全程不离开抽屉，所以这里不出现任何路由跳转。
 */
function renderDraft(draft: AssistantDraft, index: number, onOpenForm: (draft: AssistantFormDraft) => void) {
  if ('kind' in draft) {
    if (draft.kind === 'admin-changeset') return <AdminChangesetCard key={index} draft={draft} />
    if (draft.kind === 'record-action-preview') return <ActionCard key={index} draft={draft} />
    if (draft.kind === 'record-action-result') return <ActionResultCard key={index} result={draft} />
    if (draft.kind === 'config-diff') return <ConfigDiffCard key={index} draft={draft} />
    if (draft.kind === 'config-apply-result') return <ConfigApplyCard key={index} result={draft} />
    if (draft.kind === 'approval-request-preview') return <ApprovalRequestCard key={index} draft={draft} />
  }
  return <DraftCard key={index} draft={draft as AssistantFormDraft} onOpenForm={onOpenForm} />
}

/** 工具名 → 人话；未登记的工具回落到原名，避免新工具上线时芯片变空白。 */
const TOOL_LABELS: Record<string, string> = {
  search_records: '查询业务数据',
  get_record_detail: '读取单据详情',
  get_form_schema: '读取表单结构',
  draft_record: '起草单据',
  list_modules: '列出可用模块',
  describe_module: '查看模块说明',
  list_tables: '列出数据表',
  describe_table: '查看表结构',
  list_views: '列出视图',
  list_procedures: '列出存储过程',
  get_module_flow: '查看流程定义',
  kb_search: '检索知识库',
  diagnose_module: '诊断模块卡点',
  apply_changeset: '应用元数据变更',
  get_my_digest: '读取我的摘要',
  enum_metrics: '枚举指标口径',
  resolve_metric: '按口径取数',
  preview_batch_decision: '预演批量审批',
}

/**
 * 工具调用：默认收起成芯片（一眼看出助手查了什么），点开看**这次调用拿回了什么**。
 * digest 是服务端截断的工具返回（前 160 字）；以 `rejected:` / `error:` 开头表示这次调用没成，
 * 单独用警示色标出——"查不到"和"查到了"在界面上的区别必须是看得见的。
 */
function ToolCalls({ tools }: { tools: NonNullable<Bubble['tools']> }) {
  const [openIndex, setOpenIndex] = useState<number | null>(null)

  return (
    <div className="erp-assistant-tool-chips">
      {tools.map((tool, index) => {
        const digest = tool.digest ?? ''
        const failed = digest.startsWith('rejected:') || digest.startsWith('error:')
        const open = openIndex === index
        return (
          <Fragment key={`${tool.name}-${index}`}>
            <button
              type="button"
              className={`erp-assistant-chip erp-assistant-tool-chip${failed ? ' is-failed' : ''}${open ? ' is-open' : ''}`}
              aria-expanded={open}
              title={open ? '收起这次调用的返回' : '展开这次调用拿回的内容'}
              onClick={() => setOpenIndex(open ? null : index)}
            >
              <span aria-hidden="true">{failed ? '⚠' : '🔍'}</span>
              {failed
                ? `未能完成：${TOOL_LABELS[tool.name] ?? tool.name}`
                : TOOL_LABELS[tool.name] ?? `已调用 ${tool.name}`}
              <span className="erp-assistant-tool-caret" aria-hidden="true">▾</span>
            </button>
            {open && <pre className="erp-assistant-tool-detail">{digest || '（这次调用没有返回内容）'}</pre>}
          </Fragment>
        )
      })}
    </div>
  )
}

/** 复制这条回答的 Markdown 原文：粘到别处仍是结构化文本（表格/列表不塌成一行）。 */
function CopyButton({ text }: { text: string }) {
  const [copied, setCopied] = useState(false)

  const copy = () => {
    if (!navigator.clipboard?.writeText) return
    void navigator.clipboard.writeText(text).then(() => {
      setCopied(true)
      window.setTimeout(() => setCopied(false), 2000)
    }).catch(() => undefined)
  }

  return (
    <button className="btn btn-sm btn-ghost-secondary mt-1" type="button"
      title="复制这条回答（Markdown 原文）" aria-label="复制这条回答"
      onClick={copy}>
      {copied ? '已复制 ✓' : '复制'}
    </button>
  )
}



const PAGE_TYPE_LABELS: Record<string, string> = {
  list: '列表',
  view: '查看',
  edit: '编辑',
  new: '新建',
  copy: '复制',
  'config-fields': '字段配置页',
  'config-datasource': '数据源配置页',
  'config-buttons': '按钮配置页',
  'config-effect': '效果配置页',
}

function pageTypeLabel(pageType: string | null): string {
  if (!pageType) return ''
  return PAGE_TYPE_LABELS[pageType] ?? pageType
}

/** 处境快照结构校验：响应意外（非快照）时按"读不到处境"处理，不让抽屉崩掉。 */
function isSituationSnapshot(value: unknown): value is SituationSnapshot {
  if (typeof value !== 'object' || value === null) return false
  const candidate = value as Partial<SituationSnapshot>
  return typeof candidate.where === 'object' && candidate.where !== null
    && typeof candidate.digest === 'object' && candidate.digest !== null
    && Array.isArray(candidate.digest?.items)
    && typeof candidate.identity === 'object' && candidate.identity !== null
    && typeof candidate.pending === 'object' && candidate.pending !== null
}

/** 「你在哪」：由服务端校验后的处境给出模块与页面，路由信息不含业务事实。 */
function describeWhere(snapshot: SituationSnapshot): string {
  const where = snapshot.where
  const label = pageTypeLabel(where.pageType)
  if (where.moduleTitle) {
    return `你在「${where.moduleTitle}」的${label || '页面'}${where.docNo ? `，当前单据 ${where.docNo}` : ''}。`
  }

  if (label) return `你在${label}。`
  return '你当前不在具体的业务页面上。'
}

/** 摘要条目类别的中文标签（类别由服务端给出，前端不猜语义）。 */
function digestKindLabel(kind: string): string {
  switch (kind) {
    case 'overdue': return '滞留未批核'
    case 'blocked-now': return '此刻办不下去'
    default: return '最近被拒'
  }
}

/** 「压着什么 / 哪件不对」：只报非零事实，避免打开就看到一片 0。 */
function describePending(snapshot: SituationSnapshot): string {
  const overdue = snapshot.digest.items.filter(item => item.kind === 'overdue').length
  const blocked = snapshot.digest.items.filter(item => item.kind === 'blocked-now').length
  const rejected = snapshot.digest.items.filter(item => item.kind === 'rejected').length
  const parts: string[] = []
  if (overdue > 0) parts.push(`有 ${overdue} 条单据滞留未批核`)
  if (blocked > 0) parts.push(`有 ${blocked} 条单据此刻办不下去`)
  if (rejected > 0) parts.push(`最近有 ${rejected} 次操作被拒绝`)
  if (snapshot.pending.myApproval > 0 || snapshot.pending.startedInFlight > 0) {
    parts.push(`待我审批 ${snapshot.pending.myApproval} 条、我发起在途 ${snapshot.pending.startedInFlight} 条`)
  }

  return parts.length > 0 ? `${parts.join('；')}。` : '审批待办与滞留单据当前都是空的。'
}

/** 结构化确认卡片：字段级预览 + 缺失/警告提示 + 带入表单。 */
function DraftCard({ draft, onOpenForm }: { draft: AssistantFormDraft; onOpenForm: (draft: AssistantFormDraft) => void }) {
  const entries = Object.entries(draft.values)
  return (
    <div className="erp-assistant-draft-card">
      <div className="fw-bold mb-1">📝 表单草稿：{draft.moduleTitle}</div>
      <table className="table table-sm erp-assistant-draft-table">
        <tbody>
          {entries.map(([key, value]) => (
            <tr key={key}>
              <td className="text-secondary">{draft.labels?.[key] ?? key}</td>
              <td>{value}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {draft.missingRequired.length > 0 && (
        <div className="text-warning small mb-1">缺必填：{draft.missingRequired.join('、')}</div>
      )}
      {draft.warnings.map((warning, index) => (
        <div key={index} className="text-warning small">{warning}</div>
      ))}
      <button
        className="btn btn-sm btn-primary"
        type="button"
        onClick={() => onOpenForm(draft)}
      >
        带入表单填写（不自动保存）
      </button>
    </div>
  )
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
