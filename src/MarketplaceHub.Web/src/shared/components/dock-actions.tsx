import { createContext, useContext, useEffect, useLayoutEffect, useId, type ReactNode } from 'react'
import { createPortal } from 'react-dom'

// The phone's one bottom bar. The app shell draws the dock (Ana Sayfa, Siparişler, Ara, Ürünler, Menü) and, in the same
// place and shape, a task bar for the page that has actions to offer. A page never draws its own bottom bar on a phone:
// it registers its actions here (DockActions, or StickyActionBar which does it for every page bar) and they are portalled
// into the task bar; while one is registered the dock gives way to the task bar (a cross-fade).
export type DockInfo = { start?: ReactNode; end?: ReactNode }
export type DockHost = {
  // True up to 768 px, where the dock exists.
  phone: boolean
  actionsEl: HTMLElement | null
  infoEl: HTMLElement | null
  // The registration that owns the task bar now (the newest one).
  topId: string | null
  register: (id: string, hasInfo: boolean) => () => void
  // The top row of the dock: the open page's own tabs (DockTabs).
  tabsEl: HTMLElement | null
  tabsTopId: string | null
  registerTabs: (id: string) => () => void
}
export const DockContext = createContext<DockHost | null>(null)
export const useDockHost = () => useContext(DockContext)

// The page's bottom actions: the buttons in one row (the primary one stretches: .rr-primary or data-dock-primary), the
// status in a single-line strip above the bar (`info`: a tappable chip or text on the left, a short summary on the right).
export function DockActions({ label, info, children }: { label: string; info?: DockInfo; children: ReactNode }) {
  const dock = useDockHost()
  const id = useId()
  const hasInfo = Boolean(info?.start || info?.end)
  const register = dock?.register
  useLayoutEffect(() => register?.(id, hasInfo), [register, id, hasInfo])
  if (!dock || dock.topId !== id || !dock.actionsEl) return null
  return <>
    {createPortal(<div className="rv-taskbar-group" role="group" aria-label={label}>{children}</div>, dock.actionsEl)}
    {hasInfo && dock.infoEl ? createPortal(<>{info?.start ? <span className="rv-dock-info-start">{info.start}</span> : null}{info?.end ? <span className="rv-dock-info-end">{info.end}</span> : null}</>, dock.infoEl) : null}
  </>
}

export type DockTabItem = { value: string; label: ReactNode; count?: ReactNode }

// A page's tab strip on a phone: it is not drawn in the page; its tabs are chips in the dock's top row (one source: the
// page keeps its value and its onChange, the dock only draws them). The selected chip scrolls into view.
export function DockTabs({ label, items, value, onChange }: { label: string; items: DockTabItem[]; value: string; onChange: (value: string) => void }) {
  const dock = useDockHost()
  const id = useId()
  const registerTabs = dock?.registerTabs
  useLayoutEffect(() => registerTabs?.(id), [registerTabs, id])
  const tabsEl = dock?.tabsEl ?? null
  const mine = dock?.tabsTopId === id
  useEffect(() => {
    if (!mine || !tabsEl) return
    const selected = tabsEl.querySelector<HTMLElement>('[aria-selected="true"]')
    if (!selected) return
    const left = selected.offsetLeft - (tabsEl.clientWidth - selected.offsetWidth) / 2
    tabsEl.scrollTo?.({ left: Math.max(0, left), behavior: 'smooth' })
  }, [mine, tabsEl, value, items.length])
  if (!dock || !mine || !tabsEl) return null
  return createPortal(<div className="rv-dock-tablist" role="tablist" aria-label={label}>{items.map(item => <button key={item.value} type="button" role="tab" aria-selected={item.value === value} className={`rv-dock-tab${item.value === value ? ' is-active' : ''}`} onClick={() => onChange(item.value)}><span className="rv-dock-tab-label">{item.label}</span>{item.count !== undefined && item.count !== null ? <em>{item.count}</em> : null}</button>)}</div>, tabsEl)
}
