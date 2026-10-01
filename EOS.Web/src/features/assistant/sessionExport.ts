import type { AssistantMessage } from './types'

/**
 * 会话导出为 Markdown。纯函数、无 React 依赖，便于单独断言。
 *
 * 角色分段 + 时间；**工具摘要另起一小节**——把"助手当时查过什么"也留住，
 * 否则导出的历史会缺一块：回答里的结论是从哪几个工具来的，看起来就无迹可寻。
 */
export function buildSessionMarkdown(title: string, messages: AssistantMessage[]): string {
  const lines: string[] = [`# ${title}`, '']
  for (const message of messages) {
    if (message.role !== 1 && message.role !== 2) continue
    lines.push(message.role === 1 ? '## 我' : '## 工作助手')
    if (message.createdAt) lines.push(`> ${message.createdAt}`)
    lines.push('', message.content.trim(), '')
    if (message.toolCalls && message.toolCalls.length > 0) {
      lines.push('工具调用：', ...message.toolCalls.map(tool => `- \`${tool.name}\`：${tool.digest}`), '')
    }
  }
  return lines.join('\n')
}

/** 触发一次文本文件下载（导出用）。文件名里的路径分隔符换成下划线，避免被当成目录。 */
export function downloadText(fileName: string, text: string) {
  const url = URL.createObjectURL(new Blob([text], { type: 'text/markdown;charset=utf-8' }))
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = fileName.replace(/[\\/:*?"<>|]/g, '_')
  document.body.append(anchor)
  anchor.click()
  anchor.remove()
  URL.revokeObjectURL(url)
}
