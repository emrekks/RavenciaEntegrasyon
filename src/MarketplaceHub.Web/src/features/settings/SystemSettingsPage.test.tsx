import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const apiState = vi.hoisted(() => ({ calls: [] as Array<{ path: string; method: string; body?: string; headers?: Record<string, string> }>, failShippingSave: null as string | null }))

vi.mock('../../shared/api', async importOriginal => {
  const respond = async (requestedPath: string, init?: RequestInit) => {
    // Security calls go through hubApi under /auth; the fixture answers them by their auth path.
    const path = requestedPath.replace(/^\/auth(?=\/)/, '')
    const method = init?.method ?? 'GET'
    const body = typeof init?.body === 'string' ? init.body : undefined
    apiState.calls.push({ path, method, body, headers: Object.fromEntries(new Headers(init?.headers).entries()) })
    if (path === '/security-status') return { totpState: 'DISABLED', recoveryCodesRemaining: 0 }
    if (path === '/sessions') return [
      { id: 's1', state: 'ACTIVE', current: true, issuedAt: '2026-10-16T08:00:00Z', lastSeenAt: '2026-10-16T18:42:00Z', expiresAt: '2026-10-23T18:42:00Z' },
      { id: 's2', state: 'ACTIVE', current: false, issuedAt: '2026-10-15T08:00:00Z', lastSeenAt: '2026-10-16T17:05:00Z', expiresAt: '2026-10-23T09:12:00Z' },
      { id: 's3', state: 'REVOKED', current: false, issuedAt: '2026-10-10T08:00:00Z', lastSeenAt: '2026-10-12T09:31:00Z', expiresAt: '2026-10-19T09:31:00Z' }
    ]
    if (path === '/sessions/revoke-others') return undefined
    if (path === '/settings/shipping-label' && method === 'PUT' && apiState.failShippingSave) throw new Error(apiState.failShippingSave)
    if (path === '/mfa/setup') return { otpauthUri: 'otpauth://totp/MarketplaceHub:a?secret=JBSWY3DPEHPK3PXPJBSW&issuer=MarketplaceHub', qrSvg: '<svg xmlns="http://www.w3.org/2000/svg"/>', expiresAt: '2026-10-20T10:00:00Z' }
    if (path === '/mfa/confirm') return { recoveryCodes: ['ABCD-EFGH-JKMN', 'PQRS-TVWX-YZ01'] }
    if (path === '/settings/shipping-label') return { settings: method === 'PUT' ? JSON.parse(body ?? 'null') : null, version: method === 'PUT' ? 4 : 3 }
    if (path === '/settings/appearance') return { settings: null, version: 0 }
    if (path === '/settings/data-reset' && method === 'GET') return { items: [{ scope: 'PRODUCTS', count: 944 }, { scope: 'CATEGORIES', count: 12 }, { scope: 'CATEGORY_ATTRIBUTES', count: 40 }, { scope: 'BRANDS', count: 3 }, { scope: 'OPTIONS', count: 2 }] }
    if (path === '/settings/data-reset') return { products: 3, orders: 0, returns: 0, invoices: 0, categories: 0, categoryAttributes: 0, brands: 0, options: 0 }
    throw new Error(`Test API fixture does not handle ${method} ${path}`)
  }
  return { ...(await importOriginal<typeof import('../../shared/api')>()), api: vi.fn(respond), hubApi: vi.fn(respond) }
})

import { SystemSettingsPage } from './SystemSettingsPage'

let container: HTMLDivElement
let root: Root

function button(label: string) {
  const found = Array.from(document.querySelectorAll<HTMLButtonElement>('button')).find(item => item.textContent?.trim().startsWith(label))
  if (!found) throw new Error(`Could not find button starting with “${label}”`)
  return found
}

function click(element: HTMLElement) {
  act(() => { element.click() })
}

function input(element: HTMLInputElement, value: string) {
  act(() => {
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set?.call(element, value)
    element.dispatchEvent(new Event('input', { bubbles: true }))
  })
}

async function settle() {
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 15)) })
}

async function renderPage(path = '/settings') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  root = createRoot(container)
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[path]}><SystemSettingsPage /></MemoryRouter></QueryClientProvider>) })
  await settle()
}

const paneTitle = () => container.querySelector('.ss-head h2')?.textContent

describe('SystemSettingsPage', () => {
  beforeEach(() => {
    apiState.calls = []
    apiState.failShippingSave = null
    localStorage.clear()
    container = document.createElement('div')
    document.body.appendChild(container)
  })
  afterEach(() => {
    act(() => root.unmount())
    container.remove()
  })

  it('opens on the Authenticator section with the group tabs on top and the sections of the group below', async () => {
    await renderPage()
    expect(paneTitle()).toBe('Authenticator')
    const groups = Array.from(container.querySelectorAll('.ss-gtabs > button')).map(item => item.textContent)
    expect(groups).toEqual(['Güvenlik ve Oturumlar', 'Veri', 'Kargo Ayarları', 'Görünüm'])
    expect(container.querySelector('.ss-gtabs > button.is-on')?.textContent).toBe('Güvenlik ve Oturumlar')
    expect(Array.from(container.querySelectorAll('.ss-seg > button')).map(item => item.textContent)).toEqual(['Authenticator', 'Oturumlar3'])
    expect(button('Authenticator’ı Etkinleştir')).toBeTruthy()
  })

  it('sets up the authenticator with the password in the setup call, shows the base32 key and keeps the codes open on Escape (W31)', async () => {
    await renderPage()
    click(button('Authenticator’ı Etkinleştir'))
    input(document.querySelector<HTMLInputElement>('#ss-mfa-password input[name="password"]')!, 'gizli-parola')
    act(() => { document.querySelector<HTMLFormElement>('#ss-mfa-password')!.requestSubmit() })
    await settle()
    const setup = apiState.calls.find(call => call.path === '/mfa/setup')
    expect(JSON.parse(setup?.body ?? '{}')).toEqual({ password: 'gizli-parola' })
    expect(apiState.calls.some(call => call.path === '/reauthenticate')).toBe(false)
    expect(document.querySelector('.ss-man code')?.textContent).toBe('JBSW Y3DP EHPK 3PXP JBSW')

    const digits = Array.from(document.querySelectorAll<HTMLInputElement>('.ss-otp input'))
    digits.forEach((digit, index) => input(digit, String(index + 1)))
    act(() => { document.querySelector<HTMLFormElement>('#ss-mfa-verify')!.requestSubmit() })
    await settle()
    expect(document.querySelector('.ss-codes')?.textContent).toContain('ABCD-EFGH-JKMN')

    act(() => { document.querySelector('.ss-mfa')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })) })
    await settle()
    expect(document.querySelector('.ss-codes')).not.toBeNull()
    click(button('Kodları Sakladım'))
    expect(document.querySelector('.ss-codes')).toBeNull()
  })

  it('opens the first section of a former tab from an old link', async () => {
    await renderPage('/settings?tab=shipping')
    expect(paneTitle()).toBe('Gönderici ve Yazdırma')
  })

  it('closes the other sessions after confirming in the popup', async () => {
    await renderPage('/settings?tab=sessions')
    expect(container.textContent).toContain('Bu Cihaz')
    click(button('Diğer Tüm Oturumları Kapat'))
    expect(document.querySelector('.ss-modal h2')?.textContent).toBe('Diğer Oturumları Kapat')
    click(button('Oturumları Kapat'))
    await settle()
    expect(apiState.calls.some(call => call.path === '/sessions/revoke-others' && call.method === 'POST')).toBe(true)
  })

  it('deletes the chosen lists only after the confirmation text is typed', async () => {
    await renderPage('/settings?tab=database')
    await settle()
    const products = Array.from(container.querySelectorAll<HTMLLabelElement>('.ss-dr-opt')).find(item => item.textContent?.includes('Ürünler Listesi'))!
    expect(products.textContent).toContain('944')
    click(products.querySelector('input')!)
    const summary = container.querySelector('.ss-dr-sum')?.textContent
    expect(summary).toContain('Toplam 944 kayıt')
    expect(summary).toContain('Ürünler Listesi944')
    const confirm = container.querySelector<HTMLButtonElement>('.ss-dr-go')!
    expect(confirm.disabled).toBe(true)
    input(container.querySelector<HTMLInputElement>('#settings-reset-confirm')!, 'Verileri sil')
    expect(confirm.disabled).toBe(false)
    click(confirm)
    await settle()
    const request = apiState.calls.find(call => call.path === '/settings/data-reset' && call.method === 'POST')
    expect(JSON.parse(request?.body ?? '{}')).toEqual({ scopes: ['PRODUCTS'], confirmation: 'Verileri sil' })
  })

  it('enables saving the label settings once a switch changes', async () => {
    await renderPage('/settings?tab=sender')
    const save = button('Ayarları Kaydet')
    expect(save.disabled).toBe(true)
    const sticker = container.querySelector<HTMLInputElement>('input[aria-label="Sticker Yazdırma Butonunu Göster"]')!
    click(sticker)
    expect(container.querySelector('.ss-bar-copy .is-dirty')?.textContent).toBe('Kaydedilmemiş değişiklikler var')
    click(button('Ayarları Kaydet'))
    await settle()
    const saved = apiState.calls.find(call => call.path === '/settings/shipping-label' && call.method === 'PUT')
    // the save names the version it was made on, so a change made meanwhile elsewhere is refused (412)
    expect(saved?.headers?.['if-match']).toBe('"v3"')
  })

  it('shows a failed save as an error even when the server text does not sound like one (W9)', async () => {
    // Wording the old guesser read as success ("kaydedildi") or did not recognise at all.
    apiState.failShippingSave = 'Ayarlar başka bir oturumda kaydedildi; sayfayı yenileyin.'
    await renderPage('/settings?tab=sender')
    click(container.querySelector<HTMLInputElement>('input[aria-label="Sticker Yazdırma Butonunu Göster"]')!)
    click(button('Ayarları Kaydet'))
    await settle()
    const toast = container.querySelector('.app-toast')!
    expect(toast.classList.contains('app-toast-error')).toBe(true)
    expect(toast.getAttribute('role')).toBe('alert')
    expect(toast.textContent).toContain('başka bir oturumda')
  })

  it('shows a successful save as a success', async () => {
    await renderPage('/settings?tab=sender')
    click(container.querySelector<HTMLInputElement>('input[aria-label="Sticker Yazdırma Butonunu Göster"]')!)
    click(button('Ayarları Kaydet'))
    await settle()
    expect(container.querySelector('.app-toast')!.classList.contains('app-toast-success')).toBe(true)
  })

  it('label designer preview shows the address block like the print: title, recipient name, then the address', async () => {
    await renderPage('/settings?tab=designer')
    const address = container.querySelector('.preview-content-address')!
    const texts = Array.from(address.children).map(child => child.textContent)
    expect(texts).toEqual(['Teslimat adresi', 'Alıcı adıFEHİME MAT', 'KARAKÖPRÜ / ŞANLIURFA'])
  })

  it('label designer: Tab selects a block, the arrow keys move it 1% (Shift 5%), the drag handles stay out of the Tab order', async () => {
    await renderPage('/settings?tab=designer')
    const blocks = () => Array.from(container.querySelectorAll<HTMLElement>('.shipping-designer-block'))
    expect(blocks().length).toBeGreaterThan(1)
    expect(blocks().every(block => block.tabIndex === 0 && block.getAttribute('aria-roledescription') === 'etiket bloğu')).toBe(true)

    act(() => blocks()[1].focus())
    expect(blocks()[1].classList.contains('is-selected')).toBe(true)
    expect(blocks()[0].classList.contains('is-selected')).toBe(false)
    expect(Array.from(blocks()[1].querySelectorAll<HTMLButtonElement>('.shipping-designer-resize-handle')).every(handle => handle.tabIndex === -1)).toBe(true)

    const at = () => ({ left: parseFloat(blocks()[1].style.left), top: parseFloat(blocks()[1].style.top) })
    const start = at()
    const press = (key: string, shiftKey = false) => act(() => { blocks()[1].dispatchEvent(new KeyboardEvent('keydown', { key, shiftKey, bubbles: true, cancelable: true })) })
    press('ArrowRight')
    expect(at().left).toBeCloseTo(start.left + 1)
    press('ArrowDown', true)
    expect(at().top).toBeCloseTo(start.top + 5)
    press('ArrowLeft')
    press('ArrowUp', true)
    expect(at()).toEqual(start)
    // the block's name tells where it is now; focus stayed on it
    expect(blocks()[1].getAttribute('aria-label')).toContain(`solda %${start.left}`)
    expect(document.activeElement).toBe(blocks()[1])

    // never off the paper
    for (let step = 0; step < 30; step += 1) press('ArrowLeft', true)
    expect(at().left).toBe(0)
  })
})
