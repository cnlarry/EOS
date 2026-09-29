import {
  IconArrowUp,
  IconBookmark,
  IconDots,
  IconLayoutSidebar,
  IconPlayerStop,
  IconPlus,
  IconRobot,
  IconTrash,
  IconX,
} from '@tabler/icons-react'
import { useCallback, useEffect, useRef, useState } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { createSession, deleteSession, getSituation, listMessages, listSessions, saveMemory } from './api'
import { AssistantMemoryPanel } from './AssistantMemoryPanel'
import { KbDocDialog, KbSourceText } from './KbSource'
import { extractPageContext } from './pageContext'
import { buildChatSituation } from './situationSource'
import { useChatStream, type AssistantDraft, type AssistantFormDraft } from './useChatStream'
import { AdminChangesetCard } from './AdminChangesetCard'
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
  const [memoryOpen, setMemoryOpen] = useState(false)
  const [openDocId, setOpenDocId] = useState<string | null>(null)
  const [remembered, setRemembered] = useState<ReadonlySet<string>>(new Set())
  const [situation, setSituation] = useState<SituationSnapshot | null>(null)
  const scrollRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLTextAreaElement>(null)
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

  const sendMessage = useCallback(async (raw: string) => {
    const content = raw.trim()
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
                  <div className="erp-assistant-tool-chips">
                    {bubble.tools.map((tool, index) => (
                      <span key={index} className="erp-assistant-chip" title={tool.digest}>
                        🔍 {tool.name === 'search_records' ? '已查询业务数据' : `已调用 ${tool.name}`}
                      </span>
                    ))}
                  </div>
                )}
                {!bubble.streaming && bubble.drafts && bubble.drafts.length > 0 && (
                  <div className="erp-assistant-drafts">
                    {bubble.drafts.map((draft, index) => 'kind' in draft && draft.kind === 'admin-changeset' ? (
                      <AdminChangesetCard key={index} draft={draft} />
                    ) : (
                      <DraftCard key={index} draft={draft as AssistantFormDraft} onOpenForm={handleOpenInForm} />
                    ))}
                  </div>
                )}
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
  return { key: `m-${message.id}`, role: message.role as 1 | 2, text: message.content }
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
