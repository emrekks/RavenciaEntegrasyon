import { Children, cloneElement, isValidElement, useEffect, useRef, useState, type CSSProperties, type ReactElement, type ReactNode } from 'react'
import { UiIcon } from './components'

/**
 * Page-heading actions. On desktop the children render exactly where they were (the wrapper is
 * `display: contents`). On phones two of them (`primary`, indexes into the children, default the first
 * two) become equal dark buttons side by side and the rest move behind one "⋮" button (hidden when
 * nothing is left over). Tapping a button or link in the menu closes it; form controls keep it open.
 */
export function MobileActionMenu({ children, label = 'Diğer işlemler', primary = [0, 1], className }: { children: ReactNode; label?: string; primary?: number[]; className?: string }) {
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
  // Each item keeps its original index as CSS order, so the desktop order never changes.
  const items = Children.toArray(children).map((child, index) => isValidElement(child) ? cloneElement(child as ReactElement<{ style?: CSSProperties }>, { style: { ...(child.props as { style?: CSSProperties }).style, ['--mam-order' as string]: index, ['--mam-primary-order' as string]: Math.max(0, primary.indexOf(index)) } as CSSProperties }) : child)
  const primaryItems = primary.filter(index => index < items.length).map(index => items[index])
  const moreItems = items.filter((_, index) => !primary.includes(index))
  const closeAfterClick = (event: React.MouseEvent) => { if ((event.target as Element).closest('button, a')) window.setTimeout(() => setOpen(false), 0) }
  return <div className={`mam${open ? ' is-open' : ''}${moreItems.length ? ' has-more' : ''} mam-primary-${primaryItems.length}${className ? ` ${className}` : ''}`} ref={root}>
    <div className="mam-primary">{primaryItems}</div>
    {moreItems.length > 0 && <>
      <button type="button" className="mam-trigger" aria-haspopup="menu" aria-expanded={open} aria-label={label} onClick={() => setOpen(value => !value)}><UiIcon name="moreVertical" size={18} /></button>
      <div className="mam-more" role="menu" onClick={closeAfterClick}>{moreItems}</div>
    </>}
  </div>
}
