import { useEffect, useId, useRef, useState, type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes, type TextareaHTMLAttributes } from 'react'
import { UiIcon, type UiIconName } from '../components/UiIcon'

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger'
export type ButtonSize = 'sm' | 'md'

type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: ButtonVariant
  size?: ButtonSize
}

export function Button({ variant = 'primary', size = 'sm', className = '', children, ...props }: ButtonProps) {
  return <button className={`rv-button rv-button--${variant} rv-button--${size}${className ? ` ${className}` : ''}`} {...props}>{children}</button>
}

type IconButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  icon: UiIconName
  label: string
  size?: number
  variant?: ButtonVariant
}

export function IconButton({ icon, label, size = 16, variant = 'ghost', className = '', ...props }: IconButtonProps) {
  return <button className={`rv-icon-button rv-icon-button--${variant}${className ? ` ${className}` : ''}`} aria-label={label} title={label} {...props}><UiIcon name={icon} size={size} /></button>
}

export function PageHeader({ eyebrow, title, description, actions, className = '' }: { eyebrow?: string; title: ReactNode; description?: ReactNode; actions?: ReactNode; className?: string }) {
  return <header className={`rv-page-header${className ? ` ${className}` : ''}`}><div><p className="rv-page-header__eyebrow">{eyebrow}</p><h1>{title}</h1>{description && <p className="rv-page-header__description">{description}</p>}</div>{actions && <div className="rv-page-header__actions">{actions}</div>}</header>
}

export type TabItem = { value: string; label: ReactNode; count?: ReactNode; disabled?: boolean }

export function Tabs({ items, value, onChange, ariaLabel = 'Sekmeler', className = '' }: { items: TabItem[]; value: string; onChange: (value: string) => void; ariaLabel?: string; className?: string }) {
  return <div className={`rv-tabs${className ? ` ${className}` : ''}`} role="tablist" aria-label={ariaLabel}>{items.map(item => <button key={item.value} type="button" role="tab" aria-selected={item.value === value} disabled={item.disabled} className="rv-tabs__tab" onClick={() => onChange(item.value)}><span>{item.label}</span>{item.count !== undefined && <span className="rv-tabs__count">{item.count}</span>}</button>)}</div>
}

export type MenuItem = { id: string; label: ReactNode; icon?: UiIconName; danger?: boolean; disabled?: boolean; onSelect?: () => void }

export function DropdownMenu({ label = 'Menü', items, children }: { label?: string; items: MenuItem[]; children?: ReactNode }) {
  const [open, setOpen] = useState(false)
  const rootRef = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!open) return
    const close = (event: PointerEvent) => { if (!rootRef.current?.contains(event.target as Node)) setOpen(false) }
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(false) }
    document.addEventListener('pointerdown', close)
    document.addEventListener('keydown', escape)
    return () => { document.removeEventListener('pointerdown', close); document.removeEventListener('keydown', escape) }
  }, [open])
  return <div ref={rootRef} className="rv-dropdown"><Button variant="secondary" size="sm" aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen(current => !current)}>{children ?? label}<UiIcon name="chevronDown" size={14} /></Button>{open && <div className="rv-dropdown__menu" role="menu">{items.map(item => <button key={item.id} type="button" role="menuitem" disabled={item.disabled} className={item.danger ? 'is-danger' : undefined} onClick={() => { item.onSelect?.(); setOpen(false) }}>{item.icon && <UiIcon name={item.icon} size={14} />}{item.label}</button>)}</div>}</div>
}

export function Popover({ open, onOpenChange, label, children }: { open?: boolean; onOpenChange?: (open: boolean) => void; label: ReactNode; children: ReactNode }) {
  const [internalOpen, setInternalOpen] = useState(false)
  const isOpen = open ?? internalOpen
  const setOpen = (next: boolean) => { setInternalOpen(next); onOpenChange?.(next) }
  const ref = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!isOpen) return
    const close = (event: PointerEvent) => { if (!ref.current?.contains(event.target as Node)) setOpen(false) }
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(false) }
    document.addEventListener('pointerdown', close)
    document.addEventListener('keydown', escape)
    return () => { document.removeEventListener('pointerdown', close); document.removeEventListener('keydown', escape) }
  }, [isOpen])
  return <div ref={ref} className="rv-popover"><Button variant="secondary" size="sm" aria-expanded={isOpen} onClick={() => setOpen(!isOpen)}>{label}</Button>{isOpen && <div className="rv-popover__content">{children}</div>}</div>
}

export function Field({ label, hint, error, children, className = '' }: { label: ReactNode; hint?: ReactNode; error?: ReactNode; children: ReactNode; className?: string }) {
  return <label className={`rv-field${className ? ` ${className}` : ''}`}><span>{label}</span>{children}{error ? <span className="rv-field__error">{error}</span> : hint ? <span className="rv-field__hint">{hint}</span> : null}</label>
}

export function SelectField({ label, hint, children, ...props }: SelectHTMLAttributes<HTMLSelectElement> & { label: ReactNode; hint?: ReactNode }) {
  return <Field label={label} hint={hint}><select {...props}>{children}</select></Field>
}

export function SearchField({ label = 'Ara', ...props }: InputHTMLAttributes<HTMLInputElement> & { label?: string }) {
  return <div className="rv-search-field"><UiIcon name="search" size={15} /><input type="search" aria-label={label} {...props} /></div>
}

export function Toolbar({ children, className = '' }: { children: ReactNode; className?: string }) {
  return <div className={`rv-toolbar${className ? ` ${className}` : ''}`}>{children}</div>
}

export function FilterBar({ children, className = '' }: { children: ReactNode; className?: string }) {
  return <div className={`rv-filter-bar${className ? ` ${className}` : ''}`}>{children}</div>
}

export function Badge({ children, tone = 'neutral', className = '' }: { children: ReactNode; tone?: 'neutral' | 'success' | 'warning' | 'danger' | 'info'; className?: string }) {
  return <span className={`rv-badge rv-badge--${tone}${className ? ` ${className}` : ''}`}>{children}</span>
}

export function DataTable({ children, className = '' }: { children: ReactNode; className?: string }) {
  return <div className={`rv-data-table${className ? ` ${className}` : ''}`}><div className="rv-data-table__scroll">{children}</div></div>
}

export function Pagination({ page, pageCount, onChange, label }: { page: number; pageCount: number; onChange: (page: number) => void; label?: ReactNode }) {
  return <nav className="rv-pagination" aria-label="Sayfalama"><span>{label ?? `${page} / ${pageCount}`}</span><span className="rv-pagination__actions"><IconButton icon="chevronLeft" label="Önceki sayfa" disabled={page <= 1} onClick={() => onChange(page - 1)} /><IconButton icon="chevronRight" label="Sonraki sayfa" disabled={page >= pageCount} onClick={() => onChange(page + 1)} /></span></nav>
}

export function Modal({ open, title, description, onClose, children, footer }: { open: boolean; title: string; description?: ReactNode; onClose: () => void; children: ReactNode; footer?: ReactNode }) {
  useEscape(onClose, open)
  if (!open) return null
  return <><div className="rv-modal-backdrop" onMouseDown={onClose} /><section className="rv-modal" role="dialog" aria-modal="true" aria-label={title} onMouseDown={event => event.stopPropagation()}><header className="rv-modal__header"><div><h2 className="rv-modal__title">{title}</h2>{description && <p className="rv-modal__description">{description}</p>}</div><IconButton icon="close" label="Kapat" onClick={onClose} /></header><div className="rv-modal__body">{children}</div>{footer && <footer className="rv-modal__footer">{footer}</footer>}</section></>
}

export function Drawer({ open, title, description, onClose, children, footer }: { open: boolean; title: string; description?: ReactNode; onClose: () => void; children: ReactNode; footer?: ReactNode }) {
  useEscape(onClose, open)
  if (!open) return null
  return <><div className="rv-drawer-backdrop" onMouseDown={onClose} /><aside className="rv-drawer" role="dialog" aria-modal="true" aria-label={title}><header className="rv-drawer__header"><div><h2 className="rv-modal__title">{title}</h2>{description && <p className="rv-modal__description">{description}</p>}</div><IconButton icon="close" label="Kapat" onClick={onClose} /></header><div className="rv-drawer__body">{children}</div>{footer && <footer className="rv-drawer__footer">{footer}</footer>}</aside></>
}

export function ConfirmDialog({ open, title, description, confirmLabel = 'Onayla', onConfirm, onClose, danger = false }: { open: boolean; title: string; description?: ReactNode; confirmLabel?: string; onConfirm: () => void; onClose: () => void; danger?: boolean }) {
  return <Modal open={open} title={title} description={description} onClose={onClose} footer={<><Button variant="secondary" onClick={onClose}>Vazgeç</Button><Button variant={danger ? 'danger' : 'primary'} onClick={onConfirm}>{confirmLabel}</Button></>}><p>{description}</p></Modal>
}

export function Toast({ message, tone = 'neutral', onClose }: { message: ReactNode; tone?: 'neutral' | 'success' | 'error'; onClose?: () => void }) {
  return <div className={`rv-toast${tone === 'success' ? ' rv-toast--success' : tone === 'error' ? ' rv-toast--error' : ''}`} role="status"><span>{message}</span>{onClose && <IconButton icon="close" label="Bildirimi kapat" onClick={onClose} />}</div>
}

export function EmptyState({ title, description, action }: { title: string; description?: ReactNode; action?: ReactNode }) {
  return <div className="rv-empty"><strong>{title}</strong>{description && <span>{description}</span>}{action}</div>
}

export function LoadingState({ label = 'Yükleniyor…' }: { label?: string }) {
  return <div className="rv-loading" role="status"><UiIcon name="loader" size={20} /><span>{label}</span></div>
}

function useEscape(onClose: () => void, enabled: boolean) {
  useEffect(() => {
    if (!enabled) return
    const handler = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    document.addEventListener('keydown', handler)
    return () => document.removeEventListener('keydown', handler)
  }, [enabled, onClose])
}

export type InputFieldProps = InputHTMLAttributes<HTMLInputElement> & { label: ReactNode; hint?: ReactNode }
export type TextareaFieldProps = TextareaHTMLAttributes<HTMLTextAreaElement> & { label: ReactNode; hint?: ReactNode }
export function InputField({ label, hint, ...props }: InputFieldProps) { return <Field label={label} hint={hint}><input {...props} /></Field> }
export function TextareaField({ label, hint, ...props }: TextareaFieldProps) { return <Field label={label} hint={hint}><textarea {...props} /></Field> }

export const useStableId = (prefix = 'rv-field') => `${prefix}-${useId()}`
