import { useEffect, useRef, useState, type ComponentPropsWithoutRef, type ElementType, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { UiIcon } from './components'
import { useScrollLock } from './hooks/useScrollLock'

type MobileFilterSort = {
  title?: string
  value: string
  options: Array<{ value: string; label: string }>
  onChange: (value: string) => void
}

type MobileFilterShellProps = Omit<ComponentPropsWithoutRef<'div'>, 'title'> & {
  as?: 'div' | 'section'
  children: ReactNode
  activeCount?: number
  title?: string
  onClear?: () => void
  onApply?: () => void
  sort?: MobileFilterSort
}

/**
 * Wraps an existing filter bar. On desktop it renders exactly the bar it was given. On phones the
 * bar collapses to the field marked `data-mfs-search`, an optional sort button and a filter button;
 * the filter button opens every field as a full-screen "Filtrele" sheet with Temizle / Filtrele
 * actions, and the sort button opens a bottom action sheet.
 */
export function MobileFilterShell({ as = 'div', className, children, activeCount = 0, title = 'Filtrele', onClear, onApply, sort, ...rest }: MobileFilterShellProps) {
  const [open, setOpen] = useState(false)
  const [sortOpen, setSortOpen] = useState(false)
  // The open sheet leaves the page flow; a spacer as tall as the closed bar keeps its place so the list behind it does not move.
  const [spacer, setSpacer] = useState(0)
  const bar = useRef<HTMLElement>(null)
  const openSheet = () => { const element = bar.current; if (element) { const style = getComputedStyle(element); setSpacer(element.getBoundingClientRect().height + parseFloat(style.marginTop) + parseFloat(style.marginBottom)) } setOpen(true) }
  const Tag = as as ElementType
  // The sheets open on phones only; while one is open the page behind it holds still.
  useScrollLock(open || sortOpen)
  useEffect(() => {
    if (!open && !sortOpen) return
    const closeOnEscape = (event: KeyboardEvent) => { if (event.key === 'Escape') { setOpen(false); setSortOpen(false) } }
    document.addEventListener('keydown', closeOnEscape)
    return () => document.removeEventListener('keydown', closeOnEscape)
  }, [open, sortOpen])
  return <>{open && spacer > 0 && <div className="mfs-spacer" aria-hidden="true" style={{ height: spacer }} />}<Tag ref={bar} className={`mfs${open ? ' is-open' : ''}${className ? ` ${className}` : ''}`} {...rest} {...(open ? { role: 'dialog', 'aria-modal': true, 'aria-label': title } : {})}>
    <div className="mfs-header"><button type="button" className="mfs-back" aria-label="Filtreleri kapat" onClick={() => setOpen(false)}><UiIcon name="arrowLeft" size={20} /></button><strong>{title}</strong></div>
    {children}
    {sort && <button type="button" className="mfs-sort" aria-haspopup="dialog" aria-expanded={sortOpen} aria-label="Sırala" onClick={() => setSortOpen(true)}><svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M8 4v16m0 0-4-4m4 4 4-4M16 20V4m0 0-4 4m4-4 4 4" /></svg></button>}
    <button type="button" className="mfs-open" aria-haspopup="dialog" aria-expanded={open} aria-label={activeCount > 0 ? `Filtreler, ${activeCount} etkin` : 'Filtreler'} onClick={openSheet}><UiIcon name="filter" size={18} />{activeCount > 0 && <b>{activeCount}</b>}</button>
    <div className="mfs-footer"><button type="button" className="secondary" onClick={() => onClear?.()}>Temizle</button><button type="button" onClick={() => { onApply?.(); setOpen(false) }}>Filtrele</button></div>
    {sort && sortOpen && createPortal(<div className="mfs-sort-backdrop" role="presentation" onMouseDown={() => setSortOpen(false)}>
      <section className="mfs-sort-sheet" role="dialog" aria-modal="true" aria-label={sort.title ?? 'Sıralama'} onMouseDown={event => event.stopPropagation()}>
        <header><button type="button" onClick={() => setSortOpen(false)}>Kapat</button><strong>{sort.title ?? 'Sıralama'}</strong><span aria-hidden="true" /></header>
        <div role="listbox" aria-label={sort.title ?? 'Sıralama'}>{sort.options.map(option => <button type="button" role="option" aria-selected={option.value === sort.value} className={option.value === sort.value ? 'is-selected' : ''} key={option.value} onClick={() => { sort.onChange(option.value); setSortOpen(false) }}>{option.label}</button>)}</div>
      </section>
    </div>, document.body)}
  </Tag></>
}
