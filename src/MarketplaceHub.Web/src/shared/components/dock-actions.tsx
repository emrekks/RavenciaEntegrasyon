import { createContext, useContext, useEffect, useLayoutEffect, useId, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { UiIcon } from './UiIcon'

// The phone's one bottom bar. The app shell draws the dock (Ana Sayfa, Siparişler, Ürünler, Sorular, Menü) and, in the same
// place and shape, a task bar for the page that has actions to offer. A page never draws its own bottom bar on a phone:
// it registers its actions here (DockActions, or StickyActionBar which does it for every page bar) and they are portalled
// into the task bar; while one is registered the dock gives way to the task bar (a cross-fade).
//
// The task bar is one row of labelled buttons, side by side: the page's actions as they are (the primary one, the step to the next
// screen by its name, the others by theirs, a selection chip). Nothing is an unlabelled icon except the settings half of a split
// button. When they do not fit the row is fitted by measuring: first the inner space 14 -> 10 px, then the text 13 -> 12.5 px, then the
// lowest-priority actions (the last "other" ones first) move into the Diğer list, and only then does the ⋯ button appear. A label is never cut.
// The page marks what is what; it never writes CSS for the bar:
//   .rr-primary / .is-primary / data-dock-primary   the primary action
//   data-dock-step="Yayınlama"                      the step to another screen, drawn as its name (data-dock-step-short: a shorter name)
//   data-dock-clear + data-dock-count="3"           the selection chip ("3 Seçili" and its cross)
//   every other button                              an action; a split button (a role="group" of buttons) moves as one
// Priority when space runs out: primary > step > the page's other actions, left to right. data-dock-label names a button that has
// no aria-label. `info.start` is a warning (a chip in the strip above the bar, only while there is one).
export type DockInfo = { start?: ReactNode; end?: ReactNode }  // end: a summary for wider screens; a phone shows none
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
// A unit that can leave the bar: one button, or a split button (a role="group" of buttons) that goes as a whole.
type Unit = { node: HTMLElement; buttons: HTMLElement[] }
const moreWidth = 48

const isPrimary = (element: Element) => element.matches('[data-dock-primary], .rr-primary, .is-primary')
const labelOf = (element: HTMLElement) => (element.getAttribute('data-dock-label') || element.getAttribute('aria-label') || element.querySelector('.submit-label-long')?.textContent || element.textContent || element.getAttribute('title') || '').replace(/\s+/g, ' ').trim()

function actionButtons(group: HTMLElement) {
  return [...group.querySelectorAll<HTMLElement>('button, a[href]')].filter(element => {
    // The menu a primary button opens (an anchored popover) is not one of the actions.
    const anchor = element.closest('.rr-anchor')
    return !(anchor && element.parentElement !== anchor)
  })
}

function otherUnits(group: HTMLElement): Unit[] {
  const units: Unit[] = []
  for (const button of actionButtons(group)) {
    button.removeAttribute('data-dock-other')
    if (isPrimary(button) || button.matches('[data-dock-step], [data-dock-clear]')) continue
    const split = button.closest<HTMLElement>('[role="group"]')
    const node = split && split !== group && group.contains(split) ? split : button
    const unit = units.find(item => item.node === node)
    if (unit) unit.buttons.push(button); else units.push({ node, buttons: [button] })
  }
  return units
}

const toOther = (element: HTMLElement): Other => ({
  element,
  label: labelOf(element),
  iconHtml: element.querySelector('svg')?.outerHTML ?? '',
  danger: element.matches('.rr-danger, .is-danger, .rv-button-danger'),
  disabled: (element as HTMLButtonElement).disabled === true || element.getAttribute('aria-disabled') === 'true',
})

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
    // Measured start-aligned: the row is end-aligned, and an overflow to the left is not counted in scrollWidth.
    const moreShown = others.length > 0
    const overflows = (reserve: number) => { element.style.justifyContent = 'flex-start'; const over = element.scrollWidth > element.clientWidth - reserve + 1; element.style.justifyContent = ''; return over }
    element.removeAttribute('data-dock-fit')
    for (const tagged of element.querySelectorAll('[data-dock-other]')) tagged.removeAttribute('data-dock-other')
    const units = otherUnits(element)
    const moved: Unit[] = []
    const over = () => overflows(moved.length > 0 && !moreShown ? moreWidth : 0)
    if (over()) {
      element.setAttribute('data-dock-fit', '1')
      if (over()) {
        element.setAttribute('data-dock-fit', '2')
        while (over() && moved.length < units.length) {
          const unit = units[units.length - 1 - moved.length]
          unit.node.setAttribute('data-dock-other', '')
          for (const button of unit.buttons) button.setAttribute('data-dock-other', '')
          moved.push(unit)
        }
      }
    }
    const next = moved.reverse().flatMap(unit => unit.buttons.map(toOther))
    const nextSignature = next.map(item => item.label + '|' + item.disabled + '|' + item.danger).join(';')
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
