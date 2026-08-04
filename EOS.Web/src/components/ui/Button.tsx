import type { ButtonHTMLAttributes, ReactNode } from 'react'

type ButtonVariant = 'primary' | 'secondary' | 'danger' | 'ghost'
type ButtonSize = 'sm' | 'md'

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant
  size?: ButtonSize
  icon?: ReactNode
  loading?: boolean
}

const variantClasses: Record<ButtonVariant, string> = {
  primary: 'btn-primary',
  secondary: 'btn-outline-secondary',
  danger: 'btn-danger',
  ghost: 'btn-ghost-secondary',
}

export function Button({
  children,
  className = '',
  variant = 'secondary',
  size = 'md',
  icon,
  loading = false,
  disabled,
  ...props
}: ButtonProps) {
  return (
    <button
      className={`btn ${variantClasses[variant]} ${size === 'sm' ? 'btn-sm' : ''} ${className}`.trim()}
      disabled={disabled || loading}
      {...props}
    >
      {loading ? <span className="spinner-border spinner-border-sm" aria-hidden="true" /> : icon}
      {children}
    </button>
  )
}
