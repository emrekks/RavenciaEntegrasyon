import { useEffect, useRef, useState, type ButtonHTMLAttributes, type InputHTMLAttributes, type KeyboardEvent as ReactKeyboardEvent, type ReactNode, type SelectHTMLAttributes } from 'react'

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

export function LoadingState({ label = 'Yükleniyor' }: { label?: string }) {
  return <div className="rv-empty-state rv-loading-state" role="status" aria-live="polite"><span className="auth-status-loader" aria-hidden="true" /><p>{label}</p></div>
}

export function ErrorState({ title = 'Bir sorun oluştu', description, action }: { title?: string; description?: string; action?: ReactNode }) {
  return <div className="rv-empty-state rv-error-state" role="alert"><span className="rv-empty-icon" aria-hidden="true">!</span><h3>{title}</h3>{description && <p>{description}</p>}{action}</div>
}

export function SearchSelect({ label, value, placeholder = 'Seçin', options, onChange, hint, disabled = false }: { label: string; value: string; placeholder?: string; options: Array<{ value: string; label: string }>; onChange: (value: string) => void; hint?: string; disabled?: boolean }) {
  const [query, setQuery] = useState('')
  const [open, setOpen] = useState(false)
  const shellRef = useRef<HTMLDivElement>(null)
  const selected = options.find(option => option.value === value)
  const filtered = options.filter(option => option.label.toLocaleLowerCase('tr-TR').includes(query.trim().toLocaleLowerCase('tr-TR')))

  useEffect(() => {
    function onPointerDown(event: PointerEvent) {
      if (!shellRef.current?.contains(event.target as Node)) setOpen(false)
    }
    document.addEventListener('pointerdown', onPointerDown)
    return () => document.removeEventListener('pointerdown', onPointerDown)
  }, [])

  function onKeyDown(event: ReactKeyboardEvent<HTMLInputElement>) {
    if (event.key === 'Escape') setOpen(false)
    if (event.key === 'Enter' && filtered[0]) {
      event.preventDefault()
      onChange(filtered[0].value)
      setOpen(false)
      setQuery('')
    }
  }

  return <div ref={shellRef} className="rv-search-select"><label className="rv-field"><span className="rv-field-label">{label}</span><button type="button" className="rv-control rv-search-select-trigger" aria-haspopup="listbox" aria-expanded={open} disabled={disabled} onClick={() => setOpen(value => !value)}>{selected?.label ?? placeholder}<span aria-hidden="true">⌄</span></button>{hint && <small className="rv-field-hint">{hint}</small>}</label>{open && <div className="rv-search-select-panel" role="listbox"><input autoFocus className="rv-control" aria-label={`${label} ara`} placeholder="Ara..." value={query} onChange={event => setQuery(event.target.value)} onKeyDown={onKeyDown} />{filtered.length === 0 ? <p className="rv-search-select-empty">Sonuç bulunamadı.</p> : filtered.map(option => <button type="button" role="option" aria-selected={option.value === value} key={option.value} className={option.value === value ? 'is-selected' : ''} onClick={() => { onChange(option.value); setOpen(false); setQuery('') }}>{option.label}</button>)}</div>}</div>
}

export type ToastTone = 'success' | 'info' | 'warning' | 'error'

export type ToastItem = { id: string; title: string; message?: string; tone?: ToastTone }

export function ToastRegion({ items, onDismiss }: { items: ToastItem[]; onDismiss?: (id: string) => void }) {
  return <aside className="rv-toast-region" aria-live="polite" aria-label="Bildirimler">{items.map(item => <div key={item.id} className={`rv-toast rv-toast-${item.tone ?? 'info'}`} role="status"><span className="rv-toast-dot" aria-hidden="true" /><div><strong>{item.title}</strong>{item.message && <p>{item.message}</p>}</div>{onDismiss && <button type="button" className="rv-toast-dismiss" aria-label="Bildirimi kapat" onClick={() => onDismiss(item.id)}>×</button>}</div>)}</aside>
}

export function Modal({ open, title, onClose, children, className = '' }: { open: boolean; title: string; onClose: () => void; children: ReactNode; className?: string }) {
  useEffect(() => {
    if (!open) return
    function onKeyDown(event: KeyboardEvent) { if (event.key === 'Escape') onClose() }
    document.addEventListener('keydown', onKeyDown)
    return () => document.removeEventListener('keydown', onKeyDown)
  }, [open, onClose])
  if (!open) return null
  return <div className="rv-modal-backdrop" role="presentation" onMouseDown={event => { if (event.currentTarget === event.target) onClose() }}><section className={`rv-modal ${className}`.trim()} role="dialog" aria-modal="true" aria-labelledby="rv-modal-title"><header><h2 id="rv-modal-title">{title}</h2><button type="button" className="rv-button rv-button-icon" aria-label="Pencereyi kapat" onClick={onClose}>×</button></header><div className="rv-modal-body">{children}</div></section></div>
}

export function Drawer({ open, title, side = 'right', onClose, children, className = '' }: { open: boolean; title: string; side?: 'left' | 'right'; onClose: () => void; children: ReactNode; className?: string }) {
  useEffect(() => {
    if (!open) return
    function onKeyDown(event: KeyboardEvent) { if (event.key === 'Escape') onClose() }
    document.addEventListener('keydown', onKeyDown)
    return () => document.removeEventListener('keydown', onKeyDown)
  }, [open, onClose])
  if (!open) return null
  return <div className="rv-drawer-backdrop" role="presentation" onMouseDown={event => { if (event.currentTarget === event.target) onClose() }}><aside className={`rv-drawer rv-drawer-${side} ${className}`.trim()} role="dialog" aria-modal="true" aria-labelledby="rv-drawer-title"><header><h2 id="rv-drawer-title">{title}</h2><button type="button" className="rv-button rv-button-icon" aria-label="Paneli kapat" onClick={onClose}>×</button></header><div className="rv-drawer-body">{children}</div></aside></div>
}

export function DataTable({ children, caption, className = '' }: { children: ReactNode; caption?: string; className?: string }) {
  return <div className={`rv-data-table ${className}`.trim()}>{caption && <p className="rv-data-table-caption">{caption}</p>}<div className="rv-data-table-scroll"><table>{children}</table></div></div>
}

export function AppShell({ children, className = '' }: { children: ReactNode; className?: string }) {
  return <div className={`rv-app-shell ${className}`.trim()}>{children}</div>
}

export function Sidebar({ children, className = '' }: { children: ReactNode; className?: string }) {
  return <aside className={`rv-sidebar ${className}`.trim()}>{children}</aside>
}

export function Topbar({ children, className = '' }: { children: ReactNode; className?: string }) {
  return <header className={`rv-topbar ${className}`.trim()}>{children}</header>
}
