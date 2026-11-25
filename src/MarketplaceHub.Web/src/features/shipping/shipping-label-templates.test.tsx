import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// The account's template list on the server: a PUT is taken only over the version it names (If-Match); without one only
// while nothing is stored (428 after). `down` makes every request fail as when the server cannot be reached.
const server = vi.hoisted(() => ({
  templates: null as unknown[] | null,
  version: 0,
  down: false,
  puts: [] as Array<{ ifMatch: string | null; templates: Array<{ id: string; name: string; settings: { senderName?: string } }> }>,
  // Another device saves this list right before the next PUT lands.
  meanwhile: null as unknown[] | null
}))

vi.mock('../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../shared/api')>()
  const hubApi = vi.fn(async (path: string, init?: RequestInit) => {
    if (path !== '/settings/shipping-label-templates') throw new Error(`Test API fixture does not handle ${path}`)
    if (server.down) throw new original.ApiRequestError('Sunucuya ulaşılamadı.', 503)
    if (init?.method === 'PUT') {
      if (server.meanwhile) { server.templates = server.meanwhile; server.version++; server.meanwhile = null }
      const ifMatch = new Headers(init.headers).get('If-Match')
      const sent = JSON.parse(String(init.body)) as { templates: (typeof server.puts)[number]['templates'] }
      server.puts.push({ ifMatch, templates: sent.templates })
      if (ifMatch === null ? server.version > 0 : ifMatch !== `"v${server.version}"`) throw new original.ApiRequestError('Ayarlar başka bir oturumda değişti.', ifMatch === null ? 428 : 412)
      server.templates = sent.templates
      server.version++
    }
    return { settings: server.templates === null ? null : { templates: server.templates }, version: server.version }
  })
  return { ...original, hubApi }
})

import { deviceShippingLabelTemplatesKey, deviceShippingLabelTemplatesMovedKey, mergeShippingLabelTemplates, useShippingLabelTemplates, type ShippingLabelTemplate } from './shipping-label-templates'
import { normalizeShippingLabelSettings } from './shipping-label'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const template = (id: string, name: string, senderName: string): ShippingLabelTemplate =>
  ({ id, name, savedAt: '2026-11-20T09:00:00.000Z', settings: { ...normalizeShippingLabelSettings({}), senderName } })

let container: HTMLDivElement
let root: Root
let client: QueryClient
let hook: ReturnType<typeof useShippingLabelTemplates>

function Probe() {
  hook = useShippingLabelTemplates()
  return null
}

async function settle() {
  for (let index = 0; index < 8; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 5)) })
}

async function openOnDevice(device: ShippingLabelTemplate[], role = 'OWNER') {
  localStorage.clear()
  if (device.length) localStorage.setItem(deviceShippingLabelTemplatesKey, JSON.stringify(device))
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  client.setQueryData(['me'], { id: 'user-1', email: 'owner@example.com', displayName: 'Sahip', role, state: 'ACTIVE', tenantId: 'tenant-1' })
  await act(async () => { root.render(<QueryClientProvider client={client}><Probe /></QueryClientProvider>) })
  await settle()
}

const names = (list: Array<{ name: string }>) => list.map(item => item.name)

beforeEach(() => {
  server.templates = null
  server.version = 0
  server.down = false
  server.puts = []
  server.meanwhile = null
})

afterEach(() => {
  if (root) act(() => root.unmount())
  container?.remove()
  localStorage.clear()
})

describe('mergeShippingLabelTemplates', () => {
  it('keeps every account template, adds the device ones, and the device copy of a taken name gets (Bu Cihaz)', () => {
    const account = [template('a', 'Depo', 'Hesap'), template('c', 'Kargo', 'Hesap')]
    const device = [template('a', 'Depo', 'Cihaz'), template('d', 'Mağaza', 'Cihaz'), template('c', 'Kargo', 'Hesap')]
    const merged = mergeShippingLabelTemplates(account, device)
    expect(names(merged.templates)).toEqual(['Depo', 'Kargo', 'Depo (Bu Cihaz)', 'Mağaza'])
    expect(merged.templates[0].settings.senderName).toBe('Hesap')
    expect(merged.templates[2].settings.senderName).toBe('Cihaz')
    expect(new Set(merged.templates.map(item => item.id)).size).toBe(4)
    expect(merged.added).toBe(2)
  })
})

describe('useShippingLabelTemplates', () => {
  it('first device: an account without templates takes the device ones as they are; the device copy stays', async () => {
    const device = [template('t1', 'A4 Depo', 'Ravencia'), template('t2', 'Sticker', 'Ravencia')]
    await openOnDevice(device)

    expect(server.puts).toHaveLength(1)
    expect(server.puts[0].ifMatch).toBeNull()
    expect(names(server.puts[0].templates)).toEqual(['A4 Depo', 'Sticker'])
    expect(names(hook.templates)).toEqual(['A4 Depo', 'Sticker'])
    expect(hook.fromDevice).toBe(false)
    expect(JSON.parse(localStorage.getItem(deviceShippingLabelTemplatesKey) ?? '[]')).toEqual(device)
    expect(localStorage.getItem(deviceShippingLabelTemplatesMovedKey)).toBe('tenant-1')
  })

  it('second device: its other templates join the account list without losing any; the account wins a name', async () => {
    server.templates = [template('t1', 'A4 Depo', 'Ravencia'), template('t2', 'Sticker', 'Ravencia')]
    server.version = 1
    const device = [template('p1', 'A4 Depo', 'Telefon'), template('p2', 'Kapıda', 'Telefon')]
    await openOnDevice(device)

    expect(server.puts).toHaveLength(1)
    expect(server.puts[0].ifMatch).toBe('"v1"')
    expect(names(hook.templates)).toEqual(['A4 Depo', 'Sticker', 'A4 Depo (Bu Cihaz)', 'Kapıda'])
    expect(hook.templates[0].settings.senderName).toBe('Ravencia')
    expect(hook.templates[2].settings.senderName).toBe('Telefon')
    expect(JSON.parse(localStorage.getItem(deviceShippingLabelTemplatesKey) ?? '[]')).toEqual(device)
  })

  it('another device saving meanwhile: the merge is made again on the newer list, nothing of it is lost', async () => {
    server.meanwhile = [template('x1', 'Diğer Cihaz', 'Masa')]
    await openOnDevice([template('t1', 'A4 Depo', 'Ravencia')])

    expect(server.puts.map(put => put.ifMatch)).toEqual([null, '"v1"'])
    expect(names(server.templates as ShippingLabelTemplate[])).toEqual(['Diğer Cihaz', 'A4 Depo'])
  })

  it('a failed read shows the device copy and writes nothing to the account, not even a save', async () => {
    server.down = true
    server.templates = [template('t1', 'Hesap Şablonu', 'Ravencia')]
    server.version = 4
    const device = [template('p1', 'Cihaz Şablonu', 'Telefon')]
    await openOnDevice(device)

    expect(hook.fromDevice).toBe(true)
    expect(names(hook.templates)).toEqual(['Cihaz Şablonu'])
    await expect(hook.saveTemplate(template('n1', 'Yeni', 'Telefon'))).rejects.toThrow('hesaptan okunamadı')
    expect(server.puts).toHaveLength(0)
    expect(server.templates).toEqual([template('t1', 'Hesap Şablonu', 'Ravencia')])
    expect(localStorage.getItem(deviceShippingLabelTemplatesMovedKey)).toBeNull()
    expect(JSON.parse(localStorage.getItem(deviceShippingLabelTemplatesKey) ?? '[]')).toEqual(device)
  })

  it('moves a device list once: a template deleted on the account later is not brought back', async () => {
    server.templates = []
    server.version = 2
    localStorage.setItem(deviceShippingLabelTemplatesMovedKey, 'tenant-1')
    await openOnDevice([])
    localStorage.setItem(deviceShippingLabelTemplatesMovedKey, 'tenant-1')
    localStorage.setItem(deviceShippingLabelTemplatesKey, JSON.stringify([template('t1', 'Silinen', 'Ravencia')]))
    act(() => root.unmount())
    container.remove()
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
    await act(async () => { root.render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><Probe /></QueryClientProvider>) })
    await settle()

    expect(server.puts).toHaveLength(0)
    expect(hook.templates).toEqual([])
  })

  it('a role that may not write the account keeps its device copy for later and sends nothing', async () => {
    server.templates = [template('t1', 'Hesap Şablonu', 'Ravencia')]
    server.version = 1
    await openOnDevice([template('p1', 'Cihaz Şablonu', 'Telefon')], 'OPERATIONS')

    expect(server.puts).toHaveLength(0)
    expect(names(hook.templates)).toEqual(['Hesap Şablonu'])
    expect(localStorage.getItem(deviceShippingLabelTemplatesMovedKey)).toBeNull()
  })

  it('saves and deletes on the account over the version read', async () => {
    server.templates = [template('t1', 'Depo', 'Ravencia')]
    server.version = 1
    await openOnDevice([])

    await act(async () => { await hook.saveTemplate(template('t2', 'Mağaza', 'Ravencia')) })
    await act(async () => { await hook.deleteTemplate('t1') })

    expect(server.puts.map(put => put.ifMatch)).toEqual(['"v1"', '"v2"'])
    expect(names(server.templates as ShippingLabelTemplate[])).toEqual(['Mağaza'])
    expect(names(hook.templates)).toEqual(['Mağaza'])
  })
})
