import { useEffect, useRef, type ButtonHTMLAttributes, type CSSProperties, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes } from 'react'

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger'
export type ControlSize = 'sm' | 'md' | 'lg'
export type StatusTone = 'neutral' | 'info' | 'success' | 'warning' | 'danger'
export type FieldProps = { label?: string; hint?: string; error?: string; required?: boolean }

export function Button({ variant = 'primary', size = 'md', loading = false, className, disabled, children, ...props }: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: ButtonVariant; size?: ControlSize; loading?: boolean }) {
  return <button {...props} className={['rv-button', `rv-button-${variant}`, `rv-button-${size}`, className].filter(Boolean).join(' ')} disabled={disabled || loading}>{loading ? 'İşleniyor…' : children}</button>
}

export function IconButton({ label, size = 'md', variant = 'ghost', children, className, ...props }: ButtonHTMLAttributes<HTMLButtonElement> & { label: string; size?: ControlSize; variant?: ButtonVariant }) {
  return <button {...props} aria-label={label} title={props.title ?? label} className={['rv-icon-button', `rv-icon-button-${size}`, `rv-icon-button-${variant}`, className].filter(Boolean).join(' ')}>{children}</button>
}

export function TextField({ label, hint, error, required, className, ...props }: InputHTMLAttributes<HTMLInputElement> & FieldProps) {
  return <label className={['rv-field', className].filter(Boolean).join(' ')}><span>{label}{required && <b aria-hidden="true"> *</b>}</span><input {...props} aria-invalid={Boolean(error)} />{error ? <small className="rv-field-error">{error}</small> : hint ? <small className="rv-field-hint">{hint}</small> : null}</label>
}

export function SelectField({ label, hint, error, required, className, children, ...props }: SelectHTMLAttributes<HTMLSelectElement> & FieldProps) {
  return <label className={['rv-field', className].filter(Boolean).join(' ')}><span>{label}{required && <b aria-hidden="true"> *</b>}</span><select {...props} aria-invalid={Boolean(error)}>{children}</select>{error ? <small className="rv-field-error">{error}</small> : hint ? <small className="rv-field-hint">{hint}</small> : null}</label>
}

export function DateField(props: InputHTMLAttributes<HTMLInputElement> & FieldProps) { return <TextField type="date" {...props} /> }
export function SearchField({ label = 'Ara', ...props }: InputHTMLAttributes<HTMLInputElement> & FieldProps) { return <TextField label={label} type="search" {...props} /> }

export function FilterBar({ children, actions, className }: { children: ReactNode; actions?: ReactNode; className?: string }) {
  return <section className={['rv-filter-bar', className].filter(Boolean).join(' ')}><div className="rv-filter-fields">{children}</div>{actions ? <div className="rv-filter-actions">{actions}</div> : null}</section>
}

export function Tabs({ items, value, onChange, ariaLabel = 'Sekmeler' }: { items: Array<{ value: string; label: ReactNode; count?: ReactNode }>; value: string; onChange: (value: string) => void; ariaLabel?: string }) {
  return <div className="rv-tabs" role="tablist" aria-label={ariaLabel}>{items.map(item => <button key={item.value} type="button" role="tab" aria-selected={item.value === value} className={item.value === value ? 'is-active' : ''} onClick={() => onChange(item.value)}>{item.label}{item.count !== undefined ? <span className="rv-tab-count">{item.count}</span> : null}</button>)}</div>
}

export function Badge({ tone = 'neutral', children }: { tone?: StatusTone; children: ReactNode }) { return <span className={`rv-badge rv-badge-${tone}`}>{children}</span> }
export function StatusBadge({ tone, children }: { tone?: StatusTone; children: ReactNode }) { return <Badge tone={tone}>{children}</Badge> }

export function MetricCard({ label, value, detail, icon }: { label: string; value: ReactNode; detail?: ReactNode; icon?: ReactNode }) {
  return <article className="rv-metric-card">{icon ? <span className="rv-metric-icon" aria-hidden="true">{icon}</span> : null}<small>{label}</small><strong>{value}</strong>{detail ? <span>{detail}</span> : null}</article>
}

export type DataTableColumn<T> = { key: string; header: ReactNode; render: (row: T) => ReactNode; width?: string }
export function DataTable<T>({ columns, rows, getRowKey, empty }: { columns: Array<DataTableColumn<T>>; rows: T[]; getRowKey: (row: T, index: number) => string; empty?: ReactNode }) {
  if (!rows.length) return <EmptyState>{empty ?? 'Gösterilecek kayıt yok.'}</EmptyState>
  return <div className="rv-table-scroll"><table className="rv-table"><thead><tr>{columns.map(column => <th key={column.key} style={column.width ? { width: column.width } : undefined}>{column.header}</th>)}</tr></thead><tbody>{rows.map((row, index) => <tr key={getRowKey(row, index)}>{columns.map(column => <td key={column.key}>{column.render(row)}</td>)}</tr>)}</tbody></table></div>
}

export function Pagination({ page, hasNext, onPrevious, onNext }: { page: number; hasNext: boolean; onPrevious: () => void; onNext: () => void }) {
  return <nav className="rv-pagination" aria-label="Sayfalama"><Button variant="secondary" size="sm" onClick={onPrevious} disabled={page <= 1}>Önceki</Button><span>Sayfa {page}</span><Button variant="secondary" size="sm" onClick={onNext} disabled={!hasNext}>Sonraki</Button></nav>
}

export function Modal({ open, title, onClose, children, footer }: { open: boolean; title: string; onClose: () => void; children: ReactNode; footer?: ReactNode }) {
  const dialogRef = useRef<HTMLElement>(null)
  useEffect(() => {
    if (!open) return
    const previousOverflow = document.body.style.overflow
    const onKeyDown = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    document.body.style.overflow = 'hidden'
    document.addEventListener('keydown', onKeyDown)
    return () => { document.body.style.overflow = previousOverflow; document.removeEventListener('keydown', onKeyDown) }
  }, [onClose, open])
  useEffect(() => { if (open) dialogRef.current?.querySelector<HTMLElement>('button, input, select, textarea, [tabindex="0"]')?.focus() }, [open])
  if (!open) return null
  return <div className="rv-overlay" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) onClose() }}><section ref={dialogRef} className="rv-modal" role="dialog" aria-modal="true" aria-labelledby="rv-modal-title"><header><h2 id="rv-modal-title">{title}</h2><IconButton label="Kapat" variant="ghost" onClick={onClose}>×</IconButton></header><div className="rv-modal-body">{children}</div>{footer ? <footer>{footer}</footer> : null}</section></div>
}

export function Drawer({ open, title, onClose, children }: { open: boolean; title: string; onClose: () => void; children: ReactNode }) {
  const dialogRef = useRef<HTMLElement>(null)
  useEffect(() => {
    if (!open) return
    const previousOverflow = document.body.style.overflow
    const onKeyDown = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    document.body.style.overflow = 'hidden'
    document.addEventListener('keydown', onKeyDown)
    return () => { document.body.style.overflow = previousOverflow; document.removeEventListener('keydown', onKeyDown) }
  }, [onClose, open])
  useEffect(() => { if (open) dialogRef.current?.querySelector<HTMLElement>('button, input, select, textarea, [tabindex="0"]')?.focus() }, [open])
  return open ? <div className="rv-overlay" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) onClose() }}><aside ref={dialogRef} className="rv-drawer" role="dialog" aria-modal="true" aria-labelledby="rv-drawer-title"><header><h2 id="rv-drawer-title">{title}</h2><IconButton label="Kapat" variant="ghost" onClick={onClose}>×</IconButton></header><div className="rv-drawer-body">{children}</div></aside></div> : null
}

export function Toast({ children, tone = 'info' }: { children: ReactNode; tone?: StatusTone }) { return <div className={`rv-toast rv-toast-${tone}`} role="status">{children}</div> }
export function PageHeader({ eyebrow, title, description, actions }: { eyebrow?: string; title: string; description?: string; actions?: ReactNode }) { return <header className="rv-page-header"><div>{eyebrow ? <p className="rv-eyebrow">{eyebrow}</p> : null}<h1>{title}</h1>{description ? <p>{description}</p> : null}</div>{actions ? <div className="rv-page-actions">{actions}</div> : null}</header> }
export function EmptyState({ children }: { children: ReactNode }) { return <div className="rv-empty-state">{children}</div> }
export function LoadingState({ children = 'Yükleniyor…' }: { children?: ReactNode }) { return <div className="rv-loading-state" role="status"><span className="rv-spinner" aria-hidden="true" />{children}</div> }

export function controlStyle(size: ControlSize): CSSProperties { return { minHeight: `var(--rv-control-height-${size})` } }
