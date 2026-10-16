import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const apiState = vi.hoisted(() => ({ calls: [] as Array<{ path: string; method: string; body?: string }> }))

vi.mock('../../shared/api', () => {
  const respond = async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    const body = typeof init?.body === 'string' ? init.body : undefined
    apiState.calls.push({ path, method, body })
    if (path === '/security-status') return { totpState: 'DISABLED', recoveryCodesRemaining: 0 }
    if (path === '/sessions') return [
      { id: 's1', state: 'ACTIVE', current: true, issuedAt: '2026-10-16T08:00:00Z', lastSeenAt: '2026-10-16T18:42:00Z', expiresAt: '2026-10-23T18:42:00Z' },
      { id: 's2', state: 'ACTIVE', current: false, issuedAt: '2026-10-15T08:00:00Z', lastSeenAt: '2026-10-16T17:05:00Z', expiresAt: '2026-10-23T09:12:00Z' },
      { id: 's3', state: 'REVOKED', current: false, issuedAt: '2026-10-10T08:00:00Z', lastSeenAt: '2026-10-12T09:31:00Z', expiresAt: '2026-10-19T09:31:00Z' }
    ]
    if (path === '/sessions/revoke-others') return undefined
    if (path === '/settings/shipping-label') return { settings: method === 'PUT' ? JSON.parse(body ?? 'null') : null }
    if (path === '/settings/appearance') return { settings: null }
    if (path === '/settings/data-reset') return { products: 3, orders: 0, returns: 0, invoices: 0, categories: 0, categoryAttributes: 0, brands: 0, options: 0 }
    throw new Error(`Test API fixture does not handle ${method} ${path}`)
  }
  return { api: vi.fn(respond), hubApi: vi.fn(respond) }
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
    const products = Array.from(container.querySelectorAll<HTMLLabelElement>('.ss-sc')).find(item => item.textContent?.includes('Ürünler Listesi'))!
    click(products.querySelector('input')!)
    click(button('Seçili Listeleri Kalıcı Sil'))
    const confirm = Array.from(document.querySelectorAll<HTMLButtonElement>('.ss-modal footer button')).find(item => item.textContent?.includes('Kalıcı Sil'))!
    expect(confirm.disabled).toBe(true)
    input(document.querySelector<HTMLInputElement>('.ss-typed input')!, 'Verileri sil')
    expect(confirm.disabled).toBe(false)
    click(confirm)
    await settle()
    const request = apiState.calls.find(call => call.path === '/settings/data-reset')
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
    expect(apiState.calls.some(call => call.path === '/settings/shipping-label' && call.method === 'PUT')).toBe(true)
  })
})
