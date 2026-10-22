import { IconAlertCircle, IconAlertTriangle, IconCircleCheck, IconInfoCircle, IconX } from '@tabler/icons-react'
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { ToastContext, type ToastAction, type ToastOptions, type ToastVariant } from './toastContext'

/** 默认停留时长（毫秒） */
const DEFAULT_DURATION = 4000
/** 带动作按钮的提示停留更久：用户要看清文字再决定点不点 */
const ACTION_DURATION = 10_000
/** 同屏最多几条：撞顶这类可能被连点的提示不该把屏幕铺满 */
const MAX_VISIBLE = 3
/** 淡出动画时长，与 app.css 的 .erp-toast-leaving 保持一致：动画走完再摘节点，否则"逐渐淡化"会被瞬间卸载吃掉 */
const EXIT_DURATION = 220

interface ToastItem {
  id: string
  /** 业务键：同键提示原地更新 */
  key?: string
  message: ReactNode
  variant: ToastVariant
  duration: number
  /** 可选动作（复制报障编号一类） */
  action?: ToastAction
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

  const notify = useCallback(({ key, message, variant = 'info', duration, action }: ToastOptions) => {
    const effectiveDuration = duration ?? (action ? ACTION_DURATION : DEFAULT_DURATION)
    setToasts((current) => {
      // 正在淡出的那条不参与去重：它马上要消失，重新触发应当开一条新的。
      // 带业务键的提示按 key 命中（同一件事的不同阶段原地换文案/语义/动作）；
      // 不带的仍按「文案 + 语义」去重（既有行为）。
      const same = current.find((toast) => !toast.leaving
        && (key ? toast.key === key : toast.message === message && toast.variant === variant))
      if (same) {
        return current.map((toast) => (toast.id === same.id
          ? { ...toast, key, message, variant, duration: effectiveDuration, action, serial: toast.serial + 1 }
          : toast))
      }
      seq.current += 1
      const next: ToastItem[] = [...current, { id: `toast-${seq.current}`, key, message, variant, duration: effectiveDuration, action, serial: 0, leaving: false }]
      return next.length > MAX_VISIBLE ? next.slice(next.length - MAX_VISIBLE) : next
    })
  }, [])

  const dismissKey = useCallback((key: string) => {
    // 立即摘掉：这是"旧状态已失效"的清理，不需要淡出；也避免延迟计时器误删同键的新提示
    setToasts((current) => current.filter((toast) => toast.key !== key))
  }, [])

  const api = useMemo(() => ({ notify, dismiss, dismissKey }), [notify, dismiss, dismissKey])

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
  const { id, duration, serial, variant, action } = toast

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
      <span className="erp-toast-message">
        {toast.message}
        {action && (
          <button type="button" className="erp-toast-action" onClick={action.onClick}>{action.label}</button>
        )}
      </span>
      <button type="button" className="erp-toast-close" aria-label="关闭提示" onClick={() => onDismiss(id)}>
        <IconX size={14} />
      </button>
    </div>
  )
}
