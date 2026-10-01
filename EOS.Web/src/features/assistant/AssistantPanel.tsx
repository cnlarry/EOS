import {
  IconArchive,
  IconArrowUp,
  IconBookmark,
  IconDots,
  IconDownload,
  IconLayoutSidebarRightExpand,
  IconMaximize,
  IconPencil,
  IconPlayerStop,
  IconPlus,
  IconRobot,
  IconX,
} from '@tabler/icons-react'
import { Fragment, useCallback, useEffect, useRef, useState } from 'react'
import { archiveSession, listMessages, renameSession } from './api'
import { AssistantMemoryPanel } from './AssistantMemoryPanel'
import { KbDocDialog, KbSourceText } from './KbSource'
import { useAssistant, type Bubble } from './assistantContext'
import { describePending, describeWhere, digestKindLabel } from './assistantText'
import { buildSessionMarkdown, downloadText } from './sessionExport'
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
  const [menuOpen, setMenuOpen] = useState(false)
  const [renaming, setRenaming] = useState(false)
  const [renameValue, setRenameValue] = useState('')
  const [memoryOpen, setMemoryOpen] = useState(false)
  const [openDocId, setOpenDocId] = useState<string | null>(null)
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

  const currentArchived = a.sessions.find(item => item.id === a.sessionId)?.archivedAt != null

  return (
    <div className={`erp-assistant-panel is-${variant}`}>
      <header className="erp-assistant-header">
        <IconRobot size={20} />
        <span className="erp-assistant-title">工作助手</span>
        <button className="btn btn-icon btn-sm btn-ghost-secondary" type="button"
          title="新对话" aria-label="新建会话"
          onClick={() => void a.newSession()}>
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
            value={a.sessionId ?? ''}
            onChange={(event) => a.setSessionId(event.target.value || null)}
            aria-label="选择会话"
          >
            {a.sessions.length === 0 && <option value="">暂无会话</option>}
            {a.sessions.map(session => (
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
          <div key={bubble.key} className={`erp-assistant-bubble ${bubble.role === 1 ? 'is-user' : 'is-assistant'}`}>
            {bubble.role === 2 && bubble.text
              ? <KbSourceText text={bubble.text} onOpen={setOpenDocId} />
              : (bubble.text || (bubble.streaming ? '' : '(空回复)'))}
            {bubble.role === 1 && bubble.text && !bubble.streaming && (
              <button className="btn btn-sm btn-ghost-secondary mt-1" type="button"
                title="把这句话记下来（仅本人可见）"
                aria-label={`记住这条消息：${bubble.text.slice(0, 12)}`}
                disabled={a.remembered.has(bubble.key)}
                onClick={() => void a.remember(bubble)}>
                {a.remembered.has(bubble.key) ? '已记住 ✓' : '记住'}
              </button>
            )}
            {bubble.streaming && <span className="erp-assistant-cursor" aria-hidden="true">▍</span>}
            {!bubble.streaming && bubble.tools && bubble.tools.length > 0 && (
              <ToolCalls tools={bubble.tools} />
            )}
            {!bubble.streaming && bubble.drafts && bubble.drafts.length > 0 && (
              <div className="erp-assistant-drafts">
                {bubble.drafts.map((draft, index) => renderDraft(draft, index, a.openInForm))}
              </div>
            )}
            {/* 操作按钮排在内容之后（工具卡/草稿卡之下）：先读完，再决定要不要复制 */}
            {bubble.role === 2 && bubble.text && !bubble.streaming && <CopyButton text={bubble.text} />}
          </div>
        ))}
      </div>

      {a.errorText && (
        <div className="erp-assistant-error" role="alert">{a.errorText}</div>
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
