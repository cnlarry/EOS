const KB_LINK_PATTERN = /kb:\/\/doc\/(\d+)#c(\d+)/g

export interface KbLinkPart {
  docId: string
  chunk: number
}

export type KbTextPart = string | KbLinkPart

export function isKbLinkPart(part: KbTextPart): part is KbLinkPart {
  return typeof part !== 'string'
}

/** 把助手文本中的 kb://doc/{id}#c{n} 引用切成可渲染片段。 */
export function parseKbLinks(text: string): KbTextPart[] {
  const parts: KbTextPart[] = []
  let lastIndex = 0
  KB_LINK_PATTERN.lastIndex = 0
  let match: RegExpExecArray | null
  while ((match = KB_LINK_PATTERN.exec(text)) !== null) {
    if (match.index > lastIndex) parts.push(text.slice(lastIndex, match.index))
    parts.push({ docId: match[1], chunk: Number(match[2]) })
    lastIndex = match.index + match[0].length
  }
  if (lastIndex < text.length) parts.push(text.slice(lastIndex))
  return parts
}
