import { createElement, useEffect, useId, useRef, useState, type ButtonHTMLAttributes, type ComponentPropsWithoutRef, type ReactNode, type RefObject } from 'react'
import { useRememberedTabCounts } from '../tab-counts'
import { UiIcon } from './UiIcon'
import { lockPageScroll } from '../hooks/useScrollLock'
import { TitleHint } from './TitleHint'

type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger'
type ControlSize = 'sm' | 'md' | 'lg'
export type StatusTone = 'neutral' | 'info' | 'success' | 'warning' | 'danger'
type DialogSurfaceProps = { open: boolean; title: ReactNode; onClose: () => void; children: ReactNode; footer?: ReactNode; description?: ReactNode; className?: string; /** What the window is for, behind a "?" beside its title; live facts stay in `description`. */ hint?: ReactNode }

const focusableSelector = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), summary, [tabindex]:not([tabindex="-1"])'

// Windows open on top of each other (a confirmation over an edit window): only the top one answers Escape and Tab.
const openSurfaces: object[] = []

// Visible for focus purposes; a fixed-position control has no offsetParent but does have a box.
const isShown = (element: HTMLElement) => element.offsetParent !== null || element.getClientRects().length > 0

/**
 * Makes an element a modal window: focus moves in (to the element marked `data-autofocus`, else the first control) and
 * stays inside while Tab cycles, Escape closes, the page behind does not scroll, and focus returns to where it was.
 * Attach the returned ref to the window element (give it tabIndex={-1}).
 */
export function useDialogSurface<T extends HTMLElement = HTMLElement>(open: boolean, onClose: () => void): RefObject<T | null> {
  const surfaceRef = useRef<T>(null)
  const previousFocusRef = useRef<HTMLElement | null>(null)
  const onCloseRef = useRef(onClose)
  useEffect(() => { onCloseRef.current = onClose }, [onClose])

  useEffect(() => {
    if (!open) return
    const entry = {}
    openSurfaces.push(entry)
    previousFocusRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null
    const onKeyDown = (event: KeyboardEvent) => {
      if (openSurfaces[openSurfaces.length - 1] !== entry || event.defaultPrevented) return
      if (event.key === 'Escape') {
        event.preventDefault()
        onCloseRef.current()
        return
      }
      if (event.key !== 'Tab') return
      const focusable = Array.from(surfaceRef.current?.querySelectorAll<HTMLElement>(focusableSelector) ?? []).filter(isShown)
      if (!focusable.length) {
        event.preventDefault()
        surfaceRef.current?.focus()
        return
      }
      const first = focusable[0]
      const last = focusable[focusable.length - 1]
      // The surface itself takes focus when the window opens: Shift+Tab from there would leave the window, so it wraps to
      // the last control like Shift+Tab from the first; focus that has got outside the window is brought back too.
      const active = document.activeElement
      const outside = !surfaceRef.current?.contains(active)
      if (event.shiftKey && (active === first || active === surfaceRef.current || outside)) {
        event.preventDefault()
        last.focus()
      } else if (!event.shiftKey && outside) {
        event.preventDefault()
        first.focus()
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault()
        first.focus()
      }
    }
    // body and the app's own scroller (main) hold still behind the window
    const unlockScroll = lockPageScroll(true)
    document.addEventListener('keydown', onKeyDown)
    window.requestAnimationFrame(() => {
      const surface = surfaceRef.current
      if (!surface || surface.contains(document.activeElement)) return
      const first = surface.querySelector<HTMLElement>('[data-autofocus]') ?? surface.querySelector<HTMLElement>(focusableSelector)
      ;(first ?? surface).focus()
    })
    return () => {
      const index = openSurfaces.indexOf(entry)
      if (index >= 0) openSurfaces.splice(index, 1)
      unlockScroll()
      document.removeEventListener('keydown', onKeyDown)
      window.requestAnimationFrame(() => previousFocusRef.current?.focus())
    }
  }, [open])

  return surfaceRef
}

type DialogSurfaceElement = 'section' | 'div' | 'aside' | 'form'

/** A hand-built window element (its own backdrop and layout) with the behaviour of `useDialogSurface`. */
export function DialogSurface<T extends DialogSurfaceElement = 'section'>({ as, onClose, children, ...props }: Omit<ComponentPropsWithoutRef<T>, 'children'> & { as?: T; onClose: () => void; children?: ReactNode }) {
  const ref = useDialogSurface<HTMLElement>(true, onClose)
  return createElement(as ?? 'section', { role: 'dialog', 'aria-modal': true, tabIndex: -1, ...props, ref } as Record<string, unknown>, children)
}

export function Button({ variant = 'primary', size = 'md', loading = false, className, disabled, type = 'button', children, ...props }: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: ButtonVariant; size?: ControlSize; loading?: boolean }) {
  // While busy the label keeps its place (hidden) under a centred spinner, so the button never changes width.
  return <button {...props} type={type} aria-busy={loading || undefined} className={['rv-button', `rv-button-${variant}`, `rv-button-${size}`, loading ? 'is-loading' : '', className].filter(Boolean).join(' ')} disabled={disabled || loading}>{loading ? <><span className="rv-button-hold">{children}</span><span className="rv-button-busy" aria-hidden="true"><UiIcon name="sync" className="rv-button-loading-icon" /></span><span className="sr-only">İşleniyor…</span></> : children}</button>
}

function IconButton({ label, size = 'md', variant = 'ghost', type = 'button', children, className, ...props }: ButtonHTMLAttributes<HTMLButtonElement> & { label: string; size?: ControlSize; variant?: ButtonVariant }) {
  return <button {...props} type={type} aria-label={label} title={props.title ?? label} className={['rv-icon-button', `rv-icon-button-${size}`, `rv-icon-button-${variant}`, className].filter(Boolean).join(' ')}>{children}</button>
}

export function Tabs({ items: givenItems, value, onChange, ariaLabel = 'Sekmeler', className }: { items: Array<{ value: string; label: ReactNode; count?: ReactNode }>; value: string; onChange: (value: string) => void; ariaLabel?: string; className?: string }) {
  const items = useRememberedTabCounts(ariaLabel, givenItems)
  return <div className={['rv-tabs', className].filter(Boolean).join(' ')} role="tablist" aria-label={ariaLabel}>{items.map((item, index) => <button key={item.value} type="button" role="tab" tabIndex={item.value === value ? 0 : -1} aria-selected={item.value === value} className={item.value === value ? 'is-active' : ''} onClick={() => onChange(item.value)} onKeyDown={event => {
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? items.length - 1 : event.key === 'ArrowRight' ? (index + 1) % items.length : event.key === 'ArrowLeft' ? (index - 1 + items.length) % items.length : -1
    if (next < 0) return
    event.preventDefault()
    onChange(items[next].value)
    event.currentTarget.parentElement?.querySelectorAll<HTMLButtonElement>('[role="tab"]')[next]?.focus()
  }}><span className="rv-tab-label">{item.label}</span>{item.count !== undefined ? <span className="rv-tab-count">{item.count}</span> : null}</button>)}</div>
}

type BadgeProps = { tone?: StatusTone; children: ReactNode; className?: string; title?: string; role?: 'status'; 'aria-label'?: string; icon?: ReactNode }
export function Badge({ tone = 'neutral', children, className, icon, ...props }: BadgeProps) { return <span {...props} className={['rv-badge', `rv-badge-${tone}`, icon ? 'has-icon' : '', className].filter(Boolean).join(' ')}>{icon ?? <i aria-hidden="true" />}{children}</span> }
export function StatusBadge({ tone, children, className, title, role, 'aria-label': ariaLabel, icon }: BadgeProps) { return <Badge tone={tone} className={className} title={title} role={role} aria-label={ariaLabel} icon={icon}>{children}</Badge> }

export function Pagination({ page, totalPages, hasNext, onPageChange, onPrevious, onNext, disabled = false, className, ariaLabel = 'Sayfalama' }: { page: number; totalPages: number; hasNext?: boolean; onPageChange: (page: number) => void; onPrevious: () => void; onNext: () => void; disabled?: boolean; className?: string; ariaLabel?: string }) {
  const safeTotalPages = Math.max(1, totalPages)
  const safePage = Math.min(Math.max(1, page), safeTotalPages)
  const [draftPage, setDraftPage] = useState(String(safePage))
  useEffect(() => setDraftPage(String(safePage)), [safePage])
  const readDraftPage = () => {
    const parsed = Number.parseInt(draftPage, 10)
    return Math.min(Math.max(1, Number.isFinite(parsed) ? parsed : safePage), safeTotalPages)
  }
  const commitDraftPage = () => {
    const nextPage = readDraftPage()
    setDraftPage(String(nextPage))
    if (nextPage !== safePage) onPageChange(nextPage)
  }
  const goPrevious = () => {
    const draft = readDraftPage()
    if (draft !== safePage) { commitDraftPage(); return }
    onPrevious()
  }
  const goNext = () => {
    const draft = readDraftPage()
    if (draft !== safePage) { commitDraftPage(); return }
    onNext()
  }
  return <nav className={['rv-pagination', className].filter(Boolean).join(' ')} aria-label={ariaLabel}><Button variant="secondary" size="sm" onClick={goPrevious} disabled={disabled || safePage <= 1}>Önceki</Button><label className="rv-pagination-jump"><span>Sayfa</span><input type="number" inputMode="numeric" min={1} max={safeTotalPages} value={draftPage} onChange={event => setDraftPage(event.target.value)} onBlur={commitDraftPage} onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); commitDraftPage(); event.currentTarget.blur() } }} aria-label="Sayfa numarası" disabled={disabled} /><span aria-hidden="true"> / {safeTotalPages}</span></label><Button variant="secondary" size="sm" onClick={goNext} disabled={disabled || !(hasNext ?? safePage < safeTotalPages)}>Sonraki</Button></nav>
}

export function Modal({ open, title, onClose, children, footer, description, className, hint }: DialogSurfaceProps) {
  const dialogRef = useDialogSurface(open, onClose)
  const titleId = useId()
  const descriptionId = useId()
  if (!open) return null
  return <div className="rv-overlay" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) onClose() }}><section ref={dialogRef} className={['rv-modal', className].filter(Boolean).join(' ')} role="dialog" aria-modal="true" aria-labelledby={titleId} aria-describedby={description ? descriptionId : undefined} tabIndex={-1}><header><div><h2 id={titleId}>{title}{hint ? <TitleHint label={typeof title === 'string' ? title : 'Pencere'}>{hint}</TitleHint> : null}</h2>{description ? <p id={descriptionId}>{description}</p> : null}</div><IconButton label="Kapat" variant="ghost" onClick={onClose}><UiIcon name="close" /></IconButton></header><div className="rv-modal-body">{children}</div>{footer ? <footer>{footer}</footer> : null}</section></div>
}

export function Drawer({ open, title, onClose, children, footer, description, className, hint }: DialogSurfaceProps) {
  const dialogRef = useDialogSurface(open, onClose)
  const titleId = useId()
  const descriptionId = useId()
  // A drawer that closes slides back out before it unmounts (rv-closing, 220 ms), as it slid in.
  const [shown, setShown] = useState(open)
  const [closing, setClosing] = useState(false)
  if (open && (!shown || closing)) { setShown(true); setClosing(false) }
  if (!open && shown && !closing) setClosing(true)
  useEffect(() => {
    if (!closing) return
    const timer = window.setTimeout(() => { setShown(false); setClosing(false) }, 220)
    return () => window.clearTimeout(timer)
  }, [closing])
  return shown ? <div className={`rv-overlay rv-drawer-overlay${closing ? ' rv-closing' : ''}`} role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) onClose() }}><aside ref={dialogRef} className={['rv-drawer', className].filter(Boolean).join(' ')} role="dialog" aria-modal="true" aria-labelledby={titleId} aria-describedby={description ? descriptionId : undefined} tabIndex={-1}><header><div><h2 id={titleId}>{title}{hint ? <TitleHint label={typeof title === 'string' ? title : 'Pencere'}>{hint}</TitleHint> : null}</h2>{description ? <p id={descriptionId}>{description}</p> : null}</div><IconButton label="Kapat" variant="ghost" onClick={onClose}><UiIcon name="close" /></IconButton></header><div className="rv-drawer-body">{children}</div>{footer ? <footer>{footer}</footer> : null}</aside></div> : null
}

export function EmptyState({ children }: { children: ReactNode }) { return <div className="rv-empty-state"><span className="rv-empty-state-icon" aria-hidden="true"><UiIcon name="box" /></span><strong>Henüz kayıt yok</strong><p>{children}</p></div> }
export function LoadingState({ children = 'Yükleniyor…' }: { children?: ReactNode }) { return <div className="rv-loading-state" role="status" aria-live="polite"><strong>{children}</strong><span className="rv-splash-loading-bar" aria-hidden="true"><span className="rv-splash-loading-progress" /></span></div> }
