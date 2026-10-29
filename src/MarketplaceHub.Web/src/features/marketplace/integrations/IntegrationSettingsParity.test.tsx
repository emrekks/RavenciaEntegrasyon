import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// The settings window of every marketplace has the same common sections (Genel Bakış, Pazaryerine Yazma with Automatic
// and Manual, Faturalama); only Veri Eşitleme and Mağaza ve Kimlik differ per platform.
const apiState = vi.hoisted(() => ({ platformCode: 'TRENDYOL', invoiceCreationEnabled: false, externalWritesEnabled: true, calls: [] as Array<{ path: string; method: string; body?: string }> }))

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const connection = () => ({ id: 'c1', publicId: 'c1', platformCode: apiState.platformCode, environment: 'PRODUCTION', displayName: `${apiState.platformCode} Mağaza`, externalStoreId: '1234', status: 'ACTIVE', apiVersion: 'V1', lastTestedAt: '2026-10-28T10:00:00Z', lastSuccessAt: '2026-10-28T10:00:00Z', lastErrorCode: null, hasCredential: true, externalWritesEnabled: apiState.externalWritesEnabled, invoiceCreationEnabled: apiState.invoiceCreationEnabled, version: 3 })
  const policy = (resourceType: string, enabled: boolean, requiresExternalWrites = false) => ({ id: `p-${resourceType}`, resourceType, intervalSeconds: requiresExternalWrites ? 0 : 300, overlapSeconds: 0, jitterSeconds: 0, enabled, version: 1, lastSuccessAt: '2026-10-28T10:00:00Z', lastModifiedWatermark: null, healthStatus: requiresExternalWrites ? 'PERMISSION' : 'HEALTHY', requiresExternalWrites })
  const policies = () => [policy('ORDERS', true), policy('PRICE_WRITE', false, true), policy('STOCK_WRITE', false, true), policy('SHIPMENT_WRITE', true, true), policy('RETURN_WRITE', true, true), policy('MANUAL_WRITE', true, true)]
  const respond = async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    apiState.calls.push({ path, method, body: typeof init?.body === 'string' ? init.body : undefined })
    if (path === '/connections/c1' && method === 'PATCH') {
      const body = JSON.parse(String(init?.body)) as { invoiceCreationEnabled?: boolean }
      if (body.invoiceCreationEnabled !== undefined) apiState.invoiceCreationEnabled = body.invoiceCreationEnabled
      return connection()
    }
    if (path === '/connections/c1') return connection()
    if (path.endsWith('/sync-policies')) return policies()
    if (path.endsWith('/capabilities')) return ['PRICE_WRITE', 'INVENTORY_WRITE', 'SHIPMENT_WRITE', 'RETURN_READ', 'RETURN_WRITE'].map(code => ({ code, supportLevel: 'SUPPORTED', verifiedForConnection: true, verifiedAt: '2026-10-28T10:00:00Z' }))
    if (path.endsWith('/instant-notifications')) return { supported: false }
    return []
  }
  return { ...original, hubApi: vi.fn(respond), hubPoll: vi.fn(respond), loadAllPages: vi.fn(async () => ({ items: [connection()], nextCursor: null, hasMore: false })) }
})

import { IntegrationDetailPage } from './IntegrationPages'

let container: HTMLDivElement
let root: Root
let client: QueryClient

async function settle() {
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 20)) })
}

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  apiState.calls = []
  apiState.invoiceCreationEnabled = false
  apiState.externalWritesEnabled = true
  Element.prototype.scrollIntoView = vi.fn()
  container = document.createElement('div')
  document.body.append(container)
})

afterEach(() => {
  act(() => root?.unmount())
  client?.clear()
  container.remove()
})

async function renderFor(platformCode: string) {
  apiState.platformCode = platformCode
  client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  root = createRoot(container)
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/integrations/c1']}><Routes><Route path="/integrations/:id" element={<IntegrationDetailPage />} /></Routes></MemoryRouter></QueryClientProvider>) })
  await settle()
  await settle()
}

function navLabels() {
  return Array.from(container.querySelectorAll<HTMLButtonElement>('.cs-nav > button')).map(button => button.textContent ?? '')
}

function openSection(label: string) {
  const tab = Array.from(container.querySelectorAll<HTMLButtonElement>('.cs-nav > button')).find(button => button.textContent?.includes(label))!
  act(() => { tab.click() })
}

const marketplaces = ['TRENDYOL', 'HEPSIBURADA', 'N11', 'PAZARAMA', 'PTTAVM', 'SHOPIFY']

describe('integration settings parity', () => {
  it.each(marketplaces)('%s has the common sections in the same order', async platformCode => {
    await renderFor(platformCode)
    const labels = navLabels()
    for (const label of ['Genel Bakış', 'Veri Eşitleme', 'Pazaryerine Yazma', 'Faturalama', 'Mağaza ve Kimlik'])
      expect(labels.some(text => text.includes(label))).toBe(true)
    const common = ['Genel Bakış', 'Veri Eşitleme', 'Pazaryerine Yazma', 'Faturalama', 'Mağaza ve Kimlik'].map(label => labels.findIndex(text => text.includes(label)))
    expect(common).toEqual([...common].sort((left, right) => left - right))
  })

  it.each(marketplaces)('%s overview has the five common parts and names both write modes', async platformCode => {
    await renderFor(platformCode)
    const pane = container.querySelector('.cs-pane')!.textContent ?? ''
    for (const part of ['Kimlik Bilgisi', 'Son Doğrulama', 'Pazaryerine Yazma', 'Akış Sağlığı', 'Dikkat İsteyenler'])
      expect(pane).toContain(part)
    expect(pane).toContain('Otomatik: ')
    expect(pane).toContain('Manuel: ')
  })

  it.each(marketplaces)('%s splits Pazaryerine Yazma into Automatic and Manual', async platformCode => {
    await renderFor(platformCode)
    openSection('Pazaryerine Yazma')
    expect(container.querySelector('[data-external-write-group="automatic-external-writes"]')).not.toBeNull()
    expect(container.querySelector('[data-external-write-group="manual-external-writes"]')).not.toBeNull()
    expect(container.textContent).toContain('Dış Yazma Ana Anahtarı')
  })

  it('PTT AVM manual writes have their own switch, saved on the MANUAL_WRITE policy', async () => {
    await renderFor('PTTAVM')
    openSection('Pazaryerine Yazma')
    const manual = container.querySelector('[data-external-write-group="manual-external-writes"]')!
    const toggle = manual.querySelector<HTMLInputElement>('input[type="checkbox"]')!
    expect(toggle).not.toBeNull()
    expect(toggle.checked).toBe(true)
    expect(toggle.disabled).toBe(false)
    expect(manual.textContent).toContain('satıcı panelinden')
    const automatic = container.querySelector('[data-external-write-group="automatic-external-writes"]')!
    expect(automatic.querySelector('input[type="checkbox"]')).not.toBeNull()
    await act(async () => { toggle.click() })
    await settle()
    const saves = apiState.calls.filter(call => call.method === 'PUT' && call.path.includes('/sync-policies/'))
    expect(saves.map(call => call.path)).toEqual(['/connections/c1/sync-policies/MANUAL_WRITE'])
    expect(JSON.parse(saves[0].body!)).toMatchObject({ enabled: false })
  })

  it.each(['TRENDYOL', 'HEPSIBURADA', 'N11', 'PAZARAMA'])('%s manual group stays on SHIPMENT_WRITE and RETURN_WRITE', async platformCode => {
    await renderFor(platformCode)
    openSection('Pazaryerine Yazma')
    const toggle = container.querySelector<HTMLInputElement>('[data-external-write-group="manual-external-writes"] input[type="checkbox"]')!
    await act(async () => { toggle.click() })
    await settle()
    const saved = apiState.calls.filter(call => call.method === 'PUT' && call.path.includes('/sync-policies/')).map(call => call.path.split('/').pop())
    expect(saved.sort()).toEqual(['RETURN_WRITE', 'SHIPMENT_WRITE'])
  })

  it('Hepsiburada invoicing turns on from its own Faturalama section', async () => {
    await renderFor('HEPSIBURADA')
    openSection('Faturalama')
    const toggle = container.querySelector<HTMLInputElement>('.invoice-creation-setting-panel input[role="switch"]')!
    expect(toggle.disabled).toBe(false)
    expect(toggle.checked).toBe(false)
    await act(async () => { toggle.click() })
    await settle()
    const save = apiState.calls.find(call => call.path === '/connections/c1' && call.method === 'PATCH')!
    expect(JSON.parse(save.body!)).toMatchObject({ invoiceCreationEnabled: true })
  })

  it('Shopify shows the Faturalama switch locked off instead of a switch that would snap back', async () => {
    await renderFor('SHOPIFY')
    openSection('Faturalama')
    const toggle = container.querySelector<HTMLInputElement>('.invoice-creation-setting-panel input[role="switch"]')!
    expect(toggle.disabled).toBe(true)
    expect(toggle.checked).toBe(false)
    expect(container.textContent).toContain('sözleşmesi henüz doğrulanmadı')
  })
})
