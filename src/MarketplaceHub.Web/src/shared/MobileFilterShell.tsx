import { useEffect, useState, type ComponentPropsWithoutRef, type ElementType, type ReactNode } from 'react'
import { UiIcon } from './components'

type MobileFilterShellProps = Omit<ComponentPropsWithoutRef<'div'>, 'title'> & {
  as?: 'div' | 'section'
  children: ReactNode
  activeCount?: number
  title?: string
  onClear?: () => void
  onApply?: () => void
}

/**
 * Wraps an existing filter bar. On desktop it renders exactly the bar it was given. On phones the
 * bar collapses to the field marked `data-mfs-search` plus a filter button, and the button opens
 * every field as a full-screen "Filtrele" sheet with Temizle / Filtrele actions.
 */
export function MobileFilterShell({ as = 'div', className, children, activeCount = 0, title = 'Filtrele', onClear, onApply, ...rest }: MobileFilterShellProps) {
  const [open, setOpen] = useState(false)
  const Tag = as as ElementType
  useEffect(() => {
    if (!open) return
    const closeOnEscape = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(false) }
    document.addEventListener('keydown', closeOnEscape)
    document.body.classList.add('mobile-filters-open')
    return () => {
      document.removeEventListener('keydown', closeOnEscape)
      document.body.classList.remove('mobile-filters-open')
    }
  }, [open])
  return <Tag className={`mfs${open ? ' is-open' : ''}${className ? ` ${className}` : ''}`} {...rest} {...(open ? { role: 'dialog', 'aria-modal': true, 'aria-label': title } : {})}>
    <div className="mfs-header"><button type="button" className="mfs-back" aria-label="Filtreleri kapat" onClick={() => setOpen(false)}><UiIcon name="arrowLeft" size={20} /></button><strong>{title}</strong></div>
    {children}
    <button type="button" className="mfs-open" aria-haspopup="dialog" aria-expanded={open} aria-label={activeCount > 0 ? `Filtreler, ${activeCount} etkin` : 'Filtreler'} onClick={() => setOpen(true)}><UiIcon name="filter" size={18} />{activeCount > 0 && <b>{activeCount}</b>}</button>
    <div className="mfs-footer"><button type="button" className="secondary" onClick={() => onClear?.()}>Temizle</button><button type="button" onClick={() => { onApply?.(); setOpen(false) }}>Filtrele</button></div>
  </Tag>
}
