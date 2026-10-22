import { createContext, useContext, type ReactNode } from 'react'

export type ToastVariant = 'info' | 'success' | 'warning' | 'danger'

export interface ToastAction {
  /** 动作按钮文字，如「复制报障编号」 */
  label: string
  onClick: () => void
}

export interface ToastOptions {
  /**
   * 业务键：同一个键的提示原地更新（文案、语义、动作一起换），而不是再叠一条。
   * 适合"列顺序已调整 → 保存失败 → 重试成功"这类同一件事的连续状态。
   * 不传时按「文案 + 语义」去重（既有行为）。
   */
  key?: string
  /** 提示正文 */
  message: ReactNode
  /** 语义类型（对应 Tabler 的 alert 语义），默认 info */
  variant?: ToastVariant
  /** 自动消失毫秒数；0 表示不自动消失，只能手动关闭 */
  duration?: number
  /** 可选动作（如把报障编号复制给用户）；有动作时默认停留更久，避免来不及点 */
  action?: ToastAction
}

export interface ToastApi {
  notify: (options: ToastOptions) => void
  dismiss: (id: string) => void
  /** 按业务键清掉提示（例如列配置被其他入口改写后，旧的「撤销列顺序」不再有效） */
  dismissKey: (key: string) => void
}

export const ToastContext = createContext<ToastApi | null>(null)

export function useToast() {
  const context = useContext(ToastContext)
  if (!context) throw new Error('useToast must be used within ToastProvider')
  return context
}
