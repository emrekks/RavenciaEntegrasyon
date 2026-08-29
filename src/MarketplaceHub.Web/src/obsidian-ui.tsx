import type { ButtonHTMLAttributes, InputHTMLAttributes, ReactNode, SelectHTMLAttributes } from 'react'

type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger' | 'icon'

export function Button({ variant = 'secondary', className = '', ...props }: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: ButtonVariant }) {
  return <button {...props} className={`rv-button rv-button-${variant} ${className}`.trim()} />
}

export function Surface({ as: Tag = 'section', className = '', children }: { as?: 'div' | 'section' | 'article'; className?: string; children: ReactNode }) {
  return <Tag className={`rv-surface ${className}`.trim()}>{children}</Tag>
}

export function Field({ label, hint, error, children, className = '' }: { label: string; hint?: string; error?: string; children: ReactNode; className?: string }) {
  return <label className={`rv-field ${className}`.trim()}><span className="rv-field-label">{label}</span>{children}{error ? <small className="rv-field-error" role="alert">{error}</small> : hint ? <small className="rv-field-hint">{hint}</small> : null}</label>
}

export function TextField(props: InputHTMLAttributes<HTMLInputElement>) {
  return <input {...props} className={`rv-control ${props.className ?? ''}`.trim()} />
}

export function SelectField(props: SelectHTMLAttributes<HTMLSelectElement>) {
  return <select {...props} className={`rv-control ${props.className ?? ''}`.trim()} />
}

const statusLabels: Record<string, string> = {
  ACTIVE: 'Aktif', CONNECTED: 'Bağlı', VERIFIED: 'Doğrulandı', SUCCESS: 'Başarılı', SUCCEEDED: 'Başarılı',
  PENDING: 'Bekliyor', PROCESSING: 'İşleniyor', RUNNING: 'Çalışıyor', FAILED: 'Hata', ERROR: 'Hata',
  APPROVED: 'Onaylandı', CANCELLED: 'İptal edildi', DISABLED: 'Pasif'
}

export function StatusBadge({ value, tone, className = '' }: { value: string; tone?: 'good' | 'warning' | 'danger' | 'info' | 'neutral'; className?: string }) {
  const normalized = value.toUpperCase()
  const inferred = tone ?? (['ACTIVE', 'CONNECTED', 'VERIFIED', 'SUCCESS', 'SUCCEEDED', 'APPROVED'].includes(normalized) ? 'good' : ['FAILED', 'ERROR'].includes(normalized) ? 'danger' : ['PENDING', 'PROCESSING', 'RUNNING'].includes(normalized) ? 'info' : 'neutral')
  return <span className={`rv-status-badge rv-status-${inferred} ${className}`.trim()}>{statusLabels[normalized] ?? value}</span>
}

export function PageHeader({ eyebrow, title, description, actions }: { eyebrow?: string; title: string; description?: string; actions?: ReactNode }) {
  return <header className="rv-page-header"><div>{eyebrow && <p className="rv-eyebrow">{eyebrow}</p>}<h1>{title}</h1>{description && <p className="rv-page-description">{description}</p>}</div>{actions && <div className="rv-page-actions">{actions}</div>}</header>
}

export function EmptyState({ title, description, action }: { title: string; description?: string; action?: ReactNode }) {
  return <div className="rv-empty-state"><span className="rv-empty-icon" aria-hidden="true">—</span><h3>{title}</h3>{description && <p>{description}</p>}{action}</div>
}
