import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const apiState = vi.hoisted(() => ({ calls: [] as Array<{ path: string; method: string; body?: string }>, version: 4 }))

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const connection = () => ({ id: 'c1', publicId: 'c1', platformCode: 'TRENDYOL', environment: 'STAGE', displayName: 'Trendyol Test', externalStoreId: '9999', status: 'ACTIVE', apiVersion: 'V2', lastTestedAt: null, lastSuccessAt: null, lastErrorCode: null, hasCredential: true, externalWritesEnabled: false, invoiceCreationEnabled: false, version: apiState.version })
  const respond = async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    apiState.calls.push({ path, method, body: typeof init?.body === 'string' ? init.body : undefined })
    if (path === '/connections/c1/credential' && method === 'PUT') { apiState.version += 1; return undefined }
    if (path === '/connections/c1') return connection()
    if (path.endsWith('/sync-policies') || path.endsWith('/capabilities')) return []
    if (path.endsWith('/instant-notifications')) return { supported: false }
    if (path === '/connections/c1/data-reset') return { items: [{ scope: 'ORDERS', count: 3 }], keptIssuedInvoices: 0 }
    if (path === '/connections/c1/full-reset') return { items: [{ scope: 'ORDERS', count: 3 }], keptProducts: 0, job: null }
    return []
  }
  return { ...original, hubApi: vi.fn(respond), loadAllPages: vi.fn(async () => ({ items: [connection()], nextCursor: null, hasMore: false })) }
})

import { IntegrationDetailPage, IntegrationsPage } from './IntegrationPages'

let container: HTMLDivElement
let root: Root
let client: QueryClient

async function settle() {
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 20)) })
}

function setValue(element: HTMLInputElement, value: string) {
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set?.call(element, value)
  element.dispatchEvent(new Event('input', { bubbles: true }))
}

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  apiState.calls = []
  apiState.version = 4
  Element.prototype.scrollIntoView = vi.fn()
  container = document.createElement('div')
  document.body.append(container)
})

afterEach(() => {
  act(() => root?.unmount())
  client?.clear()
  container.remove()
})

async function renderPage() {
  client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  root = createRoot(container)
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/integrations/c1']}><Routes><Route path="/integrations/:id" element={<IntegrationDetailPage />} /></Routes></MemoryRouter></QueryClientProvider>) })
  await settle()
  await settle()
}

function openSection(label: string) {
  const tab = Array.from(container.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent?.includes(label))!
  act(() => { tab.click() })
}

function credentialForm() {
  openSection('Mağaza ve Kimlik')
  return container.querySelector<HTMLInputElement>('input[name="apiKey"]')!.form!
}

describe('IntegrationDetailPage credentials (W10)', () => {
  it('saves with its own pending state, re-reads the card list and the connection and clears the secret fields', async () => {
    await renderPage()
    const invalidate = vi.spyOn(client, 'invalidateQueries')
    const form = credentialForm()
    act(() => {
      setValue(form.querySelector<HTMLInputElement>('input[name="apiKey"]')!, 'new-key')
      setValue(form.querySelector<HTMLInputElement>('input[name="apiSecret"]')!, 'new-secret')
    })
    await act(async () => { form.requestSubmit() })
    await settle()

    const save = apiState.calls.find(call => call.path === '/connections/c1/credential')!
    expect(save.method).toBe('PUT')
    expect(JSON.parse(save.body!)).toEqual({ apiKey: 'new-key', apiSecret: 'new-secret' })
    const keys = invalidate.mock.calls.map(([filters]) => JSON.stringify(filters?.queryKey))
    expect(keys).toContain(JSON.stringify(['connection', 'c1']))
    expect(keys).toContain(JSON.stringify(['connections']))
    expect(form.querySelector<HTMLInputElement>('input[name="apiSecret"]')!.value).toBe('')
    expect(container.textContent).toContain('Kimlik bilgisi şifreli olarak yenilendi')
  })

  // P-5 (30 Oct 2026): the store and environment cannot change; the form saves the name and points to a new connection.
  it('shows the store without editing it, saves only the name and points to a new connection', async () => {
    await renderPage()
    openSection('Mağaza ve Kimlik')
    const store = Array.from(container.querySelectorAll<HTMLInputElement>('.connection-settings-fields input')).find(input => input.value === '9999')!
    expect(store.readOnly).toBe(true)
    expect(container.querySelector('.connection-scope-locked')!.textContent).toContain('Ortam ve mağaza bağlantı eklenirken belirlenir, sonradan değiştirilemez')
    expect(container.querySelector<HTMLAnchorElement>('.connection-scope-locked a')!.getAttribute('href')).toBe('/integrations?yeni=1')

    const name = container.querySelector<HTMLInputElement>('.connection-settings-fields input:not([readonly])')!
    act(() => setValue(name, 'Trendyol Yeni Ad'))
    await act(async () => { name.form!.requestSubmit() })
    await settle()
    const save = apiState.calls.find(call => call.path === '/connections/c1' && call.method === 'PATCH')!
    expect(JSON.parse(save.body!)).toEqual({ displayName: 'Trendyol Yeni Ad' })
  })

  it('names the environment in Turkish and offers the test order without a fixed store number (W32)', async () => {
    await renderPage()
    expect(container.textContent).toContain('Test Ortamı · Mağaza 9999')
    expect(container.textContent).not.toContain('Stage ·')
    openSection('Araçlar')
    expect(container.textContent).toContain('Test Siparişi Oluştur')
  })
})

describe('IntegrationsPage filters (W16)', () => {
  let currentSearch = ''
  function LocationProbe() {
    currentSearch = useLocation().search
    return null
  }

  async function renderList(url: string) {
    client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
    root = createRoot(container)
    await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[url]}><IntegrationsPage /><LocationProbe /></MemoryRouter></QueryClientProvider>) })
    await settle()
  }

  it('opens with the platform filter from the address and writes a new choice back', async () => {
    await renderList('/integrations?platform=n11')
    expect(container.querySelector('.integration-filter-summary')!.textContent).toContain('N11')
    expect(container.querySelector('.integration-filter-summary')!.textContent).toContain('0 bağlantı')

    act(() => { container.querySelector<HTMLButtonElement>('.integration-filter-button')!.click() })
    const trendyol = Array.from(container.querySelectorAll<HTMLButtonElement>('.integration-filter-menu button')).find(button => button.textContent === 'Trendyol')!
    await act(async () => { trendyol.click() })
    await settle()
    expect(currentSearch).toBe('?platform=trendyol')
    expect(container.querySelector('.integration-filter-summary')!.textContent).toContain('1 bağlantı')
  })

  it('opens the new connection form from ?yeni=1 and drops it from the address', async () => {
    await renderList('/integrations?yeni=1')
    expect(container.querySelector('.integration-modal')).not.toBeNull()
    expect(currentSearch).toBe('')
  })

  it('offers Tam Sıfırlama beside the selective reset in Verileri Sıfırla', async () => {
    await renderList('/integrations')
    act(() => { container.querySelector<HTMLButtonElement>('.reference-menu-trigger')!.click() })
    const reset = Array.from(container.querySelectorAll<HTMLButtonElement>('.reference-menu-popover button')).find(button => button.textContent === 'Verileri Sıfırla')!
    await act(async () => { reset.click() })
    await settle()
    const full = Array.from(container.querySelectorAll<HTMLButtonElement>('.data-reset-modes button')).find(button => button.textContent?.includes('Tam Sıfırlama'))!
    await act(async () => { full.click() })
    await settle()
    expect(container.querySelector('.connection-full-reset')).not.toBeNull()
    expect(apiState.calls.some(call => call.path === '/connections/c1/full-reset' && call.method === 'GET')).toBe(true)
  })

  it('blocks a new Trendyol connection only for an identity that cannot be sent; another format is a warning', async () => {
    await renderList('/integrations')
    act(() => { container.querySelector<HTMLButtonElement>('.integration-add-button')!.click() })
    const identity = container.querySelector<HTMLInputElement>('input[name="userAgentIdentity"]')!
    const submit = () => container.querySelector<HTMLButtonElement>('.im-foot button[type="submit"]')!

    act(() => setValue(identity, '123456 - Gümüş'))
    expect(submit().disabled).toBe(true)
    expect(container.querySelector('.im-field-notice.is-error')).not.toBeNull()

    act(() => setValue(identity, 'Firma Entegrasyon'))
    expect(submit().disabled).toBe(false)
    expect(container.querySelector('.im-field-notice.is-warning')).not.toBeNull()
  })

  it('asks a new PTT AVM connection for its store id, API user name and API password on the live store only', async () => {
    await renderList('/integrations')
    act(() => { container.querySelector<HTMLButtonElement>('.integration-add-button')!.click() })
    const pttAvm = Array.from(container.querySelectorAll<HTMLButtonElement>('.im-platforms button')).find(button => button.textContent === 'PTT AVM')!
    act(() => { pttAvm.click() })

    expect(pttAvm.getAttribute('aria-checked')).toBe('true')
    expect(container.querySelector('input[name="pttAvmUsername"]')).not.toBeNull()
    expect(container.querySelector<HTMLInputElement>('input[name="pttAvmPassword"]')!.type).toBe('password')
    expect(container.querySelector<HTMLInputElement>('input[name="externalStoreId"]')!.inputMode).toBe('numeric')
    expect(container.querySelector('input[name="pttAvmApiKey"]')).toBeNull()
    expect(container.querySelector('select[name="environment"]')).toBeNull()
    expect(container.querySelector('.integration-modal')!.textContent).toContain('PTT AVM satıcı paneli › Hesap Yönetimi › Entegrasyon Bilgileri')

    act(() => setValue(container.querySelector<HTMLInputElement>('input[name="displayName"]')!, 'PTT AVM Canlı'))
    act(() => setValue(container.querySelector<HTMLInputElement>('input[name="externalStoreId"]')!, '123456'))
    act(() => setValue(container.querySelector<HTMLInputElement>('input[name="pttAvmUsername"]')!, 'user-1'))
    act(() => setValue(container.querySelector<HTMLInputElement>('input[name="pttAvmPassword"]')!, 'pass-1'))
    await act(async () => { container.querySelector<HTMLButtonElement>('.im-foot button[type="submit"]')!.click() })
    await settle()

    const created = apiState.calls.find(call => call.path === '/connections' && call.method === 'POST')!
    expect(JSON.parse(created.body!)).toMatchObject({ platformCode: 'PTTAVM', environment: 'PRODUCTION', apiVersion: 'V1', externalStoreId: '123456', pttAvmUsername: 'user-1', pttAvmPassword: 'pass-1' })
  })
})
