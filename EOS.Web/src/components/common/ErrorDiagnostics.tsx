import { useState } from 'react'
import { IconCopy } from '@tabler/icons-react'
import { copyDiagnostics, currentAppInfo, reportIdOf } from '../../lib/diagnostics'

/**
 * 页内错误诊断条：把「报障编号」显示出来并允许一键复制。
 * 页面已有的 alert/AsyncState 显示错误文案，本组件补的是"用户能把编号交给维护者"这一步——
 * 没有它，用户只能念一串 32 位十六进制，实际等同于没有编号。
 */
export function ErrorDiagnostics({ error, className = 'mt-2' }: { error: unknown; className?: string }) {
  const [copied, setCopied] = useState(false)
  const reportId = reportIdOf(error)

  const copy = () => {
    copyDiagnostics(currentAppInfo(), reportId)
    setCopied(true)
    window.setTimeout(() => setCopied(false), 2000)
  }

  return (
    <div className={`d-flex flex-wrap align-items-center gap-2 ${className}`.trim()}>
      <span className="text-secondary small">
        报障编号：{reportId ? <code>{reportId}</code> : '（本次请求未返回，可复制页面诊断信息）'}
      </span>
      <button type="button" className="erp-command-btn" onClick={copy}>
        <IconCopy size={16} aria-hidden="true" />
        {copied ? '已复制' : reportId ? '复制报障编号' : '复制诊断信息'}
      </button>
    </div>
  )
}
