import { act, useMemo, useRef, useState, type ReactNode } from 'react'
import { MemoryRouter, useLocation, useNavigate } from 'react-router'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render } from '../test/render'
import { StickyActionBar } from '../shared/components/StickyActionBar'
import { Tabs } from '../shared/components/DesignSystem'
import { DraftSaveBar } from '../shared/draft-edits'
import { DockContext } from '../shared/components/dock-actions'
import { lockPageScroll } from '../shared/hooks/useScrollLock'
import { BackTargetContext, useBackTarget, type BackTarget } from '../shared/components/back-target'
import { phoneShellMaxWidth, phoneShellQuery } from '../shared/phone-shell'
import { BottomDock, FullScreenMenu, menuPendingTotal, useBackNavigation, useDockHostState, useMenuHistory, type MenuGroup } from './BottomDock'

// node:fs without @types/node, the way quick-edit-scroll.test.ts reads files.
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
const testFileUrl = import.meta.url
const css = fs.readFileSync(new URL('./bottom-nav.css', testFileUrl), 'utf8')

afterEach(() => { cleanup(); document.body.removeAttribute('style') })

const groups: MenuGroup[] = [
  { label: 'Ana Menü', entries: [
    { to: '/dashboard', icon: 'dashboard', label: 'Genel Bakış', end: true },
    { to: '/orders', icon: 'orders', label: 'Siparişler', count: 2, showZero: true, noun: 'bekleyen sipariş' },
    { to: '/returns', icon: 'returns', label: 'İadeler', count: 0, showZero: true },
    { to: '/invoices', icon: 'invoice', label: 'Faturalar', count: 3, showZero: true, warn: true },
    { to: '/questions', icon: 'questions', label: 'Ürün Soruları', count: 7, showZero: true, noun: 'cevaplanmamış' },
  ] },
  { label: 'Sistem', entries: [{ to: '/settings', icon: 'settings', label: 'Sistem Ayarları', end: true }] },
]

const profile = { name: 'Ayşe Yılmaz', role: 'Hesap Sahibi', email: 'ayse@ravencia.com' }
let pathNow = ''
let searchNow = ''
function Harness({ phone = true, count = 0, slotCounts = {}, children }: { phone?: boolean; count?: number; slotCounts?: Record<string, number>; children?: ReactNode }) {
  const dock = useDockHostState(phone)
  const menu = useMenuHistory()
  const navigate = useNavigate()
  const location = useLocation()
  const slotRef = useRef<HTMLButtonElement>(null)
  const [backTarget, setBackTarget] = useState<BackTarget | null>(null)
  const backHost = useMemo(() => ({ target: backTarget, set: setBackTarget }), [backTarget])
  const goBack = useBackNavigation(backTarget)
  pathNow = location.pathname
  searchNow = location.search
  return <BackTargetContext.Provider value={backHost}><DockContext.Provider value={dock.host}>
    <button type="button" id="back" onClick={() => navigate(-1)}>geri</button>
    {menu.open && <FullScreenMenu groups={groups} profile={profile} onLogout={() => undefined} onClose={menu.closeMenu} onNavigate={menu.leaveTo} pathname={location.pathname} />}
    <BottomDock menuOpen={menu.open} menuCount={count} slotCounts={slotCounts} taskActive={dock.taskActive} hasInfo={dock.hasInfo} setActionsEl={dock.setActionsEl} setStripEl={dock.setStripEl} menuSlotRef={slotRef} onToggleMenu={menu.open ? menu.closeMenu : menu.openMenu} onNavigate={menu.leaveTo} /><button type="button" id="topback" onClick={goBack}>üst geri</button>
    {children}
  </DockContext.Provider></BackTargetContext.Provider>
}
function Registers({ to }: { to: string }) { useBackTarget({ to, label: 'Ürünler' }); return null }
const mount = (path: string, props: Parameters<typeof Harness>[0] = {}) => render(<MemoryRouter initialEntries={[path]}><Harness {...props} /></MemoryRouter>)
// jsdom has no layout: the task bar row is 250 px wide and every visible action button is 100 px (a split button 140 px).
function fakeRowWidths() {
  const unit = (el: Element) => (el.querySelector('[role="group"]') ? 140 : 100)
  Object.defineProperty(HTMLElement.prototype, 'clientWidth', { configurable: true, get() { return (this as HTMLElement).classList.contains('rv-taskbar-group') ? 250 : 0 } })
  Object.defineProperty(HTMLElement.prototype, 'scrollWidth', { configurable: true, get() {
    const row = this as HTMLElement
    if (!row.classList.contains('rv-taskbar-group')) return 0
    const visible = [...row.children].filter(child => !child.hasAttribute('data-dock-other') && !(child.matches('[role="group"]') && child.querySelector('[data-dock-other]')))
    return visible.reduce((sum, child) => sum + (child.matches('[role="group"]') ? 140 : unit(child)), 0)
  } })
  return () => { delete (HTMLElement.prototype as unknown as Record<string, unknown>).clientWidth; delete (HTMLElement.prototype as unknown as Record<string, unknown>).scrollWidth }
}
const click = (element: Element | null) => act(() => { (element as HTMLElement).click() })

describe('phone bottom navigation (CSS)', () => {
  it('is drawn only up to the phone shell width (760 px, the same as the JS query), where the hamburger goes away and the page keeps room under its content', () => {
    expect(css).toMatch(/^\.rv-dock-layer \{ display: none; \}/m)
    expect(css).toContain(`@media (max-width: ${phoneShellMaxWidth}px) {`)
    expect(phoneShellQuery).toBe('(max-width: 760px)')
    const phone = css.slice(css.indexOf(`@media (max-width: ${phoneShellMaxWidth}px)`))
    expect(phone).toContain('> main { padding-bottom: var(--rv-bottom-nav-space); transition: padding-bottom var(--rv-motion-bar) var(--rv-ease-bar); }')
    expect(phone).toContain('.rv-topbar .rv-mobile-menu-toggle { display: none; }')
    expect(phone).not.toContain('rv-phonebar')
    // Room under the content: 56 + 12 + 16 and the safe area (the warning pill adds 40); the parts sit 12 px and the safe area above the edge.
    expect(phone).toContain('--rv-bottom-nav-space: calc(var(--rv-dock-bottom) + 74px)')
    expect(phone).toContain('--rv-dock-bottom: max(calc(env(safe-area-inset-bottom, 0px) + 4px), 12px)')
    const layer = phone.match(/\.rv-dock-layer \{ position: fixed[^}]*\}/)![0]
    expect(layer).toContain('pointer-events: none')
    expect(layer).toContain('bottom: var(--rv-dock-bottom)')
    expect(layer).toContain('padding: 0 8px')
    // One floating glass capsule (light frost, 8 px blur (10 at most)): a thin line and a light shadow, no frosted band, no indicator line.
    expect(phone).toContain('--rv-dock-shadow: 0 0 1.25px rgb(0 0 0 / 40%), 0 0 24px rgb(0 0 0 / 10%), 0 4px 12px rgb(0 0 0 / 6%)')
    expect(phone).toContain('--rv-dock-shadow: inset 0 0 0 1px rgb(255 255 255 / 6%), 0 0 24px rgb(0 0 0 / 40%), 0 4px 12px rgb(0 0 0 / 30%)')
    expect(phone).toContain('.rv-dock, .rv-dock-panel { background: color-mix(in srgb, var(--rv-color-surface) var(--rv-dock-glass-alpha), transparent); box-shadow: var(--rv-dock-glass-shadow); -webkit-backdrop-filter: blur(5px) saturate(1.3); backdrop-filter: blur(5px) saturate(1.3); }')
    expect(phone).toContain('--rv-dock-glass-alpha: 22%')
    expect(phone).toContain('--rv-dock-glass-alpha: 88%')
    expect(phone).toContain('inset 0 0 0 1px rgb(0 0 0 / 10%)')
    expect(phone).toContain('inset 0 0 0 1px rgb(255 255 255 / 14%)')
    expect(phone).toContain('text-shadow: 0 0 4px var(--rv-dock-halo)')
    expect(phone).toContain('color: var(--rv-color-ink); background: none; border: 0; border-radius: 999px')
    expect(phone).toContain('prefers-reduced-transparency: reduce')
    // The capsule is the same glass with the menu open: no rule restyles it for the menu (colour, blur, size, position).
    expect(phone).not.toMatch(/:has\(\.rv-fullmenu\)[^{]*\.rv-dock\b/)
    expect(phone).toContain(':root { --rv-dock-w: min(calc(100vw - 28px), 332px); --rv-dock-h: 62px; --rv-dock-icon: 22px; --rv-dock-glide-motion: 420ms; --rv-dock-color-motion: 200ms;')
    expect(phone).not.toContain('data-compact')
    expect(phone).toContain('width: var(--rv-dock-w); height: var(--rv-dock-h); padding: 4px; border-radius: calc(var(--rv-dock-h) / 2);')
    expect(phone).toContain('.rv-dock-glide { position: absolute; top: 4.5px; bottom: 4.5px; left: calc(4.5px + var(--rv-dock-i, 0) * ((100% - 8px) / 5));')
    expect(phone).toMatch(/\.rv-dock-panel \{[^}]*height: 56px; border-radius: 28px/)
    expect(phone).toContain('.rv-dock-slot:active { transform: scale(.96); }')
    // The five icons are 24 px with a 2 px stroke (the global .ui-icon rule held them at 16 px).
    expect(phone).toContain('.rv-dock-icon > .ui-icon { width: var(--rv-dock-icon); height: var(--rv-dock-icon); stroke-width: 2px;')
    expect(phone).toContain('.rv-dock-slot.is-active { color: var(--rv-color-primary-text); }')
    expect(phone).toContain('--rv-dock-active-tint: 14%')
    expect(phone).toContain('--rv-dock-active-tint: 24%')
    expect(css).not.toMatch(/rv-dock-blur|rv-dock-tint|rv-dock-panel-alpha|rv-dock-panel-blur|\.rv-dock-layer::before/)
    expect(css).not.toMatch(/touch-action/)
    expect(phone).toContain('@media (prefers-reduced-motion: reduce)')
    // The warning is a 32 px pill floating above the task panel.
    expect(phone).toMatch(/\.rv-dock-top \{ position: absolute; bottom: calc\(100% \+ 8px\)/)
    expect(phone).toContain('height: 32px; padding: 0 14px')
    // The notification panel opens from the top edge, 75% of the screen at most, with no bottom anchor.
    expect(phone).toContain(".rv-notification-history { top: calc(62px + env(safe-area-inset-top, 0px) + 8px); right: 12px; bottom: auto; left: 12px;")
    expect(phone).toContain("max-height: min(75dvh")
    expect(phone).toContain('translate: 0 -28px')
    expect(phone).toContain('.rv-taskbar-group [data-dock-other] { display: none; }')
    expect(phone).toContain('[data-dock-step]::before { content: attr(data-dock-step); }')
    expect(phone).not.toContain('rv-taskbar-menu')
  })
})

describe('Ravencia Dock', () => {
  it('is one capsule of five icon-only slots (Ana Sayfa, Siparişler, Ürünler, Sorular, Menü) with a label under each icon and a glide pill; no Ara here; the open page is the active one', () => {
    const { host } = mount('/orders')
    const dock = host.querySelector('.rv-dock')!
    expect(dock.getAttribute('aria-label')).toBe('Alt menü')
    expect([...dock.children].map(part => part.className.split(' ')[0])).toEqual(['rv-dock-glide', 'rv-dock-slot', 'rv-dock-slot', 'rv-dock-slot', 'rv-dock-slot', 'rv-dock-slot'])
    expect(dock.children[1].getAttribute('aria-label')).toBe('Ana Sayfa')
    expect(dock.children[1].getAttribute('href')).toBe('/dashboard')
    expect([...dock.querySelectorAll('.rv-dock-slot')].map(slot => slot.getAttribute('aria-label'))).toEqual(['Ana Sayfa', 'Siparişler', 'Ürünler', 'Ürün Soruları', 'Menü'])
    expect(dock.children[5].getAttribute('aria-label')).toBe('Menü')
    expect(dock.querySelector('[aria-label="Ara"], .rv-dock-search')).toBeNull()
    // Every slot has its icon and a short label under it; the accessible name is the aria-label (label and count in one, no double reading).
    expect([...dock.querySelectorAll('.rv-dock-slot')].map(part => part.querySelector('.rv-dock-label')?.textContent)).toEqual(['Ana Sayfa', 'Siparişler', 'Ürünler', 'Sorular', 'Menü'])
    for (const part of dock.querySelectorAll('.rv-dock-slot')) { expect(part.querySelector('svg')).toBeTruthy(); expect(part.getAttribute('aria-label')).toContain(part.querySelector('.rv-dock-label')!.textContent!) }
    expect(dock.querySelector('.rv-dock-glide')?.getAttribute('aria-hidden')).toBe('true')
    expect(host.querySelector('.rv-dock-layer')?.getAttribute('data-rows')).toBe('1')
    const current = dock.querySelectorAll('[aria-current="page"]')
    expect(current).toHaveLength(1)
    expect(current[0].getAttribute('aria-label')).toBe('Siparişler')
    expect(dock.querySelectorAll('.is-active')).toHaveLength(1)
    expect(dock.querySelector('.is-active')?.getAttribute('aria-label')).toBe('Siparişler')
  })

  it('makes the Ana Sayfa slot the active one on the dashboard and the Sorular slot on its page', () => {
    expect(mount('/dashboard').host.querySelector('.rv-dock-slot.is-active')?.getAttribute('aria-label')).toBe('Ana Sayfa')
    cleanup()
    const { host } = mount('/questions')
    expect(host.querySelector('a[href="/questions"]')?.classList.contains('is-active')).toBe(true)
    expect(host.querySelectorAll('.rv-dock-slot.is-active')).toHaveLength(1)
  })

  it('puts the glide pill under the open page (its slot index), keeps it undrawn outside the dock and under Menü while the menu is open', () => {
    const glide = (host: HTMLElement) => host.querySelector<HTMLElement>('.rv-dock-glide')!
    const dashboard = mount('/dashboard').host
    expect(glide(dashboard).parentElement!.style.getPropertyValue('--rv-dock-i')).toBe('0')
    cleanup()
    const questions = mount('/questions').host
    expect(glide(questions).parentElement!.style.getPropertyValue('--rv-dock-i')).toBe('3')
    expect(questions.querySelector('.rv-dock')!.hasAttribute('data-none')).toBe(false)
    cleanup()
    const reports = mount('/reports').host
    expect(reports.querySelector('.rv-dock')!.hasAttribute('data-none')).toBe(true)
    cleanup()
    const open = mount('/orders').host
    click(open.querySelector('button[aria-label^="Menü"]'))
    expect(glide(open).parentElement!.style.getPropertyValue('--rv-dock-i')).toBe('4')
  })

  it('shows the round Menü button as the active one with the cross while the menu is open, and no page is current', () => {
    const { host } = mount('/orders')
    const menuButton = () => host.querySelector('.rv-dock > button')!
    expect(menuButton().getAttribute('aria-label')).toBe('Menü')
    const before = menuButton().innerHTML
    click(menuButton())
    expect(menuButton().classList.contains('is-active')).toBe(true)
    expect(menuButton().getAttribute('aria-label')).toBe('Menüyü Kapat')
    expect(menuButton().innerHTML).not.toBe(before)
    expect(host.querySelectorAll('.rv-dock [aria-current="page"]')).toHaveLength(0)
    expect(host.querySelectorAll('.rv-dock .is-active')).toHaveLength(1)
    cleanup()
    expect(mount('/reports').host.querySelectorAll('.rv-dock .is-active')).toHaveLength(0)
  })

  it('shows the Menü badge from 1 to 99 and as 99+ above, and none at 0', () => {
    expect(mount('/orders', { count: 0 }).host.querySelector('.rv-dock-badge')).toBeNull()
    cleanup()
    expect(mount('/orders', { count: 7 }).host.querySelector('.rv-dock .rv-dock-badge')?.textContent).toBe('7')
    cleanup()
    expect(mount('/orders', { count: 120 }).host.querySelector('.rv-dock .rv-dock-badge')?.textContent).toBe('99+')
    expect(menuPendingTotal(groups)).toBe(12)
    expect(menuPendingTotal(groups, ['/orders'])).toBe(10)
    // Pages with a target of their own in the dock are not counted twice in the Menü badge.
    expect(menuPendingTotal(groups, ['/orders', '/questions'])).toBe(3)
    expect(menuPendingTotal(groups, ['/orders', '/questions'])).toBeLessThanOrEqual(3)
    cleanup()
    const counted = mount('/dashboard', { slotCounts: { '/orders': 9, '/questions': 120 } }).host
    expect(counted.querySelector('a[href="/orders"] .rv-dock-badge')?.textContent).toBe('9')
    expect(counted.querySelector('a[href="/questions"] .rv-dock-badge')?.textContent).toBe('99+')
    expect(counted.querySelector('a[href="/returns"]')).toBeNull()
    expect(counted.querySelector('a[href="/questions"]')?.getAttribute('aria-label')).toBe('Ürün Soruları, 120 cevaplanmamış')
    cleanup()
    expect(mount('/dashboard').host.querySelector('a[href="/questions"] .rv-dock-badge')).toBeNull()
  })

  it('opens the full-screen menu from the Menü slot (which becomes Kapat) and closes it again from the same slot', () => {
    const { host } = mount('/orders')
    expect(document.querySelector('.rv-fullmenu')).toBeNull()
    click(host.querySelector('button[aria-label^="Menü"]'))
    const dialog = document.querySelector('.rv-fullmenu')!
    expect(dialog.getAttribute('role')).toBe('dialog')
    expect(dialog.getAttribute('aria-modal')).toBe('true')
    expect(document.body.style.overflow).not.toBe('hidden') // no body lock: it moved the dock in a Home Screen app (useScrollLock hold: false)
    const slot = host.querySelector('.rv-dock button[aria-expanded="true"]')!
    expect(slot.getAttribute('aria-label')).toBe('Menüyü Kapat')
    expect(host.querySelector('.rv-dock [aria-current="page"]')).toBeNull()
    click(slot)
    expect(document.querySelector('.rv-fullmenu')).toBeNull()
    expect(pathNow).toBe('/orders')
  })

  it('closes with Esc and with the back button, and the back button does not leave the page', () => {
    const { host } = mount('/orders')
    click(host.querySelector('button[aria-label^="Menü"]'))
    act(() => { document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })) })
    expect(document.querySelector('.rv-fullmenu')).toBeNull()
    click(host.querySelector('button[aria-label^="Menü"]'))
    expect(document.querySelector('.rv-fullmenu')).not.toBeNull()
    click(host.querySelector('#back'))
    expect(document.querySelector('.rv-fullmenu')).toBeNull()
    expect(pathNow).toBe('/orders')
  })

  it('is the title, the groups and, at the end of the Sistem group, the user and Çıkış as a danger row; no search field, no footer', () => {
    const { host } = mount('/orders')
    click(host.querySelector('button[aria-label^="Menü"]'))
    expect(document.querySelector('.rv-fullmenu-head')?.textContent).toBe('Menü')
    expect(document.querySelector('.rv-fullmenu-search, .rv-fullmenu-foot, .rv-fullmenu-tools, input')).toBeNull()
    const system = document.querySelector('.rv-fullmenu-group[aria-label="Sistem"]')!
    expect(system.querySelector('h3')?.textContent).toBe('Sistem')
    expect(document.querySelector('.rv-fullmenu-store')).toBeNull()
    // The profile card is the last item of the scrolling list, after the groups: initials avatar, name, role and e-mail, an arrow to the account page.
    const card = document.querySelector('.rv-fullmenu-body > .rv-fullmenu-profile:last-child')!
    expect(document.querySelector('.rv-fullmenu-body > :first-child')?.classList.contains('rv-fullmenu-group')).toBe(true)
    // Only a picture of who is signed in: a plain block, not a link, no arrow, nothing to tab to.
    expect(card.tagName).toBe('DIV')
    expect(card.getAttribute('href')).toBeNull()
    expect(card.querySelector('a, button, [tabindex]')).toBeNull()
    expect(card.querySelector('.rv-fullmenu-avatar')?.textContent).toBe('AY')
    expect(card.querySelector('strong')?.textContent).toBe('Ayşe Yılmaz')
    expect(card.querySelector('small')?.textContent).toBe('Hesap Sahibi · ayse@ravencia.com')
    expect(card.querySelector('.rv-fullmenu-role')?.textContent).toBe('Hesap Sahibi')
    expect(card.querySelector('.rv-fullmenu-chev')).toBeNull()
    const rows = [...system.querySelectorAll('.rv-fullmenu-row')]
    expect(rows.map(row => row.textContent)).toEqual(['Sistem Ayarları', 'Çıkış'])
    expect(rows[1].classList.contains('is-danger')).toBe(true)
    expect(rows[1].querySelector('.rv-fullmenu-chev')).toBeNull()
    // Only pages with something pending show a count (0 shows none).
    expect([...document.querySelectorAll('.rv-fullmenu-count')].map(count => count.textContent)).toEqual(['2', '3', '7'])
  })

  it('shows only the name, with no arrow and no link, when the session has no role or e-mail and there is no page to go to', () => {
    render(<MemoryRouter><FullScreenMenu groups={groups} profile={{ name: 'ilker' }} onLogout={() => undefined} onClose={() => undefined} onNavigate={() => undefined} pathname="/orders" /></MemoryRouter>)
    const card = document.querySelector('.rv-fullmenu-profile')!
    expect(card.tagName).toBe('DIV')
    expect(card.querySelector('small')).toBeNull()
    expect(card.querySelector('.rv-fullmenu-chev')).toBeNull()
    expect(card.querySelector('.rv-fullmenu-avatar')?.textContent).toBe('İ')
  })

  it('leaves the menu for a page by replacing its history entry, so back returns to the page it was opened on', () => {
    const { host } = mount('/orders')
    click(host.querySelector('button[aria-label^="Menü"]'))
    click([...document.querySelectorAll('.rv-fullmenu-row')].find(row => row.textContent?.startsWith('Faturalar')) ?? null)
    expect(pathNow).toBe('/invoices')
    expect(document.querySelector('.rv-fullmenu')).toBeNull()
    click(host.querySelector('#back'))
    expect(pathNow).toBe('/orders')
  })
})

describe('no bell in the dock or the menu', () => {
  it('has neither a pill above the dock nor a bell, theme or search button in the menu header', () => {
    const { host } = mount('/orders')
    expect(host.querySelector('.rv-dock-pill-bell, .rv-dock-bell')).toBeNull()
    click(host.querySelector('button[aria-label^="Menü"]'))
    expect(document.querySelectorAll('.rv-fullmenu-head button')).toHaveLength(0)
  })
})

describe('page tabs stay in the page', () => {
  it('draws a page tab strip in the page on a phone as well, and the dock has no tab row', () => {
    const picked: string[] = []
    const { host } = render(<MemoryRouter initialEntries={['/orders']}><Harness><Tabs ariaLabel="Sipariş durumları" value="ALL" onChange={value => picked.push(value)} items={[{ value: 'ALL', label: 'Tümü', count: 140 }, { value: 'NEW', label: 'Yeni', count: 8 }]} /></Harness></MemoryRouter>)
    expect(host.querySelector('.rv-dock-layer')?.getAttribute('data-rows')).toBe('1')
    expect(host.querySelector('.rv-dock-tab, .rv-dock-tabs')).toBeNull()
    const tabs = [...host.querySelectorAll<HTMLButtonElement>('.rv-tabs [role="tab"]')]
    expect(tabs).toHaveLength(2)
    click(tabs[1])
    expect(picked).toEqual(['NEW'])
  })
  it('keeps the dock one row beside a selection bar: the buttons in the dock, the status in the strip above', () => {
    const { host } = render(<MemoryRouter initialEntries={['/orders']}><Harness><Tabs ariaLabel="Sipariş durumları" value="ALL" onChange={() => undefined} items={[{ value: 'ALL', label: 'Tümü' }]} /><StickyActionBar label="Toplu işlemler" lead={<span>2 sipariş seçildi</span>}><button type="button">Sil</button></StickyActionBar></Harness></MemoryRouter>)
    expect(host.querySelector('.rv-tabs')).not.toBeNull()
    expect(host.querySelector('.rv-dock-layer')?.getAttribute('data-mode')).toBe('task')
    expect(host.querySelector('.rv-taskbar-actions')?.textContent).toBe('Sil')
    // No warning, so no strip: the dock stays one row; the count is the first line of the Diğer list.
    expect(host.querySelector('.rv-dock-layer')?.getAttribute('data-rows')).toBe('1')
  })
})

describe('task mode', () => {
  it('swaps the dock for the page actions while a sticky bar is open and brings the dock back when it closes', () => {
    const bar = (open: boolean) => <MemoryRouter initialEntries={['/products/new']}><Harness><StickyActionBar open={open} label="Ürün kaydetme" info={{ start: <button type="button" className="rv-dock-chip">8 Zorunlu Alan Eksik</button>, end: '1 Satış Satırı · 0 Kanal' }} lead={<span>durum</span>}><button type="button" data-dock-primary>Ürünü Oluştur</button></StickyActionBar></Harness></MemoryRouter>
    const { host, rerender } = render(bar(true))
    const layer = host.querySelector('.rv-dock-layer')!
    expect(layer.getAttribute('data-mode')).toBe('task')
    expect(host.querySelector('.rv-taskbar-actions')?.textContent).toBe('Ürünü Oluştur')
    // Only the warning is in the strip; the neutral summary is the first line of the Diğer list.
    expect(host.querySelector('.rv-dock-info')?.textContent).toBe('8 Zorunlu Alan Eksik')
    expect(host.querySelector('.rv-dock-layer')?.getAttribute('data-rows')).toBe('2')
    expect(host.querySelector('.rv-dock')?.hasAttribute('inert')).toBe(true)
    expect(host.querySelector('.rv-action-bar')).toBeNull()
    rerender(bar(false))
    expect(layer.getAttribute('data-mode')).toBe('dock')
    expect(host.querySelector('.rv-taskbar-actions')?.textContent).toBe('')
    expect(host.querySelector('.rv-dock-layer')?.getAttribute('data-rows')).toBe('1')
  })

  it('lays the page actions out as labelled buttons side by side: no Diğer, no Menü, no Geri while they fit', () => {
    const { host } = render(<MemoryRouter initialEntries={['/products/p1']}><Harness count={5}><StickyActionBar label="Ürün kaydetme" info={{ end: '33 Satış Satırı · 0 Kanal' }}>
      <button type="button" data-dock-primary>Kaydet</button>
      <button type="button" data-dock-step="Yayınlama" aria-label="Yayınlamaya Devam Et"><span>Yayınla</span><svg /></button>
    </StickyActionBar></Harness></MemoryRouter>)
    expect(host.querySelector('.rv-taskbar-more, [data-dock-other]')).toBeNull()
    expect(host.querySelector('.rv-taskbar-menu, .rv-taskbar-back')).toBeNull()
    expect(host.querySelector('.rv-taskbar .rv-dock-badge')).toBeNull()
    expect(host.querySelector('.rv-taskbar-group')?.hasAttribute('data-dock-fit')).toBe(false)
    // The step is drawn as its name (the page's CSS content: attr), with no arrow: the arrow icon is hidden by the bar's CSS rule.
    expect(host.querySelector('[data-dock-step]')?.getAttribute('data-dock-step')).toBe('Yayınlama')
  })

  it('fits a crowded row by measuring: tighter, smaller text, then the last other actions into Diğer (a split button as a whole), which runs them', () => {
    const restore = fakeRowWidths()
    try {
      const calls: string[] = []
      const { host } = render(<MemoryRouter initialEntries={['/products/p1']}><Harness><StickyActionBar label="Ürün kaydetme">
        <button type="button" data-dock-primary onClick={() => calls.push('kaydet')}>Kaydet</button>
        <div role="group" aria-label="Platform güncelleme"><button type="button" aria-label="Hızlı Güncelle" onClick={() => calls.push('hizli')}><span>Hızlı Güncelle</span></button><button type="button" aria-label="Platform güncelleme ayarları" onClick={() => calls.push('ayar')}><svg viewBox="0 0 4 4" /></button></div>
        <button type="button" data-dock-step="Yayınlama" aria-label="Yayınlamaya Devam Et" onClick={() => calls.push('ileri')}><span>Yayınla</span></button>
        <button type="button" className="rr-danger" aria-label="Platformda Sil" onClick={() => calls.push('sil')}>Sil</button>
      </StickyActionBar></Harness></MemoryRouter>)
      const group = host.querySelector('.rv-taskbar-group')!
      // 100 + 140 + 100 + 100 = 440 > 250: first the inner space and the text shrink, then the lowest priority goes (Sil, then the split button).
      expect(group.getAttribute('data-dock-fit')).toBe('2')
      expect([...group.querySelectorAll('button')].filter(button => button.hasAttribute('data-dock-other')).map(button => button.getAttribute('aria-label'))).toEqual(['Hızlı Güncelle', 'Platform güncelleme ayarları', 'Platformda Sil'])
      expect(group.querySelector('[role="group"]')?.hasAttribute('data-dock-other')).toBe(true)
      expect(host.querySelector('button[data-dock-primary]')?.hasAttribute('data-dock-other')).toBe(false)
      expect(host.querySelector('[data-dock-step]')?.hasAttribute('data-dock-other')).toBe(false)
      // The row is not "mid-crowded" now: the primary and the step are both there and the ⋯ appears only because something moved.
      click(host.querySelector('.rv-taskbar-more-button'))
      const list = host.querySelector('.rv-taskbar-more-list')!
      expect(list.querySelector('.rv-taskbar-more-summary')).toBeNull()
      const items = [...list.querySelectorAll<HTMLButtonElement>('.rv-taskbar-more-item')]
      expect(items.map(item => item.textContent)).toEqual(['Hızlı Güncelle', 'Platform güncelleme ayarları', 'Platformda Sil'])
      expect(items[2].classList.contains('is-danger')).toBe(true)
      click(items[0])
      expect(calls).toEqual(['hizli'])
      expect(host.querySelector('.rv-taskbar-more-list')).toBeNull()
      // A press on the scrim only closes the list; Esc closes it too.
      click(host.querySelector('.rv-taskbar-more-button'))
      click(host.querySelector('.rv-taskbar-more-scrim'))
      expect(host.querySelector('.rv-taskbar-more-list')).toBeNull()
      expect(calls).toEqual(['hizli'])
      click(host.querySelector('.rv-taskbar-more-button'))
      act(() => { document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })) })
      expect(host.querySelector('.rv-taskbar-more-list')).toBeNull()
    } finally { restore() }
  })

  it('keeps a select-all action (data-dock-keep) in the row longer than the other actions', () => {
    const restore = fakeRowWidths()
    try {
      const { host } = render(<MemoryRouter initialEntries={['/orders']}><Harness><StickyActionBar label="Toplu işlemler">
        <button type="button" data-dock-clear data-dock-count="1" aria-label="1 seçili, seçimi kaldır">Temizle</button>
        <button type="button" data-dock-keep>Sayfadakileri Seç (20)</button>
        <button type="button">Kargo</button>
      </StickyActionBar></Harness></MemoryRouter>)
      const state = (text: string) => Array.from(host.querySelectorAll('.rv-taskbar-actions button')).find(button => button.textContent === text)?.hasAttribute('data-dock-other')
      expect(state('Sayfadakileri Seç (20)')).toBe(false)
      expect(state('Kargo')).toBe(true)
    } finally { restore() }
  })

  it('hides the dock (inert, data-layer) while a window or sheet holds the page lock and brings it back when it closes; the full-screen menu keeps it', () => {
    const { host } = render(<MemoryRouter initialEntries={['/orders']}><Harness /></MemoryRouter>)
    const layer = () => host.querySelector('.rv-dock-layer')!
    expect(layer().hasAttribute('data-layer')).toBe(false)
    let release = () => undefined as void
    act(() => { release = lockPageScroll(true) })
    expect(layer().getAttribute('data-layer')).toBe('open')
    expect(layer().hasAttribute('inert')).toBe(true)
    expect(document.documentElement.hasAttribute('data-rv-layer')).toBe(true)
    act(() => release())
    expect(layer().hasAttribute('data-layer')).toBe(false)
    expect(layer().hasAttribute('inert')).toBe(false)
    expect(document.documentElement.hasAttribute('data-rv-layer')).toBe(false)
    click(host.querySelector('button[aria-label="Menü"]'))
    expect(host.querySelector('.rv-fullmenu')).toBeTruthy()
    expect(layer().hasAttribute('data-layer')).toBe(false)
  })

  it('hides the dock under the CSS rule for an open layer, with the reduced-motion exception', () => {
    expect(css).toContain('.rv-dock-layer[data-layer] { opacity: 0; visibility: hidden; pointer-events: none;')
    expect(css).toContain('@media (prefers-reduced-motion: reduce) { .rv-dock-layer, .rv-dock-layer[data-layer] { transition: none; } }')
  })

  it('keeps the selection chip in the bar whatever the space', () => {
    const restore = fakeRowWidths()
    try {
      const { host } = render(<MemoryRouter initialEntries={['/returns']}><Harness><StickyActionBar label="Toplu işlemler">
        <button type="button" data-dock-clear data-dock-count="3" aria-label="3 seçili, seçimi kaldır">Vazgeç</button>
        <button type="button" className="rr-danger">Reddet</button>
        <button type="button" className="rr-primary">Onayla</button>
      </StickyActionBar></Harness></MemoryRouter>)
      expect(host.querySelector('[data-dock-clear]')?.hasAttribute('data-dock-other')).toBe(false)
      expect(host.querySelector('.rr-primary')?.hasAttribute('data-dock-other')).toBe(false)
      expect(host.querySelector('.rr-danger')?.hasAttribute('data-dock-other')).toBe(true)
    } finally { restore() }
  })

  it('shows the warning strip only while there is a warning; the strip takes no height otherwise', () => {
    const bar = (warning: boolean) => <MemoryRouter initialEntries={['/products/new']}><Harness><StickyActionBar label="Ürün kaydetme" info={{ start: warning ? <span className="rv-dock-chip is-warn">1 Zorunlu Alan Eksik</span> : undefined, end: 'özet' }}><button type="button" data-dock-primary>Kaydet</button></StickyActionBar></Harness></MemoryRouter>
    const { host, rerender } = render(bar(true))
    expect(host.querySelector('.rv-dock-layer')?.getAttribute('data-rows')).toBe('2')
    rerender(bar(false))
    expect(host.querySelector('.rv-dock-layer')?.getAttribute('data-rows')).toBe('1')
  })

  it('has no Geri in the task bar; the top bar has the way back, to the address of the list when the page was opened directly', () => {
    const { host } = render(<MemoryRouter initialEntries={['/products/p1']}><Harness><Registers to="/products" /><StickyActionBar label="Ürün kaydetme"><button type="button" data-dock-primary>Kaydet</button></StickyActionBar></Harness></MemoryRouter>)
    expect(host.querySelector('.rv-taskbar-back, .rv-taskbar-menu')).toBeNull()
    click(host.querySelector('#topback'))
    expect(pathNow).toBe('/products')
  })

  it('goes one step back in the history when the page was reached from the list (its filter comes back), not to the bare address', () => {
    const Opener = () => { const navigate = useNavigate(); return <button type="button" id="open" onClick={() => navigate('/products/p1')}>aç</button> }
    const { host } = render(<MemoryRouter initialEntries={['/orders', '/products?page=3']} initialIndex={1}><Harness><Opener /><Registers to="/products" /></Harness></MemoryRouter>)
    click(host.querySelector('#open'))
    expect(pathNow).toBe('/products/p1')
    click(host.querySelector('#topback'))
    expect(pathNow).toBe('/products')
    expect(searchNow).toBe('?page=3')
  })

  it('puts the DraftSaveBar strip of a page in the task bar: Vazgeç and Kaydet in the bar, the state in the strip', () => {
    const { host } = render(<MemoryRouter initialEntries={['/products']}><Harness><DraftSaveBar count={3} summary="özet" onSave={() => undefined} onDiscard={() => undefined} /></Harness></MemoryRouter>)
    expect(host.querySelector('.rv-dock-layer')?.getAttribute('data-mode')).toBe('task')
    expect([...host.querySelectorAll('.rv-taskbar-actions button')].map(button => button.textContent)).toEqual(['Vazgeç', 'Kaydet'])
    expect(host.querySelector('.rv-dock-layer')?.getAttribute('data-rows')).toBe('1')
    expect(host.querySelector('.rv-draftbar')).toBeNull()
  })

  it('keeps a bar in the page where there is no dock (wider screens), and a bar that holds text boxes inline', () => {
    const wide = render(<MemoryRouter><Harness phone={false}><StickyActionBar label="Toplu işlemler" lead={<span>2 seçildi</span>}><button type="button">Sil</button></StickyActionBar></Harness></MemoryRouter>)
    expect(wide.host.querySelector('.rv-action-bar')?.textContent).toBe('2 seçildiSil')
    cleanup()
    const inline = render(<MemoryRouter><Harness><StickyActionBar label="Silinecekler" dock={false}><input aria-label="onay" /></StickyActionBar></Harness></MemoryRouter>)
    expect(inline.host.querySelector('.rv-action-bar[data-dock="inline"] input')).not.toBeNull()
    expect(inline.host.querySelector('.rv-dock-layer')?.getAttribute('data-mode')).toBe('dock')
  })
})

// The shell reads the dock's state from <html> (shared/root-mark.ts): a :has() on the shell, html or body restyled the whole page on every row selection.
describe('dock state on <html>', () => {
  const mode = () => document.documentElement.getAttribute('data-rv-dock-mode')
  const rows = () => document.documentElement.getAttribute('data-rv-dock-rows')
  it('marks task mode and the warning row while a sticky bar is open, drops both while the menu is open and when the bar closes or the dock goes', () => {
    const bar = (open: boolean) => <MemoryRouter initialEntries={['/products/new']}><Harness><StickyActionBar open={open} label="Ürün kaydetme" info={{ start: <button type="button" className="rv-dock-chip">8 Zorunlu Alan Eksik</button> }}><button type="button" data-dock-primary>Ürünü Oluştur</button></StickyActionBar></Harness></MemoryRouter>
    const { host, rerender, unmount } = render(bar(false))
    expect(mode()).toBeNull()
    expect(rows()).toBeNull()
    rerender(bar(true))
    expect(mode()).toBe('task')
    expect(rows()).toBe('2')
    // The menu takes the dock back: no task mode, no warning row (BottomDock: task = taskActive && !menuOpen).
    click(host.querySelector('.rv-dock button.rv-dock-slot'))
    expect(host.querySelector('.rv-fullmenu')).toBeTruthy()
    expect(mode()).toBeNull()
    expect(rows()).toBeNull()
    click(host.querySelector('.rv-dock button.rv-dock-slot'))
    expect(mode()).toBe('task')
    rerender(bar(false))
    expect(mode()).toBeNull()
    expect(rows()).toBeNull()
    rerender(bar(true))
    unmount()
    expect(mode()).toBeNull()
    expect(rows()).toBeNull()
  })

  it('marks task mode without the warning row for a selection bar, and clears it with the selection', () => {
    const bar = (open: boolean) => <MemoryRouter initialEntries={['/orders']}><Harness><StickyActionBar open={open} label="Toplu işlemler" lead={<span>2 sipariş seçildi</span>}><button type="button">Sil</button></StickyActionBar></Harness></MemoryRouter>
    const { rerender } = render(bar(true))
    expect(mode()).toBe('task')
    expect(rows()).toBeNull()
    rerender(bar(false))
    expect(mode()).toBeNull()
  })

  it('marks the open keyboard (a focused text box) and clears it when the focus leaves', () => {
    const { host } = render(<MemoryRouter initialEntries={['/orders']}><Harness><input aria-label="Ara" /><input type="checkbox" aria-label="Seç" /></Harness></MemoryRouter>)
    act(() => host.querySelector<HTMLInputElement>('input[aria-label="Ara"]')!.focus())
    expect(document.documentElement.hasAttribute('data-rv-typing')).toBe(true)
    act(() => host.querySelector<HTMLInputElement>('input[aria-label="Seç"]')!.focus())
    expect(document.documentElement.hasAttribute('data-rv-typing')).toBe(false)
  })

  it('keeps the layer mark while any of two overlapping layers is open, whichever closes first', () => {
    render(<MemoryRouter initialEntries={['/orders']}><Harness /></MemoryRouter>)
    let first = () => undefined as void
    let second = () => undefined as void
    act(() => { first = lockPageScroll(true) })
    act(() => { second = lockPageScroll(false) })
    act(() => first())
    expect(document.documentElement.hasAttribute('data-rv-layer')).toBe(true)
    act(() => second())
    expect(document.documentElement.hasAttribute('data-rv-layer')).toBe(false)
  })
})
