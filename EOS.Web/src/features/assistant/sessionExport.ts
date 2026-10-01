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

/**
 * 触发一次文本文件下载（会话导出用）。
 *
 * <para>
 * 两个细节都直接影响"导出文件名与会话名一致"：
 * ① 只清洗**真正非法**的字符（Windows 的 `\ / : * ? " < > |` 与控制字符），中文、空格、括号原样保留；
 * ② `revokeObjectURL` **延后**执行——创建完立刻撤销，部分浏览器会来不及取走数据，
 * 表现为下载被取消或退回默认文件名。
 * </para>
 */
export function downloadText(fileName: string, text: string) {
  const url = URL.createObjectURL(new Blob([text], { type: 'text/markdown;charset=utf-8' }))
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = sanitizeFileName(fileName)
  anchor.rel = 'noopener'
  document.body.append(anchor)
  anchor.click()
  anchor.remove()
  window.setTimeout(() => URL.revokeObjectURL(url), 1000)
}

/** 文件名清洗：替换路径分隔符与控制字符，其余（中文、空格、括号）保留原样。 */
export function sanitizeFileName(fileName: string): string {
  const cleaned = fileName
    // eslint-disable-next-line no-control-regex
    .replace(/[\\/:*?"<>|\u0000-\u001f]/g, '_')
    .replace(/\s+/g, ' ')
    .trim()
  return cleaned.length > 0 ? cleaned : '未命名'
}
