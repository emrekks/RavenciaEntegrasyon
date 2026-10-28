import { Children, cloneElement, isValidElement, useRef, useState, type CSSProperties, type ReactElement, type ReactNode } from 'react'
import { UiIcon } from './components'
import { useDismissable } from './hooks/useDismissable'

/**
 * Page-heading actions. On desktop the children render exactly where they were (the wrapper is
 * `display: contents`). On phones two of them (`primary`, indexes into the children, default the first
 * two) become equal dark buttons side by side. With exactly three actions the third one is shown as an
 * icon-only button; with four or more the rest move behind one "⋮" button. Tapping a button or link in the menu closes it; form controls keep it open.
 */
export function MobileActionMenu({ children, label = 'Diğer işlemler', primary = [0, 1], className }: { children: ReactNode; label?: string; primary?: number[]; className?: string }) {
  const [open, setOpen] = useState(false)
  const root = useRef<HTMLDivElement>(null)
  useDismissable(open, [root], () => setOpen(false))
  // Each item keeps its original index as CSS order, so the desktop order never changes.
  const items = Children.toArray(children).map((child, index) => isValidElement(child) ? cloneElement(child as ReactElement<{ style?: CSSProperties }>, { style: { ...(child.props as { style?: CSSProperties }).style, ['--mam-order' as string]: index, ['--mam-primary-order' as string]: Math.max(0, primary.indexOf(index)) } as CSSProperties }) : child)
  const primaryItems = primary.filter(index => index < items.length).map(index => items[index])
  const rest = items.filter((_, index) => !primary.includes(index))
  const iconItems = items.length === 3 ? rest : []
  const moreItems = items.length > 3 ? rest : []
  const closeAfterClick = (event: React.MouseEvent) => { if ((event.target as Element).closest('button, a')) window.setTimeout(() => setOpen(false), 0) }
  return <div className={`mam${open ? ' is-open' : ''}${moreItems.length ? ' has-more' : ''} mam-primary-${primaryItems.length}${iconItems.length ? ' has-icon' : ''}${className ? ` ${className}` : ''}`} ref={root}>
    <div className="mam-primary">{primaryItems}</div>
    {iconItems.length > 0 && <div className="mam-icons">{iconItems}</div>}
    {moreItems.length > 0 && <>
      <button type="button" className="mam-trigger" aria-expanded={open} aria-label={label} onClick={() => setOpen(value => !value)}><UiIcon name="moreVertical" size={18} /></button>
      {/* eslint-disable-next-line jsx-a11y/click-events-have-key-events, jsx-a11y/no-static-element-interactions -- delegated: closes after one of its buttons or links ran, which keyboards trigger too */}
      <div className="mam-more" onClick={closeAfterClick}>{moreItems}</div>
    </>}
  </div>
}
