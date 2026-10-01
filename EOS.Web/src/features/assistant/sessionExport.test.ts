import { describe, expect, it } from 'vitest'
import { buildSessionMarkdown, sanitizeFileName } from './sessionExport'
import type { AssistantMessage } from './types'

function message(overrides: Partial<AssistantMessage> = {}): AssistantMessage {
  return {
    id: 'm', sessionId: 's', role: 2, content: '', modelName: null,
    promptTokens: null, completionTokens: null, elapsedMs: null,
    correlationId: null, createdAt: '', ...overrides,
  }
}

describe('会话导出为 Markdown', () => {
  it('按角色分段并带时间，工具摘要单独留一节（导出的历史不该缺"当时用了什么"）', () => {
    const markdown = buildSessionMarkdown('采购单问题', [
      message({ id: 'm1', role: 1, content: '采购单主表是哪张？', createdAt: '2026-10-01T10:00:00Z' }),
      message({
        id: 'm2', role: 2, content: '主表是 PUR_PURCHASE_M。', createdAt: '2026-10-01T10:00:05Z',
        toolCalls: [{ name: 'describe_module', digest: '模块 #1606 采购单' }],
      }),
    ])

    expect(markdown).toContain('# 采购单问题')
    expect(markdown).toContain('## 我')
    expect(markdown).toContain('## 工作助手')
    expect(markdown).toContain('采购单主表是哪张？')
    expect(markdown).toContain('主表是 PUR_PURCHASE_M。')
    expect(markdown).toContain('> 2026-10-01T10:00:05Z')
    expect(markdown).toContain('工具调用：')
    expect(markdown).toContain('`describe_module`：模块 #1606 采购单')
  })

  it('系统行不进导出，没有工具调用时也不留空小节', () => {
    const markdown = buildSessionMarkdown('只有寒暄', [
      message({ id: 'm1', role: 3, content: '这是系统提示，不该出现在导出里' }),
      message({ id: 'm2', role: 1, content: '你好' }),
    ])

    expect(markdown).not.toContain('这是系统提示')
    expect(markdown).not.toContain('工具调用：')
    expect(markdown).toContain('你好')
  })

  it('空会话只留标题', () => {
    expect(buildSessionMarkdown('空会话', []).trim()).toBe('# 空会话')
  })
})

describe('导出文件名清洗', () => {
  it('中文、空格、括号、全角问号原样保留——导出名要就是会话名', () => {
    expect(sanitizeFileName('十月采购对账.md')).toBe('十月采购对账.md')
    expect(sanitizeFileName('收料单（1607）的主表是哪张？.md')).toBe('收料单（1607）的主表是哪张？.md')
  })

  it('只替换真正非法的字符（Windows 路径分隔符与通配符）', () => {
    expect(sanitizeFileName('a/b\\c:d*e?f"g<h>i|j.md')).toBe('a_b_c_d_e_f_g_h_i_j.md')
  })

  it('名字被清空时给兜底，不产生空文件名', () => {
    expect(sanitizeFileName('   ')).toBe('未命名')
    expect(sanitizeFileName('')).toBe('未命名')
  })
})
