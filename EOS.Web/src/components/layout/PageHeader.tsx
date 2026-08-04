import type { ReactNode } from 'react'

interface PageHeaderProps {
  eyebrow?: string
  title: string
  description?: string
  actions?: ReactNode
}

export function PageHeader({ eyebrow, title, description, actions }: PageHeaderProps) {
  return (
    <div className="erp-page-heading">
      <div>
        {eyebrow && <div className="page-pretitle">{eyebrow}</div>}
        <h2 className="h2 mb-1">{title}</h2>
        {description && <p className="text-secondary mb-0">{description}</p>}
      </div>
      {actions && <div className="erp-page-actions">{actions}</div>}
    </div>
  )
}
