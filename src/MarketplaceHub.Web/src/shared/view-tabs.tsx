import { useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useRememberedTabCounts } from './tab-counts'
import { useDismissable } from './hooks/useDismissable'
import { createPortal } from 'react-dom'
import './view-tabs.css'

// View tabs across the panel (docs/tasarim-onerileri/faturalar-sekmeler-2.html, option 12): unframed tabs with an icon,
// the label and a boxed count, a violet underline on the active one. Tabs that mean the same thing share one icon,
// whichever page they are on ("all", "waiting", "done", "cancelled", "paused"...).
export type ViewTabIconName =
  | 'all' | 'new' | 'processing' | 'shipping' | 'done' | 'cancelled' | 'partial' | 'resend' | 'paused'
  | 'waiting' | 'unread' | 'due' | 'attention' | 'review' | 'disputed' | 'hidden' | 'connected'
  | 'order' | 'product' | 'price' | 'invoice' | 'invoiceMissing' | 'invoiceDone' | 'return' | 'category' | 'brand' | 'attribute' | 'more'

const line = (d: string) => <path d={d} fill="none" stroke="currentColor" strokeWidth={1.4} strokeLinecap="round" strokeLinejoin="round" />
const circle = <circle cx="8" cy="8" r="5.8" fill="none" stroke="currentColor" strokeWidth={1.4} />
const page = <>{line('M4 2h5l3 3v9H4z')}{line('M9 2v3h3')}</>

const icons: Record<ViewTabIconName, ReactNode> = {
  all: <>{line('M2.5 3.5h11v9h-11z')}{line('M2.5 6.5h11')}</>,
  new: <>{circle}{line('M8 5.4v5.2M5.4 8h5.2')}</>,
  processing: <>{line('M2.5 5.2 8 2.5l5.5 2.7v5.6L8 13.5l-5.5-2.7z')}{line('M2.5 5.2 8 8l5.5-2.8M8 8v5.5')}</>,
  shipping: <>{line('M1.8 4h7.7v7H1.8zM9.5 6.5h2.8l2 2.3V11H9.5')}<circle cx="4.5" cy="11.8" r="1.2" fill="none" stroke="currentColor" strokeWidth={1.3} /><circle cx="11.8" cy="11.8" r="1.2" fill="none" stroke="currentColor" strokeWidth={1.3} /></>,
  done: <>{circle}{line('m5.5 8.2 1.7 1.7 3.4-3.6')}</>,
  cancelled: <>{circle}{line('m6 6 4 4M10 6l-4 4')}</>,
  partial: <>{circle}{line('M5.4 8h5.2')}</>,
  resend: <>{line('M13 8a5 5 0 1 1-1.5-3.6')}{line('M13 2.5V5h-2.5')}</>,
  paused: <>{circle}{line('M6.6 5.8v4.4M9.4 5.8v4.4')}</>,
  waiting: <>{circle}{line('M8 5v3.2l2 1.3')}</>,
  unread: <>{line('M2.5 8.5V12a1.5 1.5 0 0 0 1.5 1.5h8a1.5 1.5 0 0 0 1.5-1.5V8.5')}{line('M2.5 8.5h3l1 1.5h3l1-1.5h3L11.5 3h-7z')}</>,
  due: <>{line('M13.6 9.3A5.8 5.8 0 1 1 9.3 2.4')}{line('M8 5v3.2l2 1.3')}{line('M12.6 2.6v2.6M12.6 7.1v.1')}</>,
  attention: <>{line('M8 2 14.5 13.5h-13z')}{line('M8 6.5v3M8 11.6v.1')}</>,
  review: <><circle cx="7" cy="7" r="4.5" fill="none" stroke="currentColor" strokeWidth={1.4} />{line('m10.5 10.5 3 3')}</>,
  disputed: <>{line('M3.5 14V2.5')}{line('M3.5 3h8l-1.6 2.8L11.5 8.6h-8')}</>,
  hidden: <>{line('M2 8s2.2-4 6-4 6 4 6 4-2.2 4-6 4-6-4-6-4z')}{line('m3 13 10-10')}</>,
  connected: <>{line('M6.8 9.2a2.6 2.6 0 0 0 3.7 0l2-2a2.6 2.6 0 0 0-3.7-3.7l-.6.6')}{line('M9.2 6.8a2.6 2.6 0 0 0-3.7 0l-2 2a2.6 2.6 0 0 0 3.7 3.7l.6-.6')}</>,
  order: <>{line('M2.5 3h2l1.6 7.4a1 1 0 0 0 1 .8h5.6a1 1 0 0 0 1-.8L14.5 6H5.2')}<circle cx="7" cy="13.5" r=".9" fill="currentColor" /><circle cx="12.5" cy="13.5" r=".9" fill="currentColor" /></>,
  product: <>{line('M5.5 2.5 2.5 4.5l1.3 2.6 1.2-.5V13.5h6V6.6l1.2.5 1.3-2.6-3-2')}{line('M5.5 2.5a2.5 2.5 0 0 0 5 0')}</>,
  price: <>{line('M2.5 8.6V2.5h6.1l5 5-6.1 6.1z')}<circle cx="5.6" cy="5.6" r="1" fill="currentColor" /></>,
  invoice: <>{page}{line('M6 8.5h4M6 11h3')}</>,
  invoiceMissing: <>{page}{line('M8 8v3M8 12.6v.1')}</>,
  invoiceDone: <>{page}{line('m6.2 10 1.4 1.4 2.4-2.6')}</>,
  return: <>{line('M5.5 4 2.5 7l3 3')}{line('M2.5 7h7a4 4 0 0 1 0 8H7')}</>,
  category: <>{line('M2.5 2.5h4.5v4.5H2.5zM9 2.5h4.5v4.5H9zM2.5 9h4.5v4.5H2.5zM9 9h4.5v4.5H9z')}</>,
  more: <><circle cx="3.8" cy="8" r="1" fill="currentColor" /><circle cx="8" cy="8" r="1" fill="currentColor" /><circle cx="12.2" cy="8" r="1" fill="currentColor" /></>,
  brand: <>{line('M8 2.2 9.7 5.7l3.8.5-2.8 2.7.7 3.8L8 10.9l-3.4 1.8.7-3.8-2.8-2.7 3.8-.5z')}</>,
  // An attribute: three named values on sliders (Özellik tabs).
  attribute: <>{line('M2.5 4h11M2.5 8h11M2.5 12h11')}<circle cx="5.5" cy="4" r="1.3" fill="var(--rv-color-surface)" stroke="currentColor" strokeWidth={1.4} /><circle cx="10.5" cy="8" r="1.3" fill="var(--rv-color-surface)" stroke="currentColor" strokeWidth={1.4} /><circle cx="7" cy="12" r="1.3" fill="var(--rv-color-surface)" stroke="currentColor" strokeWidth={1.4} /></>,
}

export function ViewTabIcon({ name }: { name: ViewTabIconName }) {
  return <svg className="rv-view-tab-icon" viewBox="0 0 16 16" aria-hidden="true">{icons[name]}</svg>
}

// Shorthand for a Tabs item: the label with its icon in front. A text label keeps the width of its selected (heavier)
// weight, so picking a tab does not nudge the tabs after it.
export function viewTabLabel(name: ViewTabIconName, label: ReactNode) {
  return <><ViewTabIcon name={name} />{typeof label === 'string' ? <span className="rv-tab-text" data-text={label}>{label}</span> : label}</>
}

type ViewTabItem = { value: string; label: ReactNode; count?: ReactNode; countTone?: 'warn' }

// Tabs with a "Diğer" menu: the values listed in `more` leave the row and open from a menu at its end, so long tab sets
// (orders and returns have nine) fit on one line. When one of them is selected, the menu button shows it as active.
// Tabs that still do not fit the row move into the menu too, last first (never the first tab or the selected one).
export function ViewTabsWithMore({ items: givenItems, more, value, onChange, ariaLabel, className }: { items: ViewTabItem[]; more: string[]; value: string; onChange: (value: string) => void; ariaLabel: string; className?: string }) {
  const items = useRememberedTabCounts(ariaLabel, givenItems)
  const [open, setOpen] = useState(false)
  const [position, setPosition] = useState<{ top: number; right: number } | null>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const menu = useRef<HTMLDivElement>(null)
  const [overflow, setOverflow] = useState<string[]>([])
  const row = useRef<HTMLDivElement>(null)
  // On phones every tab stays in one strip that scrolls sideways; the Diğer menu is for wider screens.
  const [phone, setPhone] = useState(() => typeof window !== 'undefined' && window.matchMedia('(max-width: 760px)').matches)
  useEffect(() => {
    const list = window.matchMedia('(max-width: 760px)')
    const update = () => setPhone(list.matches)
    list.addEventListener('change', update); window.addEventListener('resize', update)
    return () => { list.removeEventListener('change', update); window.removeEventListener('resize', update) }
  }, [])
  const moved = useMemo(() => phone ? [] : [...overflow, ...more], [phone, overflow, more])
  const main = items.filter(item => !moved.includes(item.value))
  const extra = items.filter(item => moved.includes(item.value))
  useLayoutEffect(() => {
    const element = row.current
    if (phone || !element || element.scrollWidth <= element.clientWidth + 1) return
    const next = items.map(item => item.value).filter((candidate, index) => index > 0 && !moved.includes(candidate) && candidate !== value).pop()
    if (next) setOverflow(current => [next, ...current])
  }, [phone, items, moved, value])
  useEffect(() => {
    const element = row.current
    if (!element || typeof ResizeObserver === 'undefined') return
    let width = element.clientWidth
    const refit = () => { if (element.clientWidth !== width) { width = element.clientWidth; setOverflow([]) } }
    const observer = new ResizeObserver(refit)
    observer.observe(element)
    window.addEventListener('resize', refit)
    return () => { observer.disconnect(); window.removeEventListener('resize', refit) }
  }, [])
  const selectedExtra = extra.find(item => item.value === value)
  useEffect(() => { if (phone) row.current?.querySelector<HTMLElement>('[aria-selected="true"]')?.scrollIntoView({ block: 'nearest', inline: 'center' }) }, [phone, value])
  useEffect(() => {
    if (!open) return
    const place = () => { const rect = trigger.current?.getBoundingClientRect(); if (rect) setPosition({ top: rect.bottom + 6, right: Math.max(12, window.innerWidth - rect.right) }) }
    place()
    window.addEventListener('resize', place); window.addEventListener('scroll', place, true)
    return () => { window.removeEventListener('resize', place); window.removeEventListener('scroll', place, true) }
  }, [open])
  useDismissable(open, [trigger, menu], () => setOpen(false), { triggerRef: trigger })
  const tab = (item: ViewTabItem) => <button key={item.value} type="button" role="tab" tabIndex={item.value === value ? 0 : -1} aria-selected={item.value === value} className={item.value === value ? 'is-active' : ''} onClick={() => onChange(item.value)}><span className="rv-tab-label">{item.label}</span>{item.count !== undefined ? <span className={`rv-tab-count${item.countTone === 'warn' ? ' is-warn' : ''}`}>{item.count}</span> : null}</button>
  return <div ref={row} className={['rv-tabs', 'rv-view-tabs', phone ? 'is-strip' : '', className].filter(Boolean).join(' ')} role="tablist" aria-label={ariaLabel}>
    {main.map(tab)}
    {extra.length > 0 && <button ref={trigger} type="button" role="tab" aria-selected={Boolean(selectedExtra)} aria-haspopup="menu" aria-expanded={open} tabIndex={selectedExtra ? 0 : -1} className={`rv-view-tabs-more${selectedExtra ? ' is-active' : ''}`} onClick={() => setOpen(current => !current)}>
      {selectedExtra ? <><span className="rv-tab-label">{selectedExtra.label}</span>{selectedExtra.count !== undefined && <span className="rv-tab-count">{selectedExtra.count}</span>}</> : <span className="rv-tab-label"><ViewTabIcon name="more" />Diğer</span>}
      <svg className="rv-view-tabs-chevron" viewBox="0 0 16 16" aria-hidden="true">{line('m4.5 6.5 3.5 3.5 3.5-3.5')}</svg>
    </button>}
    {open && position && createPortal(<div ref={menu} className="rv-view-tabs-menu" role="menu" aria-label={`${ariaLabel}: diğer`} style={{ top: position.top, right: position.right }}>
      {extra.map(item => <button key={item.value} type="button" role="menuitemradio" aria-checked={item.value === value} className={item.value === value ? 'is-active' : ''} onClick={() => { onChange(item.value); setOpen(false) }}><span className="rv-tab-label">{item.label}</span>{item.count !== undefined && <span className="rv-tab-count">{item.count}</span>}</button>)}
    </div>, document.body)}
  </div>
}
