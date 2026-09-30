import { Fragment, useEffect, useState } from 'react'
import { Modal } from '../../components/ui/Modal'
import { getKbDocument } from './api'
import { parseKbLinks } from './kbSources'
import { renderMarkdown } from './markdown'
import type { KbDocument } from './types'

/**
 * 助手气泡文本：按 Markdown 渲染（GFM 子集），并把 kb 来源引用做成可点击按钮。
 * 来源引用是裸文本 `kb://doc/{id}#c{n}`，因此挂在行内文本钩子上——Markdown 与来源识别互不干扰。
 */
export function KbSourceText({ text, onOpen }: { text: string; onOpen: (docId: string) => void }) {
  return (
    <div className="erp-assistant-md">
      {renderMarkdown(text, {
        renderText: segment => <KbSegments text={segment} onOpen={onOpen} />,
      })}
    </div>
  )
}

/** 一段纯文本里的来源引用切分渲染；没有引用时原样输出（不额外包节点）。 */
function KbSegments({ text, onOpen }: { text: string; onOpen: (docId: string) => void }) {
  const parts = parseKbLinks(text)
  if (parts.every(part => typeof part === 'string')) return <>{text}</>
  return (
    <>
      {parts.map((part, index) => typeof part === 'string' ? (
        <Fragment key={index}>{part}</Fragment>
      ) : (
        <button key={index} type="button" className="erp-assistant-kb-link"
          title={`查看知识库来源（文档 ${part.docId}，第 ${part.chunk} 段）`}
          onClick={() => onOpen(part.docId)}>
          [来源 {part.docId}#{part.chunk}]
        </button>
      ))}
    </>
  )
}

/** 来源原文对话框：经服务端再次鉴权，不可见文档显示不存在。 */
export function KbDocDialog({ docId, onClose }: { docId: string; onClose: () => void }) {
  const [doc, setDoc] = useState<KbDocument | null>(null)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    setDoc(null)
    setFailed(false)
    getKbDocument(docId).then(setDoc).catch(() => setFailed(true))
  }, [docId])

  return (
    <Modal title={doc ? `知识库来源：${doc.title}` : '知识库来源'} onClose={onClose} size="lg" scrollable>
      {!doc && !failed && <div className="text-secondary">加载中…</div>}
      {failed && <div className="erp-assistant-error" role="alert">来源不存在或不在你的可见范围。</div>}
      {doc && (
        <>
          {doc.sourceUri && <div className="text-secondary small mb-2">出处：{doc.sourceUri}</div>}
          {doc.chunks.map(chunk => (
            <section key={chunk.serialNo} className="mb-2">
              <div className="text-secondary small">第 {chunk.serialNo} 段</div>
              <p className="mb-1">{chunk.content}</p>
            </section>
          ))}
        </>
      )}
    </Modal>
  )
}
