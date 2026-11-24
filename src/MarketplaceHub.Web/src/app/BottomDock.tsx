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
export const menuPendingTotal = (groups: MenuGroup[]) => groups.reduce((sum, group) => sum + group.entries.reduce((inner, entry) => inner + (typeof entry.count === 'number' && entry.count > 0 ? entry.count : 0), 0), 0)
export const badgeText = (count: number) => count > 99 ? '99+' : String(count)

export function useDockHostState(phone: boolean) {
  const [actionsEl, setActionsEl] = useState<HTMLElement | null>(null)
  const [infoEl, setInfoEl] = useState<HTMLElement | null>(null)
  const [entries, setEntries] = useState<Array<{ id: string; hasInfo: boolean }>>([])
  const register = useCallback((id: string, hasInfo: boolean) => {
    setEntries(current => [...current.filter(entry => entry.id !== id), { id, hasInfo }])
    return () => setEntries(current => current.filter(entry => entry.id !== id))
  }, [])
  const top = entries[entries.length - 1]
  const host = useMemo<DockHost>(() => ({ phone, actionsEl, infoEl, topId: top?.id ?? null, register }), [phone, actionsEl, infoEl, top?.id, register])
  return { host, taskActive: Boolean(top), hasInfo: Boolean(top?.hasInfo), setActionsEl, setInfoEl }
}

export { DockContext }

const dockPages: Array<{ to: string; icon: UiIconName; label: string; end?: boolean }> = [
  { to: '/dashboard', icon: 'dashboard', label: 'Ana Sayfa', end: true },
  { to: '/orders', icon: 'orders', label: 'Siparişler' },
]
const dockPagesAfter: Array<{ to: string; icon: UiIconName; label: string; end?: boolean }> = [
  { to: '/products', icon: 'products', label: 'Ürünler' },
]

// The keyboard is open while a text box has focus: the bar steps aside (bottom-nav.css does it with :has()).
export function BottomDock({ menuOpen, menuCount, taskActive, hasInfo, setActionsEl, setInfoEl, menuSlotRef, onToggleMenu, onSearch, onNavigate }: {
  menuOpen: boolean
  menuCount: number
  taskActive: boolean
  hasInfo: boolean
  setActionsEl: (element: HTMLElement | null) => void
  setInfoEl: (element: HTMLElement | null) => void
  menuSlotRef: RefObject<HTMLButtonElement | null>
  onToggleMenu: () => void
  onSearch: () => void
  // Leaves the open menu for a page (replaces the menu's history entry).
  onNavigate: (to: string) => void
}) {
  const { pathname } = useLocation()
  const task = taskActive && !menuOpen
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
  const isActive = (to: string, end?: boolean) => !menuOpen && (end ? pathname === to : pathname === to || pathname.startsWith(`${to}/`))
  const follow = (to: string) => (event: MouseEvent<HTMLAnchorElement>) => {
    if (!menuOpen || event.metaKey || event.ctrlKey || event.shiftKey || event.button !== 0) return
    event.preventDefault()
    onNavigate(to)
  }
  const slot = (page: { to: string; icon: UiIconName; label: string; end?: boolean }) => {
    const active = isActive(page.to, page.end)
    return <Link key={page.to} to={page.to} className={`rv-dock-slot${active ? ' is-active' : ''}`} aria-label={page.label} aria-current={active ? 'page' : undefined} onClick={follow(page.to)}>
      <span className="rv-dock-pill"><UiIcon name={page.icon} size={20} />{active ? <span className="rv-dock-label">{page.label}</span> : null}</span>
    </Link>
  }
  const menuLabel = menuOpen ? 'Kapat' : 'Menü'
  const menuAria = menuOpen ? 'Menüyü Kapat' : menuCount > 0 ? `Menü, ${menuCount} bekleyen işlem` : 'Menü'
  return <div className="rv-dock-layer" data-mode={task ? 'task' : 'dock'}>
    <div className="rv-dock-info" ref={setInfoEl} data-visible={task && hasInfo && !infoHidden ? 'true' : 'false'} />
    <nav className="rv-dock" aria-label="Alt menü" inert={task || undefined}>
      {dockPages.map(slot)}
      <div className="rv-dock-slot rv-dock-slot-center"><button type="button" className="rv-dock-search" aria-label="Ara" aria-haspopup="dialog" onClick={onSearch}><UiIcon name="search" size={20} /></button></div>
      {dockPagesAfter.map(slot)}
      <button type="button" ref={menuSlotRef} className={`rv-dock-slot${menuOpen ? ' is-active' : ''}`} aria-label={menuAria} aria-haspopup="dialog" aria-expanded={menuOpen} onClick={onToggleMenu}>
        <span className="rv-dock-pill"><span className="rv-dock-icon"><UiIcon name={menuOpen ? 'close' : 'menu'} size={20} />{!menuOpen && menuCount > 0 ? <span className="rv-dock-badge" aria-hidden="true">{badgeText(menuCount)}</span> : null}</span>{menuOpen ? <span className="rv-dock-label">{menuLabel}</span> : null}</span>
      </button>
    </nav>
    <div className="rv-taskbar" inert={!task || undefined}>
      <button type="button" className="rv-taskbar-menu" aria-label="Menüyü Aç" aria-haspopup="dialog" onClick={onToggleMenu}><UiIcon name="menu" size={20} />{menuCount > 0 ? <span className="rv-dock-badge" aria-hidden="true">{badgeText(menuCount)}</span> : null}</button>
      <div className="rv-taskbar-actions" ref={setActionsEl} />
    </div>
  </div>
}

const focusableSelector = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'
const turkishKey = (value: string) => value.toLocaleLowerCase('tr-TR')

// The full-screen menu: the page's own colour over the top bar, rising from the bottom; the dock stays below it.
// The menu is the app's one menu definition (App.tsx's groups, filtered the way the sidebar is), drawn as cards.
export function FullScreenMenu({ groups, storeName, theme, onToggleTheme, onLogout, onClose, onNavigate, pathname }: {
  groups: MenuGroup[]
  storeName: string
  theme: 'light' | 'dark'
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
    const hasCount = typeof entry.count === 'number' && (entry.count > 0 || entry.showZero)
    const active = entry.end ? pathname === entry.to : pathname === entry.to || pathname.startsWith(`${entry.to}/`)
    const countLabel = typeof entry.count === 'number' && entry.count > 999 ? '999 üzeri' : entry.count?.toLocaleString('tr-TR')
    return <Link key={entry.to} to={entry.to} className={`rv-fullmenu-row${active ? ' is-current' : ''}`} aria-current={active ? 'page' : undefined} aria-label={hasCount ? `${entry.label}, ${countLabel} ${entry.noun ?? 'bildirim'}` : undefined}
      onClick={event => { if (event.metaKey || event.ctrlKey || event.shiftKey || event.button !== 0) return; event.preventDefault(); onNavigate(entry.to) }}>
      <UiIcon name={entry.icon} size={20} />
      <span className="rv-fullmenu-name">{entry.label}</span>
      {hasCount ? <span className={`rv-fullmenu-count${entry.warn && entry.count! > 0 ? ' is-warn' : ''}`} aria-hidden="true">{entry.count! > 999 ? '999+' : entry.count!.toLocaleString('tr-TR')}</span> : null}
    </Link>
  }
  return <div ref={rootRef} className="rv-fullmenu" role="dialog" aria-modal="true" aria-label="Menü" tabIndex={-1}>
    <header className="rv-fullmenu-head"><h2>Menü</h2><span className="rv-fullmenu-store" title={storeName}>{storeName}</span></header>
    <div className="rv-fullmenu-search"><UiIcon name="search" size={18} /><input type="search" value={query} onChange={event => setQuery(event.target.value)} placeholder="Sayfa Ara" aria-label="Sayfa Ara" autoComplete="off" enterKeyHint="search" /></div>
    <div className="rv-fullmenu-body">
      {shown.map(group => <section className="rv-fullmenu-group" key={group.label} aria-label={group.label}><h3>{group.label}</h3><div className="rv-fullmenu-card">{group.entries.map(row)}</div></section>)}
      {!shown.length ? <p className="rv-fullmenu-empty" role="status">Eşleşen Sayfa Yok</p> : null}
    </div>
    <footer className="rv-fullmenu-foot">
      <button type="button" className="rv-fullmenu-action" aria-pressed={theme === 'dark'} onClick={onToggleTheme}><UiIcon name={theme === 'light' ? 'moon' : 'sun'} size={18} />{theme === 'light' ? 'Koyu Tema' : 'Açık Tema'}</button>
      <button type="button" className="rv-fullmenu-action is-danger" onClick={onLogout}><UiIcon name="logout" size={18} />Çıkış</button>
    </footer>
  </div>
}

// Open / close of the full menu in the browser history: opening adds an entry on the same address (state rvMenu), the
// phone's back button and every "close" take it away again, and leaving for a page replaces it, so back never lands on
// the page the menu was opened from twice and never leaves the app.
export function useMenuHistory() {
  const navigate = useNavigate()
  const location = useLocation()
  const open = isMenuState(location.state)
  const openMenu = useCallback(() => { if (!open) navigate(`${location.pathname}${location.search}`, { state: { [menuHistoryKey]: true } }) }, [navigate, open, location.pathname, location.search])
  const closeMenu = useCallback(() => { if (open) navigate(-1) }, [navigate, open])
  const leaveTo = useCallback((to: string) => {
    if (to === location.pathname) { closeMenu(); return }
    navigate(to, { replace: true })
  }, [navigate, location.pathname, closeMenu])
  return { open, openMenu, closeMenu, leaveTo }
}
