import { createContext, useContext, useLayoutEffect, useId, type ReactNode } from 'react'
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
