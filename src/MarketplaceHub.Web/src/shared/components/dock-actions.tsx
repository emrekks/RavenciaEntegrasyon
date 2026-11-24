import { createContext, useContext, useEffect, useLayoutEffect, useId, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { UiIcon } from './UiIcon'

// The phone's one bottom bar. The app shell draws the dock (Ana Sayfa, Siparişler, Ürünler, Sorular, Menü) and, in the same
// place and shape, a task bar for the page that has actions to offer. A page never draws its own bottom bar on a phone:
// it registers its actions here (DockActions, or StickyActionBar which does it for every page bar) and they are portalled
// into the task bar; while one is registered the dock gives way to the task bar (a cross-fade).
//
// The task bar is one row of labelled controls: [Menü] [primary, stretching] [step "İleri"] [Diğer]. The page's buttons are
// sorted by markers the page puts on them, never by page CSS:
//   .rr-primary / .is-primary / data-dock-primary   the primary action (full label, stretches)
//   data-dock-step                                  the step to the next screen ("İleri" and its arrow)
//   data-dock-clear + data-dock-count="3"           the selection chip ("3 Seçili" and its cross)
//   every other button                              goes into the Diğer list (icon and its full name); when it is the
//                                                   only other one and its label is short (<= 8 characters) it stays
//                                                   in the bar as a labelled button. data-dock-label gives a name where the
//                                                   button has no aria-label.
// `info.start` is a warning (a chip in the strip above the bar, only while there is one); `info.end` is the neutral
// summary, the grey first line of the Diğer list.
export type DockInfo = { start?: ReactNode; end?: ReactNode }
export type DockHost = {
  // True up to 768 px, where the dock exists.
  phone: boolean
  actionsEl: HTMLElement | null
  infoEl: HTMLElement | null
  // The registration that owns the task bar now (the newest one).
  topId: string | null
  register: (id: string, hasWarning: boolean) => () => void
}
export const DockContext = createContext<DockHost | null>(null)
export const useDockHost = () => useContext(DockContext)

type Other = { element: HTMLElement; label: string; iconHtml: string; danger: boolean; disabled: boolean }
const shortLabel = 8

const isPrimary = (element: Element) => element.matches('[data-dock-primary], .rr-primary, .is-primary')
const labelOf = (element: HTMLElement) => (element.getAttribute('data-dock-label') || element.getAttribute('aria-label') || element.querySelector('.submit-label-long')?.textContent || element.textContent || element.getAttribute('title') || '').replace(/\s+/g, ' ').trim()
const barLabelOf = (element: HTMLElement) => (element.querySelector('.submit-label-short')?.textContent || labelOf(element)).trim()

// Reads the page's buttons in the group and tags the ones that leave the bar (data-dock-other) or stay as the lone short one
// (data-dock-keep). The tags are plain attributes on the page's own elements; the page keeps its handlers and states.
// `squeezed`: the row does not fit, so even a lone short button goes into the Diğer list.
function sortActions(group: HTMLElement, squeezed = false): Other[] {
  const candidates = [...group.querySelectorAll<HTMLElement>('button, a[href]')].filter(element => {
    // The menu a primary button opens (an anchored popover) is not one of the actions.
    const anchor = element.closest('.rr-anchor')
    return !(anchor && element.parentElement !== anchor)
  })
  for (const element of candidates) { element.removeAttribute('data-dock-other'); element.removeAttribute('data-dock-keep') }
  const others = candidates.filter(element => !isPrimary(element) && !element.matches('[data-dock-step], [data-dock-clear]'))
  if (!squeezed && others.length === 1 && barLabelOf(others[0]).length <= shortLabel) { others[0].setAttribute('data-dock-keep', ''); return [] }
  for (const element of others) element.setAttribute('data-dock-other', '')
  return others.map(element => ({
    element,
    label: labelOf(element),
    iconHtml: element.querySelector('svg')?.outerHTML ?? '',
    danger: element.matches('.rr-danger, .is-danger, .rv-button-danger'),
    disabled: (element as HTMLButtonElement).disabled === true || element.getAttribute('aria-disabled') === 'true',
  }))
}

export function DockActions({ label, info, children }: { label: string; info?: DockInfo; children: ReactNode }) {
  const dock = useDockHost()
  const id = useId()
  const hasWarning = Boolean(info?.start)
  const register = dock?.register
  useLayoutEffect(() => register?.(id, hasWarning), [register, id, hasWarning])
  const group = useRef<HTMLDivElement>(null)
  const [others, setOthers] = useState<Other[]>([])
  const signature = useRef('')
  const [open, setOpen] = useState(false)
  // A turned screen changes what fits: the row is fitted again.
  const [, refit] = useState(0)
  useEffect(() => { const onResize = () => refit(value => value + 1); window.addEventListener('resize', onResize); return () => window.removeEventListener('resize', onResize) }, [])
  // After every render of the page's buttons: sort them again (a button may appear, or switch to disabled).
  // eslint-disable-next-line react-hooks/exhaustive-deps -- runs after every render on purpose; it sets state only when the sorted buttons changed
  useLayoutEffect(() => {
    if (!group.current) return
    const element = group.current
    // Fit the row: the labelled step first gives way to its arrow, then a lone short button moves into the Diğer list; the
    // primary label is never cut.
    // (the row is end-aligned, and an overflow to the left is not counted in scrollWidth: it is measured start-aligned)
    const overflows = () => { element.style.justifyContent = 'flex-start'; const over = element.scrollWidth > element.clientWidth + 1; element.style.justifyContent = ''; return over }
    element.removeAttribute('data-dock-compact')
    let next = sortActions(element)
    if (overflows()) {
      element.setAttribute('data-dock-compact', '')
      if (overflows() && element.querySelector('[data-dock-keep]')) next = sortActions(element, true)
    }
    const nextSignature = next.map(item => `${item.label}|${item.disabled}|${item.danger}`).join(';')
    if (nextSignature !== signature.current) { signature.current = nextSignature; setOthers(next) }
  })
  const mine = Boolean(dock) && dock?.topId === id
  const hasMore = others.length > 0
  // The Diğer list closes on Esc and on the phone's back button (a history entry while it is open); a press outside only closes it.
  useEffect(() => {
    if (!open || !mine) return undefined
    window.history.pushState({ ...(window.history.state ?? {}), rvMore: true }, '')
    const close = () => setOpen(false)
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') { event.preventDefault(); close() } }
    window.addEventListener('popstate', close)
    document.addEventListener('keydown', onKey)
    return () => {
      window.removeEventListener('popstate', close)
      document.removeEventListener('keydown', onKey)
      if (window.history.state?.rvMore) window.history.back()
    }
  }, [open, mine])
  useEffect(() => { if (!hasMore) setOpen(false) }, [hasMore])
  if (!dock || !mine || !dock.actionsEl) return null
  return <>
    {createPortal(<>
      <div ref={group} className="rv-taskbar-group" role="group" aria-label={label}>{children}</div>
      {hasMore ? <div className="rv-taskbar-more">
        <button type="button" className="rv-taskbar-more-button" aria-label="Diğer" aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen(value => !value)}><UiIcon name="moreVertical" size={20} /></button>
        {open ? <>
          <div className="rv-taskbar-more-scrim" aria-hidden="true" onClick={() => setOpen(false)} />
          <div className="rv-taskbar-more-list" role="menu" aria-label="Diğer işlemler">
            {info?.end ? <div className="rv-taskbar-more-summary">{info.end}</div> : null}
            {others.map((item, index) => <button key={index} type="button" role="menuitem" className={'rv-taskbar-more-item' + (item.danger ? ' is-danger' : '')} disabled={item.disabled} onClick={() => { setOpen(false); item.element.click() }}>
              <span className="rv-taskbar-more-icon" aria-hidden="true" dangerouslySetInnerHTML={{ __html: item.iconHtml }} /><span>{item.label}</span>
            </button>)}
          </div>
        </> : null}
      </div> : null}
    </>, dock.actionsEl)}
    {hasWarning && dock.infoEl ? createPortal(<span className="rv-dock-info-start">{info?.start}</span>, dock.infoEl) : null}
  </>
}
