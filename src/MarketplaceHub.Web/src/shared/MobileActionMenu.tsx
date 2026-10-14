import { useEffect, useRef, useState, type ReactNode } from 'react'
import { UiIcon } from './components'

/**
 * Page-heading actions. On desktop the children render exactly where they were (the wrapper is
 * `display: contents`). On phones they collapse behind one "⋮" button and open as a small menu;
 * tapping a button or link inside closes it, form controls (select, file input) keep it open.
 */
export function MobileActionMenu({ children, label = 'Sayfa işlemleri' }: { children: ReactNode; label?: string }) {
  const [open, setOpen] = useState(false)
  const root = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!open) return
    const close = (event: PointerEvent) => { if (!root.current?.contains(event.target as Node)) setOpen(false) }
    const escape = (event: globalThis.KeyboardEvent) => { if (event.key === 'Escape') setOpen(false) }
    document.addEventListener('pointerdown', close)
    document.addEventListener('keydown', escape)
    return () => { document.removeEventListener('pointerdown', close); document.removeEventListener('keydown', escape) }
  }, [open])
  return <div className={`mam${open ? ' is-open' : ''}`} ref={root}>
    <button type="button" className="mam-trigger" aria-haspopup="menu" aria-expanded={open} aria-label={label} onClick={() => setOpen(value => !value)}><UiIcon name="moreVertical" size={18} /></button>
    <div className="mam-actions" role="menu" onClick={event => { if ((event.target as Element).closest('button, a')) window.setTimeout(() => setOpen(false), 0) }}>{children}</div>
  </div>
}
