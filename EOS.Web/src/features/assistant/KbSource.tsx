import { useEffect, useState } from 'react'
import { Modal } from '../../components/ui/Modal'
import { getKbDocument } from './api'
import { parseKbLinks } from './kbSources'
import type { KbDocument } from './types'

/** 助手气泡文本：kb 来源引用渲染为可点击按钮，点击打开原文。 */
export function KbSourceText({ text, onOpen }: { text: string; onOpen: (docId: string) => void }) {
  const parts = parseKbLinks(text)
  if (parts.every(part => typeof part === 'string')) return <>{text}</>
  return (
    <>
      {parts.map((part, index) => typeof part === 'string' ? (
        <span key={index}>{part}</span>
      ) : (
        <button key={index} type="button" className="btn btn-link btn-sm p-0 align-baseline"
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
