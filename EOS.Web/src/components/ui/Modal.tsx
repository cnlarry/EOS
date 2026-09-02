import type { CSSProperties, ReactNode } from 'react'

/**
 * 统一弹窗基础组件（代码质量批 3 C6 收编）：
 * 原 22 文件各自手写 `modal modal-blur show d-block` 壳（header/btn-close/aria 重复），
 * 收敛到本组件；支持标准三段（header/body/footer）、尺寸与滚动。
 */
export interface ModalProps {
  title: string
  onClose: () => void
  children: ReactNode
  footer?: ReactNode
  size?: 'sm' | 'lg' | 'xl'
  scrollable?: boolean
  ariaLabel?: string
  /** 自定义 dialog 类（存量弹窗如 erp-dialog-md/lg、erp-chooser-dialog 等，C6 迁移期兼容）。 */
  dialogClassName?: string
  /** body 内联样式（存量弹窗如审批历史 maxHeight 滚动）。 */
  bodyStyle?: CSSProperties
}

export function Modal({ title, onClose, children, footer, size, scrollable, ariaLabel, dialogClassName, bodyStyle }: ModalProps) {
  const dialogClass = [
    'modal-dialog',
    'modal-dialog-centered',
    size ? `modal-${size}` : '',
    scrollable ? 'modal-dialog-scrollable' : '',
    dialogClassName,
  ].filter(Boolean).join(' ')
  return (
    <div className="modal modal-blur show d-block" role="dialog" aria-modal="true" aria-label={ariaLabel ?? title}>
      <div className={dialogClass}>
        <div className="modal-content">
          <div className="modal-header">
            <h2 className="modal-title">{title}</h2>
            <button type="button" className="btn-close" aria-label="关闭" onClick={onClose} />
          </div>
          <div className="modal-body" style={bodyStyle}>{children}</div>
          {footer ? <div className="modal-footer">{footer}</div> : null}
        </div>
      </div>
    </div>
  )
}
