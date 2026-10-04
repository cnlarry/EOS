import {
  IconArchive,
  IconArrowUp,
  IconBookmark,
  IconDots,
  IconDownload,
  IconLayoutSidebarRightExpand,
  IconListDetails,
  IconMaximize,
  IconPencil,
  IconPlayerStop,
  IconPlus,
  IconRobot,
  IconThumbDown,
  IconThumbUp,
  IconX,
} from '@tabler/icons-react'
import { Fragment, useCallback, useEffect, useRef, useState } from 'react'
import { useLocation } from 'react-router-dom'
import { archiveSession, listMessages, renameSession } from './api'
import { AssistantMemoryPanel } from './AssistantMemoryPanel'
import { KbDocDialog, KbSourceText } from './KbSource'
import { SESSION_ADMIN_PATH, useAssistant, type Bubble } from './assistantContext'
import { useOpenTab } from '../../components/layout/WorkspaceNavContext'
import { describePending, describeWhere, digestKindLabel } from './assistantText'
import { buildSessionMarkdown, downloadText } from './sessionExport'
import {
  SITUATION_TOGGLE_KEYS,
  SITUATION_TOGGLE_LABELS,
  isSituationIncluded,
  setSituationIncluded,
  situationPreview,
} from './situationSource'
import { ActionCard, ActionResultCard } from './ActionCard'
import { AdminChangesetCard } from './AdminChangesetCard'
import { ApprovalRequestCard } from './ApprovalRequestCard'
import { ConfigApplyCard, ConfigDiffCard } from './ConfigDiffCard'
import type { AssistantDraft, AssistantFormDraft } from './useChatStream'

interface AssistantPanelProps {
  /** 抽屉（半屏）还是全屏标签页。两者共用同一份会话状态，只是壳不同。 */
  variant: 'drawer' | 'page'
  /** 抽屉侧：切到全屏（由壳负责开标签并收起抽屉）。 */
  onExpand?: () => void
  /** 全屏侧：切回半屏（由壳负责关标签并展开抽屉）。 */
  onCollapse?: () => void
}

/**
 * 助手的**主体面板**：会话栏 + 消息区 + 输入区。
 *
 * <para>
 * 抽屉与全屏标签页都渲染它，会话数据来自 <see cref="useAssistant"/> 的共享状态——
 * 所以切换形态时对话内容与正在流式生成的回答都不受影响。
 * 菜单是否展开、重命名输入框这类**纯界面状态**留在本组件内：换壳时重置是合理的，
 * 而"对话进展"不是界面状态，它在上面的 Provider 里。
 * </para>
 */
export function AssistantPanel({ variant, onExpand, onCollapse }: AssistantPanelProps) {
  const a = useAssistant()
  const location = useLocation()
  const [menuOpen, setMenuOpen] = useState(false)
  const [renaming, setRenaming] = useState(false)
  const [renameValue, setRenameValue] = useState('')
  const [memoryOpen, setMemoryOpen] = useState(false)
  const [openDocId, setOpenDocId] = useState<string | null>(null)
  // 「处境」开关面板：处境是随每条消息隐式发出的，用户得看得见、也得能逐项关掉。
  const [contextOpen, setContextOpen] = useState(false)
  const [contextTick, setContextTick] = useState(0)
  const scrollRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLTextAreaElement>(null)
  const menuRef = useRef<HTMLDivElement>(null)
  // Esc 取消重命名时不提交：元素卸载未必派发 blur，这里自己记一笔
  const cancelRenameRef = useRef(false)

  // 菜单是浮层：点它之外就收起来（抽屉的收起另有处理，在 AssistantDock）
  useEffect(() => {
    if (!menuOpen) return
    const onPointerDown = (event: PointerEvent) => {
      if (menuRef.current && !menuRef.current.contains(event.target as Node)) setMenuOpen(false)
    }
    document.addEventListener('pointerdown', onPointerDown)
    return () => document.removeEventListener('pointerdown', onPointerDown)
  }, [menuOpen])

  // 新内容进来自动滚到底（换壳后新挂载的面板同样要落到最新一条）
  useEffect(() => {
    const el = scrollRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [a.bubbles])

  const startRename = useCallback(() => {
    setMenuOpen(false)
    const current = a.sessions.find(item => item.id === a.sessionId)
    if (!current) return
    cancelRenameRef.current = false
    setRenameValue(current.title)
    setRenaming(true)
  }, [a.sessions, a.sessionId])

  const cancelRename = useCallback(() => {
    // 用标记而不是只 setRenaming(false)：元素卸载未必派发 blur，若有 blur 也会被这个标记拦下
    cancelRenameRef.current = true
    setRenaming(false)
  }, [])

  const commitRename = useCallback(async () => {
    if (cancelRenameRef.current) {
      cancelRenameRef.current = false
      return
    }
    setRenaming(false)
    const title = renameValue.trim()
    if (!a.sessionId || !title) return
    try {
      await renameSession(a.sessionId, title)
      await a.refreshSessions()
    } catch {
      a.setErrorText('重命名失败，请重试。')
    }
  }, [renameValue, a])

  /** 归档 / 取消归档。归档代替删除：会话从列表里收起来，历史一行不动，随时可取消。 */
  const handleArchiveSession = useCallback(async (archived: boolean) => {
    setMenuOpen(false)
    if (!a.sessionId) return
    try {
      await archiveSession(a.sessionId, archived)
      const items = await a.refreshSessions()
      // 归档后当前会话可能已不在可见列表里 → 落到第一个仍可见的会话
      if (!items.some(item => item.id === a.sessionId)) {
        a.setSessionId(items[0]?.id ?? null)
      }
    } catch {
      a.setErrorText(archived ? '归档失败，请重试。' : '取消归档失败，请重试。')
    }
  }, [a])

  /** 导出当前会话为 Markdown 文件（纯前端拼装：内容全在手里的消息里，不必再开一个端点）。 */
  const handleExportSession = useCallback(async () => {
    setMenuOpen(false)
    if (!a.sessionId) return
    try {
      const messages = await listMessages(a.sessionId)
      const title = a.sessions.find(item => item.id === a.sessionId)?.title ?? '工作助手会话'
      downloadText(`${title}.md`, buildSessionMarkdown(title, messages))
    } catch {
      a.setErrorText('导出失败，请重试。')
    }
  }, [a])

  /** 摘要区那句"查看我的偏好与记忆"：只把提示词填进输入框并聚焦，发不发由用户决定。 */
  const handleAskDigest = useCallback(() => {
    a.setInput('请总结我的偏好和记住的事项')
    inputRef.current?.focus()
  }, [a])

  const handleKeyDown = useCallback((event: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault()
      void a.sendMessage(a.input)
    }
  }, [a])

  const openTab = useOpenTab()
  const currentSession = a.sessions.find(item => item.id === a.sessionId)
  const currentArchived = currentSession?.archivedAt != null

  return (
    <div className={`erp-assistant-panel is-${variant}`}>
      {/* 顶部条分两段：左段是"我是谁 + 当前会话"，右段是本面板的动作。
          全屏是页级宽度，若会话选择照半屏那样撑满，就会被拉成横贯整屏的一条长杠。 */}
      <header className="erp-assistant-header">
        <IconRobot size={20} />
        <span className="erp-assistant-title">工作助手</span>
        <button
          className={variant === 'page' ? 'btn btn-sm btn-outline-secondary' : 'btn btn-icon btn-sm btn-ghost-secondary'}
          type="button"
          title="新会话" aria-label="新建会话"
          onClick={() => void a.newSession()}>
          <IconPlus size={14} />
          {variant === 'page' && <span className="ms-1">新会话</span>}
        </button>
        {renaming ? (
          // 编辑态必须一眼看出来：只把下拉换成同尺寸输入框、文字还一样，用户会觉得"点了没反应"
          // （这正是最初的反馈），所以这里给高亮 + 明确的确认/取消。
          <div className="erp-assistant-rename">
            <input
              className="form-control form-control-sm"
              value={renameValue}
              autoFocus
              aria-label="会话标题"
              placeholder="会话名称"
              onChange={(event) => setRenameValue(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === 'Enter') {
                  event.preventDefault()
                  void commitRename()
                }
                if (event.key === 'Escape') {
                  event.preventDefault()
                  cancelRename()
                }
              }}
              onBlur={() => void commitRename()}
            />
            {/* mousedown 先 preventDefault：否则点按钮会先让输入框失焦而触发 blur 提交 */}
            <button className="btn btn-sm btn-primary" type="button"
              title="保存名称" aria-label="保存名称"
              onMouseDown={(event) => event.preventDefault()}
              onClick={() => void commitRename()}>
              ✓
            </button>
            <button className="btn btn-sm btn-ghost-secondary" type="button"
              title="取消（Esc）" aria-label="取消重命名"
              onMouseDown={(event) => event.preventDefault()}
              onClick={cancelRename}>
              ✕
            </button>
          </div>
        ) : (
          <select
            className="form-select form-select-sm erp-assistant-session-select"
            value={a.sessionId ?? ''}
            onChange={(event) => a.setSessionId(event.target.value || null)}
            aria-label="选择会话"
            title={currentSession ? `当前会话：${currentSession.title}` : '暂无会话'}
          >
            {a.sessions.length === 0 && <option value="">暂无会话</option>}
            {a.sessions.map(session => (
              <option key={session.id} value={session.id}>
                {session.archivedAt ? `（已归档）${session.title}` : session.title}
              </option>
            ))}
          </select>
        )}
        {/* 面板级动作的起点：全屏下由 CSS 给这里 `margin-left: auto`，
            把「更多 / 记忆 / 形态切换」顶到右侧，中间留白交给两段分组承担 */}
        <div className="position-relative erp-assistant-header-tools" ref={menuRef}>
          <button className="btn btn-icon btn-sm btn-ghost-secondary" type="button"
            title="更多操作" aria-label="更多操作"
            onClick={() => setMenuOpen(value => !value)}>
            <IconDots size={16} />
          </button>
          {menuOpen && (
            <div className="dropdown-menu dropdown-menu-end show erp-assistant-menu">
              <button className="dropdown-item" type="button" disabled={!a.sessionId}
                onClick={startRename}>
                <IconPencil size={14} className="me-1" />重命名
              </button>
              <button className="dropdown-item" type="button" disabled={!a.sessionId}
                onClick={() => void handleExportSession()}>
                <IconDownload size={14} className="me-1" />导出为 Markdown
              </button>
              <button className="dropdown-item" type="button" disabled={!a.sessionId}
                onClick={() => void handleArchiveSession(!currentArchived)}>
                <IconArchive size={14} className="me-1" />{currentArchived ? '取消归档' : '归档会话'}
              </button>
              <div className="dropdown-divider" />
              <button className="dropdown-item" type="button"
                onClick={() => { setMenuOpen(false); a.setOpen(false); openTab(SESSION_ADMIN_PATH) }}>
                <IconListDetails size={14} className="me-1" />会话管理
              </button>
              <button className="dropdown-item" type="button"
                onClick={() => { a.setShowArchived(value => !value); setMenuOpen(false) }}>
                <IconArchive size={14} className="me-1" />
                {a.showArchived ? '✓ 显示已归档' : '显示已归档'}
              </button>
            </div>
          )}
        </div>
        <button className="btn btn-icon btn-sm btn-ghost-secondary" type="button"
          title="我的记忆（仅本人可见）" aria-label="我的记忆"
          onClick={() => setMemoryOpen(value => !value)}>
          <IconBookmark size={16} />
        </button>
        {variant === 'drawer' ? (
          <>
            <button className="btn btn-icon btn-sm btn-ghost-secondary" type="button"
              title="全屏（在工作区新开一个标签）" aria-label="全屏"
              onClick={onExpand}>
              <IconMaximize size={16} />
            </button>
            <button className="btn btn-icon btn-sm btn-ghost-secondary" type="button"
              title="关闭 (Ctrl+/)" aria-label="关闭工作助手"
              onClick={() => a.setOpen(false)}>
              <IconX size={16} />
            </button>
          </>
        ) : (
          <button className="btn btn-icon btn-sm btn-ghost-secondary" type="button"
            title="半屏（回到侧边抽屉）" aria-label="半屏"
            onClick={onCollapse}>
            <IconLayoutSidebarRightExpand size={16} />
          </button>
        )}
      </header>

      {memoryOpen && (
        <AssistantMemoryPanel
          onClose={() => setMemoryOpen(false)}
          onAskDigest={handleAskDigest}
        />
      )}

      <div className="erp-assistant-messages" ref={scrollRef}>
        {a.bubbles.length === 0 && (
          <div className="erp-assistant-situation">
            <div className="erp-assistant-situation-head">我是 EOS 工作助手，先说你此刻的处境：</div>
            {a.situation ? (
              <>
                <div className="erp-assistant-situation-line">{describeWhere(a.situation)}</div>
                <div className="erp-assistant-situation-line">{describePending(a.situation)}</div>
                {a.situation.digest.items.length > 0 ? (
                  <ul className="erp-assistant-situation-list">
                    {a.situation.digest.items.map((item, index) => (
                      <li key={`${item.kind}-${item.moduleId}-${item.key}-${index}`}>
                        <button className="erp-assistant-situation-item" type="button"
                          title="点一下问助手这条为什么卡住"
                          onClick={() => a.askAbout(item)}>
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
                {a.situation.digest.caveats.map((caveat, index) => (
                  <div key={index} className="erp-assistant-situation-caveat">{caveat}</div>
                ))}
                {a.situation.where.dropped.length > 0 && (
                  <div className="erp-assistant-situation-caveat">
                    已按服务端白名单剔除 {a.situation.where.dropped.length} 项无法采纳的上报。
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
        {a.bubbles.map(bubble => (
          <div key={bubble.key} className={`erp-assistant-message ${bubble.role === 1 ? 'is-user' : 'is-assistant'}`}>
            {/* 气泡样式按侧别分（用户=主色底、助手=浅底描边）：此前只写了 message 行的类、气泡自身没带
                修饰类，样式块 `erp-assistant-bubble.is-user/.is-assistant` 一直没生效——用户消息因此
                退化成"没有底色的一段文字"，长回答一来就被淹掉 */}
            <div className={`erp-assistant-bubble ${bubble.role === 1 ? 'is-user' : 'is-assistant'}`}>
              {bubble.role === 2 && bubble.reasoning && <ReasoningBlock text={bubble.reasoning} />}
              {bubble.role === 2 && bubble.text
                ? <KbSourceText text={bubble.text} onOpen={setOpenDocId} />
                : (bubble.text || (bubble.streaming ? '' : '(空回复)'))}
              {bubble.streaming && <span className="erp-assistant-cursor" aria-hidden="true">▍</span>}
              {/* 工具轮进行中：模型一次给出多个调用时，这里显示"正在调用 X"。
                  此前这段执行期界面上什么都没有，几秒到十几秒的空白会被读成卡死。 */}
              {bubble.streaming && bubble.runningTools && bubble.runningTools.length > 0 && (
                <div className="erp-assistant-tool-chips" aria-live="polite">
                  {bubble.runningTools.map((name, index) => (
                    <span key={`${name}-${index}`} className="erp-assistant-chip">
                      {TOOL_LABELS[name] ?? name} 正在执行…
                    </span>
                  ))}
                </div>
              )}
              {!bubble.streaming && bubble.tools && bubble.tools.length > 0 && (
                <ToolCalls tools={bubble.tools} />
              )}
              {/* 被输出上限截断：回答"突然结束"必须说清是没写完，而不是答完了 */}
              {!bubble.streaming && bubble.truncated && (
                <div className="erp-assistant-truncated">
                  回答达到输出上限被截断，内容可能不完整；可以让助手接着说完。
                </div>
              )}
              {/* 用户侧消息回看"这轮发了什么出去"：隐式上报若连痕迹都没有，用户无从知道助手看到了什么 */}
              {bubble.role === 1 && bubble.situation && bubble.situation.length > 0 && (
                <div className="erp-assistant-context">
                  <span className="erp-assistant-context-title">随这条消息附带</span>
                  <ul>
                    {bubble.situation.map(item => (
                      <li key={item.key}><strong>{item.label}</strong>：{item.detail}</li>
                    ))}
                  </ul>
                </div>
              )}
              {!bubble.streaming && bubble.drafts && bubble.drafts.length > 0 && (
                <div className="erp-assistant-drafts">
                  {bubble.drafts.map((draft, index) => renderDraft(draft, index, a.openInForm))}
                </div>
              )}
            </div>
            {/* 操作按钮在气泡**外面**：贴在气泡下方、随其左右对齐，不再挤在正文里抢视线 */}
            {bubble.text && !bubble.streaming && (
              <div className="erp-assistant-message-actions">
                {bubble.role === 2 && <CopyButton text={bubble.text} />}
                {bubble.role === 2 && <FeedbackButtons bubble={bubble} onRate={a.rateMessage} />}
                {bubble.role === 1 && (
                  <button className="btn btn-sm btn-ghost-secondary" type="button"
                    title="把这句话记下来（仅本人可见）"
                    aria-label={`记住这条消息：${bubble.text.slice(0, 12)}`}
                    disabled={a.remembered.has(bubble.key)}
                    onClick={() => void a.remember(bubble)}>
                    {a.remembered.has(bubble.key) ? '已记住 ✓' : '记住'}
                  </button>
                )}
              </div>
            )}
          </div>
        ))}
      </div>

      {a.errorText && (
        <div className="erp-assistant-error" role="alert">{a.errorText}</div>
      )}

      {contextOpen && (
        <div className="erp-assistant-context-panel">
          <div className="erp-assistant-context-panel-head">
            助手随每条消息附带的处境（关掉即不再发出）
          </div>
          {SITUATION_TOGGLE_KEYS.map(key => (
            <label key={key} className="erp-assistant-context-toggle">
              <input
                type="checkbox"
                checked={isSituationIncluded(key)}
                onChange={(event) => {
                  setSituationIncluded(key, event.target.checked)
                  setContextTick(tick => tick + 1)
                }}
              />
              <span>{SITUATION_TOGGLE_LABELS[key]}</span>
            </label>
          ))}
          <div className="erp-assistant-context-panel-note" data-tick={contextTick}>
            成本位 / 保密位 / 禁止字段由服务端按模块权限二次剔除；关掉的项不会随任何消息发出。
          </div>
        </div>
      )}

      {openDocId && (
        <KbDocDialog docId={openDocId} onClose={() => setOpenDocId(null)} />
      )}

      <footer className="erp-assistant-input">
        <button
          className="btn btn-sm btn-ghost-secondary erp-assistant-context-btn"
          type="button"
          aria-expanded={contextOpen}
          title="看看助手随消息带了哪些处境，可逐项关掉"
          onClick={() => setContextOpen(open => !open)}
        >
          处境 {situationPreview(location.pathname).length}
        </button>
        <textarea
          ref={inputRef}
          className="form-control form-control-sm"
          placeholder="输入问题，Enter 发送，Shift+Enter 换行"
          rows={2}
          value={a.input}
          onChange={(event) => a.setInput(event.target.value)}
          onKeyDown={handleKeyDown}
        />
        {a.streaming ? (
          <button className="btn btn-sm btn-secondary erp-assistant-send" type="button"
            title="停止生成" aria-label="停止生成" onClick={a.stop}>
            <IconPlayerStop size={16} />
          </button>
        ) : (
          <button className="btn btn-sm btn-primary erp-assistant-send" type="button"
            title="发送" aria-label="发送" disabled={!a.input.trim()}
            onClick={() => void a.sendMessage(a.input)}>
            <IconArrowUp size={16} />
          </button>
        )}
      </footer>
    </div>
  )
}

/**
 * 确认卡按 kind 分派。操作卡与结果卡都在**助手内**渲染：
 * 用户改值、重算预演、确认执行全程不离开助手，所以这里不出现任何路由跳转。
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

/**
 * 推理内容：默认收起——它是模型的思考过程，不是答案。
 *
 * 只有模型真的给出推理内容才渲染（服务端按 `reasoning_content` 透传），且**不落库**：
 * 它不进后续上下文，刷新会话后不再显示；这让"回复为什么这么写"在当场可查，
 * 又不为此付一份长期存储与隐私成本。
 */
function ReasoningBlock({ text }: { text: string }) {
  const [open, setOpen] = useState(false)
  return (
    <div className="erp-assistant-reasoning">
      <button
        type="button"
        className="erp-assistant-chip"
        aria-expanded={open}
        onClick={() => setOpen(value => !value)}
      >
        思考过程
        <span className="erp-assistant-tool-caret" aria-hidden="true">▾</span>
      </button>
      {open && <pre className="erp-assistant-reasoning-detail">{text}</pre>}
    </div>
  )
}

/** 踩的原因用**固定选项**：自由文本既没人愿意填，收集回来也没法统计。 */
const FEEDBACK_REASONS = ['答得不对', '答非所问', '数据查错了', '该说没权限却说没有', '太啰嗦']

/**
 * 赞 / 踩。点同一个方向第二次即取消（`rateMessage` 里换算成"取消"），
 * 所以界面上的按钮点得亮也点得灭——没有"点错了改不回来"的死角。
 */
function FeedbackButtons({
  bubble,
  onRate,
}: {
  bubble: Bubble
  onRate: (bubble: Bubble, feedback: 0 | 1 | -1, reason?: string) => Promise<void>
}) {
  const [asking, setAsking] = useState(false)
  return (
    <>
      <button
        className="btn btn-sm btn-ghost-secondary"
        type="button"
        title="这条回答有用"
        aria-label="赞这条回答"
        aria-pressed={bubble.feedback === 1}
        onClick={() => {
          setAsking(false)
          void onRate(bubble, 1)
        }}
      >
        <IconThumbUp size={14} />
      </button>
      <button
        className="btn btn-sm btn-ghost-secondary"
        type="button"
        title="这条回答有问题"
        aria-label="踩这条回答"
        aria-pressed={bubble.feedback === -1}
        onClick={() => {
          if (bubble.feedback === -1) {
            setAsking(false)
            void onRate(bubble, -1)
            return
          }
          setAsking(true)
        }}
      >
        <IconThumbDown size={14} />
      </button>
      {asking && (
        <span className="erp-assistant-feedback-reasons">
          {FEEDBACK_REASONS.map(reason => (
            <button
              key={reason}
              className="btn btn-sm btn-ghost-secondary"
              type="button"
              onClick={() => {
                setAsking(false)
                void onRate(bubble, -1, reason)
              }}
            >
              {reason}
            </button>
          ))}
          <button className="btn btn-sm btn-ghost-secondary" type="button" onClick={() => setAsking(false)}>
            取消
          </button>
        </span>
      )}
    </>
  )
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
