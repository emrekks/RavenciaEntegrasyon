import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// The account's label settings on the server: a PUT is taken only over the version it names (If-Match).
const server = vi.hoisted(() => ({ settings: { senderName: 'Ravencia' } as Record<string, unknown>, version: 3, puts: [] as Array<string | null> }))

vi.mock('../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../shared/api')>()
  const hubApi = vi.fn(async (path: string, init?: RequestInit) => {
    if (path !== '/settings/shipping-label') throw new Error(`Test API fixture does not handle ${path}`)
    if (init?.method === 'PUT') {
      const ifMatch = new Headers(init.headers).get('If-Match')
      server.puts.push(ifMatch)
      if (ifMatch !== `"v${server.version}"`) throw new original.ApiRequestError('Ayar başka bir oturumda değişti.', 412)
      server.settings = JSON.parse(String(init.body)) as Record<string, unknown>
      server.version++
    }
    return { settings: server.settings, version: server.version }
  })
  return { ...original, hubApi }
})

import { useShippingLabelSettings } from './shipping-settings'
import { normalizeShippingLabelSettings } from './shipping-label'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let container: HTMLDivElement
let root: Root
let client: QueryClient
let hook: ReturnType<typeof useShippingLabelSettings>

function Probe() {
  hook = useShippingLabelSettings()
  return null
}

async function settle() {
  for (let index = 0; index < 5; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 5)) })
}

beforeEach(async () => {
  server.settings = { ...normalizeShippingLabelSettings({}), senderName: 'Ravencia' }
  server.version = 3
  server.puts = []
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => { root.render(<QueryClientProvider client={client}><Probe /></QueryClientProvider>) })
  await settle()
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

async function changedElsewhere(senderName: string) {
  server.settings = { ...server.settings, senderName }
  server.version++
  await act(async () => { await client.invalidateQueries({ queryKey: ['shipping-label-settings'] }) })
  await settle()
}

describe('useShippingLabelSettings', () => {
  it('a clean draft follows the server', async () => {
    expect(hook.settings.senderName).toBe('Ravencia')
    await changedElsewhere('Ravencia Depo')
    expect(hook.settings.senderName).toBe('Ravencia Depo')
    expect(hook.conflict).toBe(false)
  })

  it('keeps an unsaved design when the settings change elsewhere, and saves it only on purpose', async () => {
    act(() => hook.setSettings({ ...hook.settings, senderName: 'Benim Tasarımım' }))
    await changedElsewhere('Başka Yönetici')
    // The draft stays; the newer server copy is offered, not forced.
    expect(hook.settings.senderName).toBe('Benim Tasarımım')
    expect(hook.conflict).toBe(true)
    // A plain save is still made over the version the draft started from: refused, the draft kept.
    await act(async () => { await expect(hook.save(hook.settings)).rejects.toThrow() })
    await settle()
    expect(hook.settings.senderName).toBe('Benim Tasarımım')
    expect(server.settings.senderName).toBe('Başka Yönetici')
    // Benimkiyle Üzerine Yaz writes the draft over the newer version.
    await act(async () => { await hook.overwrite() })
    await settle()
    expect(server.settings.senderName).toBe('Benim Tasarımım')
    expect(hook.conflict).toBe(false)
  })

  it('Güncel Ayarı Yükle drops the draft for the newer settings', async () => {
    act(() => hook.setSettings({ ...hook.settings, senderName: 'Benim Tasarımım' }))
    await changedElsewhere('Başka Yönetici')
    act(() => hook.acceptServer())
    await settle()
    expect(hook.settings.senderName).toBe('Başka Yönetici')
    expect(hook.conflict).toBe(false)
  })
})
