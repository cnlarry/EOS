import { useCallback, useRef, useState } from 'react'
import type { ChatSituation } from './situationSource'
import type {
  AssistantAdminDraft,
  AssistantApprovalRequestPreview,
  AssistantConfigApplyResult,
  AssistantConfigDiff,
  AssistantRecordActionPreview,
  AssistantRecordActionResult,
} from './types'

/**
 * SSE 事件：delta=文本增量；reasoning=推理内容（只展示，不落库）；tool_start / tool_result=工具执行进度
 * （工具轮里模型常常一次给好几个调用，执行期间不发进度的话界面上是一片空白，会被读成卡死）；
 * done=回复已落库（含工具摘要、表单草稿与截断原因）；error=流中失败。
 */
export type ChatStreamEvent =
  | { event: 'delta'; text: string }
  | { event: 'reasoning'; text: string }
  | { event: 'tool_start'; name: string }
  | { event: 'tool_result'; name: string; digest: string; ok: boolean }
  | {
      event: 'done'
      message: unknown
      toolCalls?: Array<{ name: string; digest: string }>
      drafts?: AssistantDraft[]
      /** 完成原因；`length` = 被输出上限截断（界面要如实提示"还没写完"）。 */
      finishReason?: string | null
    }
  | { event: 'error'; code: string; message: string }

/**
 * 确认卡载荷七种：表单草稿（带入表单）、元数据变更集（确认执行）、
 * 记录动作的预演（就地操作卡）、记录动作的执行结果（回读结果）、
 * 配置改动对照卡（逐项勾选后应用）、配置改动的应用结果、
 * 批核族的操作请求卡（逐行勾选确认后由界面直调既有端点）。
 */
export type AssistantDraft =
  | AssistantFormDraft
  | AssistantAdminDraft
  | AssistantRecordActionPreview
  | AssistantRecordActionResult
  | AssistantConfigDiff
  | AssistantConfigApplyResult
  | AssistantApprovalRequestPreview

/** 表单草稿：前端确认后经现有保存管线执行，助手不新增写路径。 */
export interface AssistantFormDraft {
  moduleId: number
  moduleTitle: string
  values: Record<string, string>
  labels?: Record<string, string>
  missingRequired: string[]
  unknownKeys: string[]
  warnings: string[]
}

/** 随 chat 请求上报的处境（路由 + 界面状态总线采集）。 */
export type ChatPageContext = ChatSituation

interface SendOptions {
  sessionId: string
  content: string
  pageContext?: ChatPageContext | null
  onDelta: (text: string) => void
  /** 推理内容增量（模型有才有）。它只用于展示，服务端不落库。 */
  onReasoning?: (text: string) => void
  /** 某个工具开始执行：界面据此显示"正在调用 X"，替代此前的空白等待。 */
  onToolStart?: (name: string) => void
  /** 某个工具返回（digest 与最终工具卡同源，ok 用于区分"查到"与"没查到"）。 */
  onToolResult?: (name: string, digest: string, ok: boolean) => void
  onDone?: (
    message: unknown,
    toolCalls?: Array<{ name: string; digest: string }>,
    drafts?: AssistantDraft[],
    finishReason?: string | null,
  ) => void
  onError?: (code: string, message: string) => void
}

function parseProblem(message: string): string {
  try {
    const body = JSON.parse(message) as { message?: string; title?: string }
    return body.message ?? body.title ?? '请求失败。'
  } catch {
    return '请求失败。'
  }
}

/**
 * 消费 POST SSE 流（EventSource 只支持 GET，故用 fetch + ReadableStream）。
 * 断流语义：用户主动停止或网络中断时保留已收到的增量文本，由调用方决定重发；
 * 服务端在断开时会中止模型调用、不落库半截回复。
 */
export function useChatStream() {
  const [streaming, setStreaming] = useState(false)
  const abortRef = useRef<AbortController | null>(null)

  const stop = useCallback(() => {
    abortRef.current?.abort()
    abortRef.current = null
  }, [])

  const send = useCallback(async (options: SendOptions): Promise<'done' | 'error' | 'aborted'> => {
    const controller = new AbortController()
    abortRef.current = controller
    setStreaming(true)
    let outcome: 'done' | 'error' | 'aborted' = 'aborted'
    try {
      const response = await fetch(`/api/v1/assistant/sessions/${options.sessionId}/chat`, {
        method: 'POST',
        credentials: 'include',
        headers: {
          'Content-Type': 'application/json',
          'X-Client-Id': 'eos.web',
          'X-Correlation-Id': newCorrelationId(),
        },
        body: JSON.stringify({ content: options.content, context: options.pageContext ?? null }),
        signal: controller.signal,
      })
      if (!response.ok || !response.body) {
        const raw = await response.text().catch(() => '')
        options.onError?.(`HTTP_${response.status}`, parseProblem(raw))
        setStreaming(false)
        abortRef.current = null
        return 'error'
      }

      const reader = response.body.getReader()
      const decoder = new TextDecoder()
      let buffer = ''
      for (;;) {
        const { done, value } = await reader.read()
        if (done) break
        buffer += decoder.decode(value, { stream: true })
        // SSE 帧以空行分隔；逐帧解析 event/data 两行
        let separator = buffer.indexOf('\n\n')
        while (separator >= 0) {
          const frame = buffer.slice(0, separator)
          buffer = buffer.slice(separator + 2)
          const evt = parseFrame(frame)
          if (evt) {
            switch (evt.event) {
              case 'delta':
                options.onDelta(evt.text)
                break
              case 'reasoning':
                options.onReasoning?.(evt.text)
                break
              case 'tool_start':
                options.onToolStart?.(evt.name)
                break
              case 'tool_result':
                options.onToolResult?.(evt.name, evt.digest, evt.ok)
                break
              case 'done':
                outcome = 'done'
                options.onDone?.(evt.message, evt.toolCalls, evt.drafts, evt.finishReason)
                break
              case 'error':
                outcome = 'error'
                options.onError?.(evt.code, evt.message)
                break
            }
          }
          separator = buffer.indexOf('\n\n')
        }
      }
    } catch (error) {
      if (!(error instanceof DOMException && error.name === 'AbortError')) {
        options.onError?.('NETWORK_ERROR', error instanceof Error ? error.message : '连接中断。')
        outcome = 'error'
      }
    } finally {
      setStreaming(false)
      if (abortRef.current === controller) abortRef.current = null
    }
    return outcome
  }, [])

  return { send, stop, streaming }
}

function parseFrame(frame: string): ChatStreamEvent | null {
  let eventName = 'message'
  let data = ''
  for (const line of frame.split('\n')) {
    if (line.startsWith('event:')) eventName = line.slice(6).trim()
    else if (line.startsWith('data:')) data += line.slice(5).trim()
  }
  if (!data) return null
  try {
    const payload = JSON.parse(data) as Record<string, unknown>
    switch (eventName) {
      case 'delta':
        return { event: 'delta', text: typeof payload.text === 'string' ? payload.text : '' }
      case 'reasoning':
        return { event: 'reasoning', text: typeof payload.text === 'string' ? payload.text : '' }
      case 'tool_start':
        return { event: 'tool_start', name: typeof payload.name === 'string' ? payload.name : '' }
      case 'tool_result':
        return {
          event: 'tool_result',
          name: typeof payload.name === 'string' ? payload.name : '',
          digest: typeof payload.digest === 'string' ? payload.digest : '',
          ok: payload.ok !== false,
        }
      case 'done':
        return {
          event: 'done',
          message: (payload as { message?: unknown }).message ?? payload,
          toolCalls: (payload as { toolCalls?: Array<{ name: string; digest: string }> }).toolCalls,
          drafts: (payload as { drafts?: AssistantDraft[] }).drafts,
          finishReason: (payload as { finishReason?: string | null }).finishReason ?? null,
        }
      case 'error':
        return {
          event: 'error',
          code: typeof payload.code === 'string' ? payload.code : 'UNKNOWN',
          message: typeof payload.message === 'string' ? payload.message : '助手调用失败。',
        }
      default:
        return null
    }
  } catch {
    return null
  }
}

function newCorrelationId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID()
  }
  return `eos-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`
}
