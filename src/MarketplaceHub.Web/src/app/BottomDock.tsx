import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type MouseEvent, type ReactNode, type RefObject } from 'react'
import { Link, useLocation, useNavigate } from 'react-router'
import { UiIcon, type UiIconName } from '../shared/components'
import { DockContext, type DockHost } from '../shared/components/dock-actions'
import { useScrollLock } from '../shared/hooks/useScrollLock'

// The phone's bottom navigation (owner, 24 Nov 2026; bottom-nav.css): one panel, the "Ravencia Dock" (Ana Sayfa,
// Siparişler, Ara, Ürünler, Menü) and, in its place and shape, the task bar of a page that has actions to offer
// (shared/components/dock-actions.tsx). The Menü slot opens a full-screen menu that is an entry of the browser history,
// so the phone's back button closes it and never leaves the page.
export type MenuEntry = { to: string; icon: UiIconName; label: string; end?: boolean; count?: number; noun?: string; warn?: boolean; showZero?: boolean }
export type MenuGroup = { label: string; entries: MenuEntry[] }

export const menuHistoryKey = 'rvMenu'
export const isMenuState = (state: unknown) => Boolean(state && typeof state === 'object' && (state as Record<string, unknown>)[menuHistoryKey])

// The sum of the pending counts the menu's own badges show (nothing is read again for it).
// `except` leaves out the pages that have a target of their own in the dock (their count is drawn on that target).
export const menuPendingTotal = (groups: MenuGroup[], except: string[] = []) => groups.reduce((sum, group) => sum + group.entries.reduce((inner, entry) => inner + (typeof entry.count === 'number' && entry.count > 0 && !except.includes(entry.to) ? entry.count : 0), 0), 0)
export const badgeText = (count: number) => count > 99 ? '99+' : String(count)

export function useDockHostState(phone: boolean) {
  const [actionsEl, setActionsEl] = useState<HTMLElement | null>(null)
  const [stripEl, setStripEl] = useState<HTMLElement | null>(null)
  const [entries, setEntries] = useState<Array<{ id: string; hasInfo: boolean }>>([])
  const register = useCallback((id: string, hasInfo: boolean) => {
    setEntries(current => [...current.filter(entry => entry.id !== id), { id, hasInfo }])
    return () => setEntries(current => current.filter(entry => entry.id !== id))
  }, [])
  const top = entries[entries.length - 1]
  const host = useMemo<DockHost>(() => ({ phone, actionsEl, infoEl: stripEl, topId: top?.id ?? null, register }), [phone, actionsEl, stripEl, top?.id, register])
  return { host, taskActive: Boolean(top), hasInfo: Boolean(top?.hasInfo), setActionsEl, setStripEl }
}

export { DockContext }

const dockPages: Array<{ to: string; icon: UiIconName; label: string; end?: boolean; aria?: string; noun?: string }> = [
  { to: '/dashboard', icon: 'dashboard', label: 'Ana Sayfa', end: true },
  { to: '/orders', icon: 'orders', label: 'Siparişler' },
  { to: '/products', icon: 'products', label: 'Ürünler' },
  { to: '/questions', icon: 'questions', label: 'Sorular', aria: 'Ürün Soruları', noun: 'cevaplanmamış' },
]

// The keyboard is open while a text box has focus: the dock steps aside (bottom-nav.css does it with :has()).
// The panel: the bottom row holds the four targets - or, in task mode, a Menü button and the page's buttons; in task mode
// the status strip is a top row above it.
export function BottomDock({ menuOpen, menuCount, slotCounts, taskActive, hasInfo, unread, unreadError, setActionsEl, setStripEl, menuSlotRef, onToggleMenu, onNavigate, onOpenNotifications }: {
  menuOpen: boolean
  menuCount: number
  // The pending count drawn on a page's own target ("/orders": 9).
  slotCounts: Record<string, number>
  taskActive: boolean
  hasInfo: boolean
  // Unread notifications (the pill above the dock, or the bell at the end of the strip) and whether one is an error.
  unread: number
  unreadError: boolean
  setActionsEl: (element: HTMLElement | null) => void
  setStripEl: (element: HTMLElement | null) => void
  menuSlotRef: RefObject<HTMLButtonElement | null>
  onToggleMenu: () => void
  // Leaves the open menu for a page (replaces the menu's history entry).
  onNavigate: (to: string) => void
  onOpenNotifications: () => void
}) {
  const { pathname } = useLocation()
  const task = taskActive && !menuOpen
  const strip = task && (hasInfo || unread > 0)
  const rows = strip ? 2 : 1
  const [infoHidden, setInfoHidden] = useState(false)
  useEffect(() => {
    let last = window.scrollY
    const onScroll = () => {
      const y = Math.max(0, window.scrollY)
      const delta = y - last
      if (y < 24) setInfoHidden(false)
      else if (delta > 6) setInfoHidden(true)
      else if (delta < -6) setInfoHidden(false)
      if (Math.abs(delta) > 6 || y < 24) last = y
    }
    window.addEventListener('scroll', onScroll, { passive: true })
    return () => window.removeEventListener('scroll', onScroll)
  }, [])
  const isActive = (to: string, end?: boolean) => !menuOpen && (end ? pathname === to : pathname === to || pathname.startsWith(to + '/'))
  const follow = (to: string) => (event: MouseEvent<HTMLAnchorElement>) => {
    if (!menuOpen || event.metaKey || event.ctrlKey || event.shiftKey || event.button !== 0) return
    event.preventDefault()
    onNavigate(to)
  }
  const slot = (page: { to: string; icon: UiIconName; label: string; end?: boolean; aria?: string; noun?: string }) => {
    const active = isActive(page.to, page.end)
    const count = slotCounts[page.to] ?? 0
    return <Link key={page.to} to={page.to} className={'rv-dock-slot' + (active ? ' is-active' : '')} aria-label={count > 0 ? (page.aria ?? page.label) + ', ' + count + ' ' + (page.noun ?? 'bekleyen') : (page.aria ?? page.label)} aria-current={active ? 'page' : undefined} onClick={follow(page.to)}>
      <span className="rv-dock-icon"><UiIcon name={page.icon} size={20} />{count > 0 ? <span className="rv-dock-badge" aria-hidden="true">{badgeText(count)}</span> : null}</span><span className="rv-dock-label">{page.label}</span>
    </Link>
  }
  const menuAria = menuOpen ? 'Menüyü Kapat' : menuCount > 0 ? 'Menü, ' + menuCount + ' bekleyen işlem' : 'Menü'
  const showStripBell = strip && unread > 0
  const bellLabel = 'Bildirimler, ' + unread + ' okunmamış'
  return <div className="rv-dock-layer" data-mode={task ? 'task' : 'dock'} data-rows={rows}>
    <div className="rv-dock-top" data-top={strip ? 'info' : 'none'} inert={rows === 1 || undefined}>
      <div className="rv-dock-info" data-visible={strip && !infoHidden ? 'true' : 'false'}>
        <div className="rv-dock-info-slot" ref={setStripEl} />
        {showStripBell ? <button type="button" className={'rv-dock-bell' + (unreadError ? ' is-error' : '')} aria-label={bellLabel} aria-haspopup="dialog" onClick={onOpenNotifications}><UiIcon name="bell" size={14} />{badgeText(unread)}</button> : null}
      </div>
    </div>
    {!showStripBell && !menuOpen && unread > 0 ? <button type="button" className={'rv-dock-pill-bell' + (unreadError ? ' is-error' : '')} aria-label={bellLabel} aria-haspopup="dialog" onClick={onOpenNotifications}><UiIcon name="bell" size={15} /><span>{badgeText(unread)}</span></button> : null}
    <div className="rv-dock-bottom">
      <nav className="rv-dock" aria-label="Alt menü" inert={task || undefined}>
        {dockPages.map(slot)}
        <button type="button" ref={menuSlotRef} className={'rv-dock-slot' + (menuOpen ? ' is-active' : '')} aria-label={menuAria} aria-haspopup="dialog" aria-expanded={menuOpen} onClick={onToggleMenu}>
          <span className="rv-dock-icon"><UiIcon name={menuOpen ? 'close' : 'menu'} size={20} />{!menuOpen && menuCount > 0 ? <span className="rv-dock-badge" aria-hidden="true">{badgeText(menuCount)}</span> : null}</span><span className="rv-dock-label">{menuOpen ? 'Kapat' : 'Menü'}</span>
        </button>
      </nav>
      <div className="rv-taskbar" inert={!task || undefined}>
        <button type="button" className="rv-taskbar-menu" aria-label="Menüyü Aç" aria-haspopup="dialog" onClick={onToggleMenu}><UiIcon name="menu" size={20} />{menuCount > 0 ? <span className="rv-dock-badge" aria-hidden="true">{badgeText(menuCount)}</span> : null}</button>
        <div className="rv-taskbar-actions" ref={setActionsEl} />
      </div>
    </div>
  </div>
}

const focusableSelector = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'
const turkishKey = (value: string) => value.toLocaleLowerCase('tr-TR')

// The full-screen menu: the page's own colour over the top bar, rising from the bottom; the dock stays below it.
// The menu is the app's one menu definition (App.tsx's groups, filtered the way the sidebar is), drawn as cards.
export function FullScreenMenu({ groups, storeName, theme, unread, unreadError, onToggleTheme, onLogout, onClose, onNavigate, onOpenNotifications, onSearch, pathname }: {
  groups: MenuGroup[]
  storeName: string
  theme: 'light' | 'dark'
  unread: number
  unreadError: boolean
  onOpenNotifications: () => void
  onSearch: () => void
  onToggleTheme: () => void
  onLogout: () => void
  onClose: () => void
  onNavigate: (to: string) => void
  pathname: string
}) {
  const rootRef = useRef<HTMLDivElement>(null)
  const [query, setQuery] = useState('')
  useScrollLock(true)
  useEffect(() => {
    rootRef.current?.focus({ preventScroll: true })
    // Everything behind the layer (the page and its top bar) is out of reach for the keyboard and for screen readers.
    const main = document.querySelector<HTMLElement>('.app-shell.stitch-shell > main')
    main?.setAttribute('inert', '')
    return () => main?.removeAttribute('inert')
  }, [])
  const trap = (event: KeyboardEvent | ReactKeyboardEvent) => {
    // The notification sheet over the menu owns Esc and Tab while it is open.
    if (document.querySelector('.rv-notification-history')) return
    if (event.key === 'Escape') { event.preventDefault(); onClose(); return }
    if (event.key !== 'Tab') return
    // The dock stays usable (it holds the Kapat slot), so the cycle runs through the menu and then the dock.
    const scopes = [rootRef.current, document.querySelector('.rv-dock')]
    const items = scopes.flatMap(scope => scope ? [...scope.querySelectorAll<HTMLElement>(focusableSelector)] : []).filter(element => element.offsetParent !== null || element === document.activeElement)
    if (!items.length) return
    const first = items[0]
    const last = items[items.length - 1]
    const current = document.activeElement
    if (event.shiftKey && (current === first || current === rootRef.current)) { event.preventDefault(); last.focus() }
    else if (!event.shiftKey && current === last) { event.preventDefault(); first.focus() }
  }
  useEffect(() => {
    const listener = (event: KeyboardEvent) => trap(event)
    document.addEventListener('keydown', listener)
    return () => document.removeEventListener('keydown', listener)
  })
  const needle = turkishKey(query.trim())
  const shown = groups.map(group => ({ ...group, entries: needle ? group.entries.filter(entry => turkishKey(entry.label).includes(needle)) : group.entries })).filter(group => group.entries.length)
  const row = (entry: MenuEntry): ReactNode => {
    const hasCount = typeof entry.count === 'number' && entry.count > 0
    const active = entry.end ? pathname === entry.to : pathname === entry.to || pathname.startsWith(`${entry.to}/`)
    const countLabel = typeof entry.count === 'number' && entry.count > 999 ? '999 üzeri' : entry.count?.toLocaleString('tr-TR')
    return <Link key={entry.to} to={entry.to} className={`rv-fullmenu-row${active ? ' is-current' : ''}`} aria-current={active ? 'page' : undefined} aria-label={hasCount ? `${entry.label}, ${countLabel} ${entry.noun ?? 'bildirim'}` : undefined}
      onClick={event => { if (event.metaKey || event.ctrlKey || event.shiftKey || event.button !== 0) return; event.preventDefault(); onNavigate(entry.to) }}>
      <UiIcon name={entry.icon} size={20} />
      <span className="rv-fullmenu-name">{entry.label}</span>
      {hasCount ? <span className={`rv-fullmenu-count${entry.warn ? ' is-warn' : ''}`} aria-hidden="true">{entry.count! > 999 ? '999+' : entry.count!.toLocaleString('tr-TR')}</span> : null}
      <UiIcon className="rv-fullmenu-chev" name="chevronRight" size={16} />
    </Link>
  }
  return <div ref={rootRef} className="rv-fullmenu" role="dialog" aria-modal="true" aria-label="Menü" tabIndex={-1}>
    <header className="rv-fullmenu-head"><h2>Menü</h2><div className="rv-fullmenu-tools">
      <button type="button" className="rv-fullmenu-tool" aria-label="Ara" aria-haspopup="dialog" onClick={onSearch}><UiIcon name="search" size={18} /></button>
      <button type="button" className="rv-fullmenu-tool" aria-label={unread > 0 ? `Bildirimler, ${unread} okunmamış` : 'Bildirimler'} aria-haspopup="dialog" onClick={onOpenNotifications}><UiIcon name="bell" size={18} />{unread > 0 ? <span className={`rv-fullmenu-tool-badge${unreadError ? ' is-error' : ''}`} aria-hidden="true">{badgeText(unread)}</span> : null}</button>
      <button type="button" className="rv-fullmenu-tool" aria-label={theme === 'light' ? 'Koyu temaya geç' : 'Açık temaya geç'} aria-pressed={theme === 'dark'} onClick={onToggleTheme}><UiIcon name={theme === 'light' ? 'moon' : 'sun'} size={18} /></button>
    </div></header>
    <div className="rv-fullmenu-search"><UiIcon name="search" size={18} /><input type="search" value={query} onChange={event => setQuery(event.target.value)} placeholder="Sayfa Ara" aria-label="Sayfa Ara" autoComplete="off" enterKeyHint="search" /></div>
    <div className="rv-fullmenu-body">
      {shown.map(group => <section className="rv-fullmenu-group" key={group.label} aria-label={group.label}><h3>{group.label}</h3><div className="rv-fullmenu-list">{group.entries.map(row)}</div></section>)}
      {!shown.length ? <p className="rv-fullmenu-empty" role="status">Eşleşen Sayfa Yok</p> : null}
    <footer className="rv-fullmenu-foot">
      <span className="rv-fullmenu-store" title={storeName}>{storeName}</span>
      <button type="button" className="rv-fullmenu-action is-danger" onClick={onLogout}><UiIcon name="logout" size={18} />Çıkış</button>
    </footer>
    </div>
  </div>
}

// Open / close of the full menu in the browser history: opening adds an entry on the same address (state rvMenu), the
// phone's back button and every "close" take it away again, and leaving for a page replaces it, so back never lands on
// the page the menu was opened from twice and never leaves the app.
export function useMenuHistory() {
  const navigate = useNavigate()
  const location = useLocation()
  const open = isMenuState(location.state)
  const openMenu = useCallback(() => { if (!open) navigate(`${location.pathname}${location.search}`, { state: { ...(location.state as object | null), [menuHistoryKey]: true } }) }, [navigate, open, location.pathname, location.search, location.state])
  const closeMenu = useCallback(() => { if (open) navigate(-1) }, [navigate, open])
  const leaveTo = useCallback((to: string) => {
    if (to === location.pathname) { closeMenu(); return }
    navigate(to, { replace: true })
  }, [navigate, location.pathname, closeMenu])
  return { open, openMenu, closeMenu, leaveTo }
}

// The notification sheet (phone) is an entry of the history too, like the menu: back closes it, leaving for a page replaces it.
export const notificationHistoryKey = 'rvNotifications'
export function useNotificationHistory() {
  const navigate = useNavigate()
  const location = useLocation()
  const open = Boolean(location.state && typeof location.state === 'object' && (location.state as Record<string, unknown>)[notificationHistoryKey])
  const openSheet = useCallback(() => { if (!open) navigate(`${location.pathname}${location.search}`, { state: { ...(location.state as object | null), [notificationHistoryKey]: true } }) }, [navigate, open, location.pathname, location.search, location.state])
  const closeSheet = useCallback(() => { if (open) navigate(-1) }, [navigate, open])
  const leaveTo = useCallback((href: string) => navigate(href, { replace: true }), [navigate])
  return { open, openSheet, closeSheet, leaveTo }
}
