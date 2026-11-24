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
    { to: '/questions', icon: 'questions', label: 'Ürün Soruları', count: 7, showZero: true, noun: 'cevaplanmamış' },
  ] },
  { label: 'Sistem', entries: [{ to: '/settings', icon: 'settings', label: 'Sistem Ayarları', end: true }] },
]

let pathNow = ''
function Harness({ phone = true, count = 0, slotCounts = {}, children }: { phone?: boolean; count?: number; slotCounts?: Record<string, number>; children?: ReactNode }) {
  const dock = useDockHostState(phone)
  const menu = useMenuHistory()
  const navigate = useNavigate()
  const location = useLocation()
  const slotRef = useRef<HTMLButtonElement>(null)
  pathNow = location.pathname
  return <DockContext.Provider value={dock.host}>
    <button type="button" id="back" onClick={() => navigate(-1)}>geri</button>
    {menu.open && <FullScreenMenu groups={groups} storeName="Mağaza" onLogout={() => undefined} onClose={menu.closeMenu} onNavigate={menu.leaveTo} pathname={location.pathname} />}
    <BottomDock menuOpen={menu.open} menuCount={count} slotCounts={slotCounts} taskActive={dock.taskActive} hasInfo={dock.hasInfo} setActionsEl={dock.setActionsEl} setStripEl={dock.setStripEl} menuSlotRef={slotRef} onToggleMenu={menu.open ? menu.closeMenu : menu.openMenu} onNavigate={menu.leaveTo} />
    {children}
  </DockContext.Provider>
}
const mount = (path: string, props: Parameters<typeof Harness>[0] = {}) => render(<MemoryRouter initialEntries={[path]}><Harness {...props} /></MemoryRouter>)
const click = (element: Element | null) => act(() => { (element as HTMLElement).click() })

describe('phone bottom navigation (CSS)', () => {
  it('is drawn only up to 768 px, where the hamburger and the top bar search go away and the page keeps room under its content', () => {
    expect(css).toMatch(/^\.rv-dock-layer \{ display: none; \}/m)
    const phone = css.slice(css.indexOf('@media (max-width: 768px)'))
    expect(phone).toContain('> main { padding-bottom: var(--rv-bottom-nav-space); transition: padding-bottom 180ms ease; }')
    // The page's own top bar stays on a phone; only its hamburger goes (Menü is in the dock).
    expect(phone).toContain('.rv-topbar .rv-mobile-menu-toggle { display: none; }')
    expect(phone).not.toContain('rv-phonebar')
    // A bar on the bottom edge: full width, 56 px, a line on top only - no box, radius, shadow or blur.
    const layer = phone.match(/\.rv-dock-layer \{[^}]*\}/)![0]
    expect(layer).toMatch(/right: 0; bottom: 0; left: 0;/)
    expect(layer).toContain('border-top: 1px solid var(--rv-color-border)')
    expect(layer).not.toMatch(/radius|shadow|blur/)
    expect(phone).not.toContain('.rv-dock-layer::after')
    expect(phone).toMatch(/\.rv-dock-bottom \{[^}]*height: 56px/)
    expect(phone).toMatch(/\.rv-dock-layer\[data-rows="2"\] \.rv-dock-top \{ height: 29px; \}/)
    // One sliding line (24 x 3 px, 200 ms), the active column in the accent colour with no fill.
    expect(phone).toMatch(/\.rv-dock-indicator \{[^}]*width: 20%; height: 3px;[^}]*transform 200ms ease-out/)
    expect(phone).toContain('width: 24px; height: 3px')
    expect(phone).toMatch(/\.rv-dock-slot\.is-active \{ color: var\(--rv-color-primary-text\); font-weight: 600; \}/)
    expect(phone).toContain('.rv-dock-slot:active .rv-dock-icon { transform: scale(.94); }')
    expect(css).not.toMatch(/touch-action/)
    expect(phone).toContain('@media (prefers-reduced-motion: reduce)')
    // The notification panel opens from the top edge, 75% of the screen at most, with no bottom anchor.
    expect(phone).toContain(".rv-notification-history { top: calc(62px + env(safe-area-inset-top, 0px) + 8px); right: 12px; bottom: auto; left: 12px;")
    expect(phone).toContain("max-height: min(75dvh")
    expect(phone).toContain('translateY(-28px)')
    // Page buttons that are not the primary, the step or the selection chip wait in the Diğer list.
    expect(phone).toContain('.rv-taskbar-group [data-dock-other] { display: none; }')
  })
})

describe('Ravencia Dock', () => {
  it('has Ana Sayfa, Siparişler, Ara, Ürünler and Menü; the open page is the active slot and shows its label', () => {
    const { host } = mount('/orders')
    const dock = host.querySelector('.rv-dock')!
    expect([...dock.querySelectorAll('.rv-dock-slot')].map(slot => slot.getAttribute('aria-label'))).toEqual(['Ana Sayfa', 'Siparişler', 'Ürünler', 'Ürün Soruları', 'Menü'])
    expect(dock.querySelector('.rv-dock-search')).toBeNull()
    expect(host.querySelector('.rv-dock-layer')?.getAttribute('data-rows')).toBe('1')
    const current = dock.querySelectorAll('[aria-current="page"]')
    expect(current).toHaveLength(1)
    expect(current[0].getAttribute('aria-label')).toBe('Siparişler')
    expect(current[0].textContent).toBe('Siparişler')
    // Every target shows its label, active or not.
    expect([...dock.querySelectorAll('.rv-dock-label')].map(label => label.textContent)).toEqual(['Ana Sayfa', 'Siparişler', 'Ürünler', 'Sorular', 'Menü'])
  })

  it('has one indicator line that sits on the open column and slides to Menü while the menu is open', () => {
    const { host } = mount('/orders')
    const indicator = host.querySelector<HTMLElement>('.rv-dock-indicator')!
    expect(host.querySelectorAll('.rv-dock-indicator')).toHaveLength(1)
    expect(indicator.style.transform).toBe('translateX(100%)')
    expect(host.querySelector('.rv-dock-slot.is-active')?.getAttribute('aria-label')).toBe('Siparişler')
    click(host.querySelector('button[aria-label^="Menü"]'))
    expect(indicator.style.transform).toBe('translateX(400%)')
    expect(host.querySelector('.rv-dock-slot.is-active')?.textContent).toBe('Kapat')
    cleanup()
    expect(mount('/reports').host.querySelector<HTMLElement>('.rv-dock-indicator')?.style.opacity).toBe('0')
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
    cleanup()
    const counted = mount('/dashboard', { slotCounts: { '/orders': 9, '/questions': 120 } }).host
    expect(counted.querySelector('a[href="/orders"] .rv-dock-badge')?.textContent).toBe('9')
    expect(counted.querySelector('a[href="/questions"] .rv-dock-badge')?.textContent).toBe('99+')
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

  it('is the title, the groups and, at the end of the Sistem group, the user and Çıkış as a danger row; no search field, no footer', () => {
    const { host } = mount('/orders')
    click(host.querySelector('button[aria-label^="Menü"]'))
    expect(document.querySelector('.rv-fullmenu-head')?.textContent).toBe('Menü')
    expect(document.querySelector('.rv-fullmenu-search, .rv-fullmenu-foot, .rv-fullmenu-tools, input')).toBeNull()
    const system = document.querySelector('.rv-fullmenu-group[aria-label="Sistem"]')!
    expect(system.querySelector('h3 .rv-fullmenu-store')?.textContent).toBe('Mağaza')
    const rows = [...system.querySelectorAll('.rv-fullmenu-row')]
    expect(rows.map(row => row.textContent)).toEqual(['Sistem Ayarları', 'Çıkış'])
    expect(rows[1].classList.contains('is-danger')).toBe(true)
    expect(rows[1].querySelector('.rv-fullmenu-chev')).toBeNull()
    // Only pages with something pending show a count (0 shows none).
    expect([...document.querySelectorAll('.rv-fullmenu-count')].map(count => count.textContent)).toEqual(['2', '3', '7'])
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

  it('shows only labelled controls: primary and step in the bar, the other actions in the Diğer list that runs them', () => {
    const calls: string[] = []
    const { host } = render(<MemoryRouter initialEntries={['/products/p1']}><Harness><StickyActionBar label="Ürün kaydetme" info={{ end: '33 Satış Satırı · 0 Kanal' }}>
      <button type="button" data-dock-primary onClick={() => calls.push('kaydet')}>Kaydet</button>
      <button type="button" data-dock-step aria-label="Yayınlamaya Devam Et" onClick={() => calls.push('ileri')}><span>Yayınla</span><svg /></button>
      <button type="button" aria-label="Hızlı Güncelle" onClick={() => calls.push('hizli')}><span>Hızlı Güncelle</span><svg viewBox="0 0 4 4" /></button>
      <button type="button" className="rr-danger" aria-label="Platformda Sil" onClick={() => calls.push('sil')}>x</button>
    </StickyActionBar></Harness></MemoryRouter>)
    const group = host.querySelector('.rv-taskbar-group')!
    const tagged = [...group.querySelectorAll('button')].map(button => [button.textContent, button.hasAttribute('data-dock-other')])
    expect(tagged).toEqual([['Kaydet', false], ['Yayınla', false], ['Hızlı Güncelle', true], ['x', true]])
    // The strip has no warning: one row, nothing empty above the bar.
    expect(host.querySelector('.rv-dock-layer')?.getAttribute('data-rows')).toBe('1')
    expect(host.querySelector('.rv-dock-top')?.getAttribute('data-top')).toBe('none')
    expect(host.querySelector('.rv-taskbar-more-list')).toBeNull()
    click(host.querySelector('.rv-taskbar-more-button'))
    const list = host.querySelector('.rv-taskbar-more-list')!
    expect(list.querySelector('.rv-taskbar-more-summary')?.textContent).toBe('33 Satış Satırı · 0 Kanal')
    const items = [...list.querySelectorAll<HTMLButtonElement>('.rv-taskbar-more-item')]
    expect(items.map(item => item.textContent)).toEqual(['Hızlı Güncelle', 'Platformda Sil'])
    expect(items[1].classList.contains('is-danger')).toBe(true)
    click(items[0])
    expect(calls).toEqual(['hizli'])
    expect(host.querySelector('.rv-taskbar-more-list')).toBeNull()
    // A press on the scrim only closes the list.
    click(host.querySelector('.rv-taskbar-more-button'))
    click(host.querySelector('.rv-taskbar-more-scrim'))
    expect(host.querySelector('.rv-taskbar-more-list')).toBeNull()
    expect(calls).toEqual(['hizli'])
    // Esc closes it too.
    click(host.querySelector('.rv-taskbar-more-button'))
    act(() => { document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })) })
    expect(host.querySelector('.rv-taskbar-more-list')).toBeNull()
  })

  it('keeps one other action with a short name in the bar (no Diğer), and the selection chip is never an other action', () => {
    const { host } = render(<MemoryRouter initialEntries={['/returns']}><Harness><StickyActionBar label="Toplu işlemler">
      <button type="button" data-dock-clear data-dock-count="3" aria-label="Seçimi Temizle">Vazgeç</button>
      <button type="button" className="rr-danger">Reddet</button>
      <button type="button" className="rr-primary">Onayla</button>
    </StickyActionBar></Harness></MemoryRouter>)
    expect(host.querySelector('.rv-taskbar-more')).toBeNull()
    expect(host.querySelector('[data-dock-other]')).toBeNull()
    expect(host.querySelector('button[data-dock-keep]')?.textContent).toBe('Reddet')
    expect(host.querySelector('[data-dock-clear]')?.getAttribute('data-dock-count')).toBe('3')
  })

  it('shows the warning strip only while there is a warning; the strip takes no height otherwise', () => {
    const bar = (warning: boolean) => <MemoryRouter initialEntries={['/products/new']}><Harness><StickyActionBar label="Ürün kaydetme" info={{ start: warning ? <span className="rv-dock-chip is-warn">1 Zorunlu Alan Eksik</span> : undefined, end: 'özet' }}><button type="button" data-dock-primary>Kaydet</button></StickyActionBar></Harness></MemoryRouter>
    const { host, rerender } = render(bar(true))
    expect(host.querySelector('.rv-dock-layer')?.getAttribute('data-rows')).toBe('2')
    rerender(bar(false))
    expect(host.querySelector('.rv-dock-layer')?.getAttribute('data-rows')).toBe('1')
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
