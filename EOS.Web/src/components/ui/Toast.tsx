import { IconAlertCircle, IconAlertTriangle, IconCircleCheck, IconInfoCircle, IconX } from '@tabler/icons-react'
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { ToastContext, type ToastOptions, type ToastVariant } from './toastContext'

/** 默认停留时长（毫秒） */
const DEFAULT_DURATION = 4000
/** 同屏最多几条：撞顶这类可能被连点的提示不该把屏幕铺满 */
const MAX_VISIBLE = 3
/** 淡出动画时长，与 app.css 的 .erp-toast-leaving 保持一致：动画走完再摘节点，否则"逐渐淡化"会被瞬间卸载吃掉 */
const EXIT_DURATION = 220

interface ToastItem {
  id: string
  message: ReactNode
  variant: ToastVariant
  duration: number
  /** 同一条文案被重复触发时自增，用来重启倒计时，而不是再堆一条出来 */
  serial: number
  /** 正在淡出：节点留到动画结束，且不再参与去重 */
  leaving: boolean
}

const VARIANT_ICONS: Record<ToastVariant, ReactNode> = {
  info: <IconInfoCircle size={16} />,
  success: <IconCircleCheck size={16} />,
  warning: <IconAlertTriangle size={16} />,
  danger: <IconAlertCircle size={16} />,
}

/**
 * 全局轻提示：瞬时、不需要用户决策的反馈走这里（撞顶、保存成功、复制成功等）。
 * 与页面内容相关的反馈仍用页内 alert / AsyncState；需要用户决策的用 Modal。
 * 三档口径见 docs/架构.md「设计系统最低约束」。
 */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<ToastItem[]>([])
  const seq = useRef(0)

  const dismiss = useCallback((id: string) => {
    setToasts((current) => {
      const target = current.find((toast) => toast.id === id)
      if (!target || target.leaving) return current
      return current.map((toast) => (toast.id === id ? { ...toast, leaving: true } : toast))
    })
    // 标记为淡出后延迟摘除：立即删节点会让淡出动画完全看不到
    window.setTimeout(() => {
      setToasts((current) => current.filter((toast) => toast.id !== id))
    }, EXIT_DURATION)
  }, [])

  const notify = useCallback(({ message, variant = 'info', duration = DEFAULT_DURATION }: ToastOptions) => {
    setToasts((current) => {
      // 正在淡出的那条不参与去重：它马上要消失，重新触发应当开一条新的
      const same = current.find((toast) => !toast.leaving && toast.message === message && toast.variant === variant)
      if (same) {
        return current.map((toast) => (toast.id === same.id ? { ...toast, duration, serial: toast.serial + 1 } : toast))
      }
      seq.current += 1
      const next: ToastItem[] = [...current, { id: `toast-${seq.current}`, message, variant, duration, serial: 0, leaving: false }]
      return next.length > MAX_VISIBLE ? next.slice(next.length - MAX_VISIBLE) : next
    })
  }, [])

  const api = useMemo(() => ({ notify, dismiss }), [notify, dismiss])

  return (
    <ToastContext.Provider value={api}>
      {children}
      <div className="erp-toast-container">
        {toasts.map((toast) => (
          <ToastCard key={toast.id} toast={toast} onDismiss={dismiss} />
        ))}
      </div>
    </ToastContext.Provider>
  )
}

function ToastCard({ toast, onDismiss }: { toast: ToastItem; onDismiss: (id: string) => void }) {
  const { id, duration, serial, variant } = toast

  useEffect(() => {
    if (duration <= 0) return undefined
    const timer = window.setTimeout(() => onDismiss(id), duration)
    return () => window.clearTimeout(timer)
  }, [duration, id, onDismiss, serial])

  return (
    // warning/danger 抢播报（role=alert），其余走礼貌播报（role=status）
    <div
      className={`alert alert-${variant} erp-toast mb-0${toast.leaving ? ' erp-toast-leaving' : ''}`}
      role={variant === 'warning' || variant === 'danger' ? 'alert' : 'status'}
    >
      <span className="erp-toast-icon" aria-hidden="true">{VARIANT_ICONS[variant]}</span>
      <span className="erp-toast-message">{toast.message}</span>
      <button type="button" className="erp-toast-close" aria-label="关闭提示" onClick={() => onDismiss(id)}>
        <IconX size={14} />
      </button>
    </div>
  )
}
