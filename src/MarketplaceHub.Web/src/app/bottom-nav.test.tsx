import { act, useRef, type ReactNode } from 'react'
import { MemoryRouter, useLocation, useNavigate } from 'react-router'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render } from '../test/render'
import { StickyActionBar } from '../shared/components/StickyActionBar'
import { Tabs } from '../shared/components/DesignSystem'
import { DraftSaveBar } from '../shared/draft-edits'
import { DockContext } from '../shared/components/dock-actions'
import { BottomDock, FullScreenMenu, menuPendingTotal, useDockHostState, useMenuHistory, type MenuGroup } from './BottomDock'

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
  ] },
  { label: 'Sistem', entries: [{ to: '/settings', icon: 'settings', label: 'Sistem Ayarları', end: true }] },
]

let pathNow = ''
function Harness({ phone = true, count = 0, unread = 0, slotCounts = {}, children }: { phone?: boolean; count?: number; unread?: number; slotCounts?: Record<string, number>; children?: ReactNode }) {
  const dock = useDockHostState(phone)
  const menu = useMenuHistory()
  const navigate = useNavigate()
  const location = useLocation()
  const slotRef = useRef<HTMLButtonElement>(null)
  pathNow = location.pathname
  return <DockContext.Provider value={dock.host}>
    <button type="button" id="back" onClick={() => navigate(-1)}>geri</button>
    {menu.open && <FullScreenMenu unread={unread} unreadError={false} onOpenNotifications={() => undefined} onSearch={() => undefined} groups={groups} storeName="Mağaza" theme="light" onToggleTheme={() => undefined} onLogout={() => undefined} onClose={menu.closeMenu} onNavigate={menu.leaveTo} pathname={location.pathname} />}
    <BottomDock unread={unread} unreadError={false} onOpenNotifications={() => undefined} menuOpen={menu.open} menuCount={count} slotCounts={slotCounts} taskActive={dock.taskActive} hasInfo={dock.hasInfo} setActionsEl={dock.setActionsEl} setStripEl={dock.setStripEl} menuSlotRef={slotRef} onToggleMenu={menu.open ? menu.closeMenu : menu.openMenu} onNavigate={menu.leaveTo} />
    {children}
  </DockContext.Provider>
}
const mount = (path: string, props: Parameters<typeof Harness>[0] = {}) => render(<MemoryRouter initialEntries={[path]}><Harness {...props} /></MemoryRouter>)
const click = (element: Element | null) => act(() => { (element as HTMLElement).click() })
const typeInto = (input: HTMLInputElement, value: string) => act(() => {
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(input, value)
  input.dispatchEvent(new Event('input', { bubbles: true }))
})

describe('phone bottom navigation (CSS)', () => {
  it('is drawn only up to 768 px, where the hamburger and the top bar search go away and the page keeps room under its content', () => {
    expect(css).toMatch(/^\.rv-dock-layer \{ display: none; \}/m)
    const phone = css.slice(css.indexOf('@media (max-width: 768px)'))
    expect(phone).toContain('.rv-mobile-menu-toggle { display: none; }')
    expect(phone).toContain('.rv-topbar-search { display: none; }')
    expect(phone).toContain('> main { padding-top: env(safe-area-inset-top, 0px); padding-bottom: var(--rv-bottom-nav-space); transition: padding-bottom 180ms ease; }')
    expect(phone).toContain('--rv-topbar-height: 0px')
    expect(phone).toMatch(/\.rv-dock-layer \{[^}]*bottom: var\(--rv-dock-bottom\)/)
    expect(phone).toMatch(/\.rv-dock-bottom \{[^}]*height: 52px/)
    expect(phone).toMatch(/\.rv-dock-layer\[data-rows="2"\] \.rv-dock-top \{ height: 40px; \}/)
    expect(css).not.toMatch(/touch-action/)
    expect(phone).toContain('@media (prefers-reduced-motion: reduce)')
  })
})

describe('Ravencia Dock', () => {
  it('has Ana Sayfa, Siparişler, Ara, Ürünler and Menü; the open page is the active slot and shows its label', () => {
    const { host } = mount('/orders')
    const dock = host.querySelector('.rv-dock')!
    expect([...dock.querySelectorAll('.rv-dock-slot')].map(slot => slot.getAttribute('aria-label'))).toEqual(['Ana Sayfa', 'Siparişler', 'Ürünler', 'Menü'])
    expect(dock.querySelector('.rv-dock-search')).toBeNull()
    expect(host.querySelector('.rv-dock-layer')?.getAttribute('data-rows')).toBe('1')
    const current = dock.querySelectorAll('[aria-current="page"]')
    expect(current).toHaveLength(1)
    expect(current[0].getAttribute('aria-label')).toBe('Siparişler')
    expect(current[0].textContent).toBe('Siparişler')
    // Every target shows its label, active or not.
    expect([...dock.querySelectorAll('.rv-dock-label')].map(label => label.textContent)).toEqual(['Ana Sayfa', 'Siparişler', 'Ürünler', 'Menü'])
  })

  it('shows the Menü badge from 1 to 99 and as 99+ above, and none at 0', () => {
    expect(mount('/orders', { count: 0 }).host.querySelector('.rv-dock-badge')).toBeNull()
    cleanup()
    expect(mount('/orders', { count: 7 }).host.querySelector('.rv-dock .rv-dock-badge')?.textContent).toBe('7')
    cleanup()
    expect(mount('/orders', { count: 120 }).host.querySelector('.rv-dock .rv-dock-badge')?.textContent).toBe('99+')
    expect(menuPendingTotal(groups)).toBe(5)
    expect(menuPendingTotal(groups, ['/orders'])).toBe(3)
    cleanup()
    expect(mount('/dashboard', { slotCounts: { '/orders': 9 } }).host.querySelector('a[href="/orders"] .rv-dock-badge')?.textContent).toBe('9')
  })

  it('opens the full-screen menu from the Menü slot (which becomes Kapat) and closes it again from the same slot', () => {
    const { host } = mount('/orders')
    expect(document.querySelector('.rv-fullmenu')).toBeNull()
    click(host.querySelector('button[aria-label^="Menü"]'))
    const dialog = document.querySelector('.rv-fullmenu')!
    expect(dialog.getAttribute('role')).toBe('dialog')
    expect(dialog.getAttribute('aria-modal')).toBe('true')
    expect(document.body.style.overflow).toBe('hidden')
    const slot = host.querySelector('.rv-dock button[aria-expanded="true"]')!
    expect(slot.textContent).toBe('Kapat')
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

  it('filters the entries with Sayfa Ara and leaves the menu for a page by replacing its history entry', () => {
    const { host } = mount('/orders')
    click(host.querySelector('button[aria-label^="Menü"]'))
    const input = document.querySelector<HTMLInputElement>('.rv-fullmenu-search input')!
    typeInto(input, 'fat')
    expect([...document.querySelectorAll('.rv-fullmenu-row')].map(row => row.querySelector('.rv-fullmenu-name')?.textContent)).toEqual(['Faturalar'])
    expect(document.querySelector('.rv-fullmenu-row .rv-fullmenu-count')?.textContent).toBe('3')
    // The user and Çıkış are the last item of the scrolling list, not a bar of their own.
    expect(document.querySelector('.rv-fullmenu-body > .rv-fullmenu-foot .rv-fullmenu-action')?.textContent).toBe('Çıkış')
    expect(document.querySelector('.rv-fullmenu > .rv-fullmenu-foot')).toBeNull()
    typeInto(input, 'iade')
    expect(document.querySelector('.rv-fullmenu-row .rv-fullmenu-count')).toBeNull()
    typeInto(input, 'fat')
    typeInto(input, '')
    click([...document.querySelectorAll('.rv-fullmenu-row')].find(row => row.textContent?.startsWith('Faturalar')) ?? null)
    expect(pathNow).toBe('/invoices')
    expect(document.querySelector('.rv-fullmenu')).toBeNull()
    click(host.querySelector('#back'))
    expect(pathNow).toBe('/orders')
  })
})

describe('notification pill', () => {
  it('is absent at 0 unread, shows 99+ above 99, hides while the menu is open, and moves into the task bar strip as a bell', () => {
    expect(mount('/orders', { unread: 0 }).host.querySelector('.rv-dock-pill-bell')).toBeNull()
    cleanup()
    const { host } = mount('/orders', { unread: 120 })
    expect(host.querySelector('.rv-dock-pill-bell')?.textContent).toBe('99+')
    click(host.querySelector('button[aria-label^="Menü"]'))
    expect(host.querySelector('.rv-dock-pill-bell')).toBeNull()
    expect(document.querySelector('.rv-fullmenu-tool-badge')?.textContent).toBe('99+')
    cleanup()
    const task = render(<MemoryRouter initialEntries={['/orders']}><Harness unread={3}><StickyActionBar label="Toplu işlemler"><button type="button">Sil</button></StickyActionBar></Harness></MemoryRouter>)
    expect(task.host.querySelector('.rv-dock-pill-bell')).toBeNull()
    expect(task.host.querySelector('.rv-dock-info .rv-dock-bell')?.textContent).toBe('3')
    expect(task.host.querySelector('.rv-dock-info')?.getAttribute('data-visible')).toBe('true')
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
    expect(host.querySelector('.rv-dock-info-start')?.textContent).toBe('2 sipariş seçildi')
  })
})

describe('task mode', () => {
  it('swaps the dock for the page actions while a sticky bar is open and brings the dock back when it closes', () => {
    const bar = (open: boolean) => <MemoryRouter initialEntries={['/products/new']}><Harness><StickyActionBar open={open} label="Ürün kaydetme" info={{ start: <button type="button" className="rv-dock-chip">8 Zorunlu Alan Eksik</button>, end: '1 Satış Satırı · 0 Kanal' }} lead={<span>durum</span>}><button type="button" data-dock-primary>Ürünü Oluştur</button></StickyActionBar></Harness></MemoryRouter>
    const { host, rerender } = render(bar(true))
    const layer = host.querySelector('.rv-dock-layer')!
    expect(layer.getAttribute('data-mode')).toBe('task')
    expect(host.querySelector('.rv-taskbar-actions')?.textContent).toBe('Ürünü Oluştur')
    expect(host.querySelector('.rv-dock-info')?.textContent).toBe('8 Zorunlu Alan Eksik1 Satış Satırı · 0 Kanal')
    expect(host.querySelector('.rv-dock-info')?.getAttribute('data-visible')).toBe('true')
    expect(host.querySelector('.rv-dock')?.hasAttribute('inert')).toBe(true)
    expect(host.querySelector('.rv-action-bar')).toBeNull()
    rerender(bar(false))
    expect(layer.getAttribute('data-mode')).toBe('dock')
    expect(host.querySelector('.rv-taskbar-actions')?.textContent).toBe('')
    expect(host.querySelector('.rv-dock-info')?.getAttribute('data-visible')).toBe('false')
  })

  it('opens the menu from the task bar, which shows the dock while the menu is open', () => {
    const { host } = render(<MemoryRouter initialEntries={['/orders']}><Harness><StickyActionBar label="Toplu işlemler"><button type="button">Sil</button></StickyActionBar></Harness></MemoryRouter>)
    click(host.querySelector('.rv-taskbar-menu'))
    expect(host.querySelector('.rv-dock-layer')?.getAttribute('data-mode')).toBe('dock')
    expect(document.querySelector('.rv-fullmenu')).not.toBeNull()
  })

  it('puts the DraftSaveBar strip of a page in the task bar: Vazgeç and Kaydet in the bar, the state in the strip', () => {
    const { host } = render(<MemoryRouter initialEntries={['/products']}><Harness><DraftSaveBar count={3} summary="özet" onSave={() => undefined} onDiscard={() => undefined} /></Harness></MemoryRouter>)
    expect(host.querySelector('.rv-dock-layer')?.getAttribute('data-mode')).toBe('task')
    expect([...host.querySelectorAll('.rv-taskbar-actions button')].map(button => button.textContent)).toEqual(['Vazgeç', 'Kaydet'])
    expect(host.querySelector('.rv-dock-info-start')?.textContent).toBe('3 Değişiklik · Kaydedilmedi')
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
