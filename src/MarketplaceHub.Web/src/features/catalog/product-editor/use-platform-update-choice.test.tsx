import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { legacyPlatformUpdatePreferencesKey, legacyPlatformUpdateStoresKey, platformUpdateChoiceRecord, type PlatformUpdateChoiceRecord } from './platform-update-fields'

const hubApi = vi.fn<(path: string, init?: RequestInit) => Promise<unknown>>()
vi.mock('../../../shared/api', () => ({
  hubApi: (path: string, init?: RequestInit) => hubApi(path, init),
  settingsIfMatch: (version: number | null | undefined) => version && version > 0 ? { 'If-Match': `"v${version}"` } : {}
}))

const { usePlatformUpdateChoice } = await import('./use-platform-update-choice')

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let root: Root | undefined
let host: HTMLDivElement | undefined
let current: ReturnType<typeof usePlatformUpdateChoice> | undefined

function Probe() {
  current = usePlatformUpdateChoice()
  return null
}

async function mount() {
  host = document.createElement('div')
  document.body.append(host)
  root = createRoot(host)
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => { root?.render(<QueryClientProvider client={client}><Probe /></QueryClientProvider>) })
  await until(() => current?.loaded === true)
}

// Waits, a little at a time, until the hook's reads and writes have settled.
async function until(check: () => boolean) {
  for (let index = 0; index < 100 && !check(); index += 1) await act(async () => { await new Promise(resolve => setTimeout(resolve, 5)) })
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 5)) })
}

const legacyPreferences = '{"fields":["TITLE","PRICE"],"openGroups":["sales"],"knownFields":["TITLE","DESCRIPTION","IMAGES","ATTRIBUTES","WARRANTY","DESI","DELIVERY","PRICE","STOCK","VAT_RATE"],"knownGroups":["product","shipping","sales"]}'
const puts = () => hubApi.mock.calls.filter(([, init]) => init?.method === 'PUT')

beforeEach(() => {
  hubApi.mockReset()
  window.localStorage.clear()
})

afterEach(() => {
  if (root) act(() => root?.unmount())
  host?.remove()
  root = undefined
  host = undefined
  current = undefined
})

describe('usePlatformUpdateChoice (9.1.16)', () => {
  it('uses the server choice, the same on every device, and leaves a local copy alone when the server has one', async () => {
    const saved = platformUpdateChoiceRecord({ fields: ['DESI'], openGroups: [] }, ['ty'])
    window.localStorage.setItem(legacyPlatformUpdatePreferencesKey, legacyPreferences)
    hubApi.mockResolvedValue({ settings: saved, version: 3 })
    await mount()
    expect(current?.loaded).toBe(true)
    expect(current?.record).toEqual(saved)
    expect(puts()).toHaveLength(0)
  })

  it('moves the browser’s old choice to the server once when the server has none, then removes the local copy', async () => {
    window.localStorage.setItem(legacyPlatformUpdatePreferencesKey, legacyPreferences)
    window.localStorage.setItem(legacyPlatformUpdateStoresKey, '{"selected":["ty"]}')
    hubApi.mockImplementation(async (_path, init) => init?.method === 'PUT' ? { settings: JSON.parse(String(init.body)) as PlatformUpdateChoiceRecord, version: 1 } : { settings: null, version: 0 })
    await mount()
    await until(() => window.localStorage.getItem(legacyPlatformUpdatePreferencesKey) === null)
    expect(puts()).toHaveLength(1)
    const [path, init] = puts()[0]!
    expect(path).toBe('/settings/platform-update')
    // The first save of the setting goes without If-Match (nothing stored yet).
    expect(init?.headers).toEqual({})
    expect(JSON.parse(String(init?.body))).toMatchObject({ fields: ['TITLE', 'PRICE'], openGroups: ['sales'], connectionIds: ['ty'] })
    expect(window.localStorage.getItem(legacyPlatformUpdatePreferencesKey)).toBeNull()
    expect(window.localStorage.getItem(legacyPlatformUpdateStoresKey)).toBeNull()
    expect(current?.record?.fields).toEqual(['TITLE', 'PRICE'])
  })

  it('keeps the local copy, and goes on using it, when the move fails (a role that may not save, offline)', async () => {
    window.localStorage.setItem(legacyPlatformUpdatePreferencesKey, legacyPreferences)
    hubApi.mockImplementation(async (_path, init) => { if (init?.method === 'PUT') throw new Error('Bu işlem için yeterli yetkiniz yok.'); return { settings: null, version: 0 } })
    await mount()
    await until(() => puts().length > 0)
    expect(puts()).toHaveLength(1)
    expect(window.localStorage.getItem(legacyPlatformUpdatePreferencesKey)).toBe(legacyPreferences)
    expect(current?.record?.fields).toEqual(['TITLE', 'PRICE'])
    expect(current?.record?.connectionIds).toBeNull()
  })

  it('has no choice and moves nothing with neither a server nor a local choice', async () => {
    hubApi.mockResolvedValue({ settings: null, version: 0 })
    await mount()
    expect(current?.record).toBeNull()
    expect(puts()).toHaveLength(0)
  })

  it('saves over the version read', async () => {
    hubApi.mockImplementation(async (_path, init) => init?.method === 'PUT' ? { settings: JSON.parse(String(init.body)) as PlatformUpdateChoiceRecord, version: 5 } : { settings: platformUpdateChoiceRecord({ fields: ['PRICE'], openGroups: [] }, null), version: 4 })
    await mount()
    const next = platformUpdateChoiceRecord({ fields: ['STOCK'], openGroups: ['sales'] }, ['hb'])
    await act(async () => { await current?.save(next) })
    await until(() => current?.record?.fields[0] === 'STOCK')
    expect(puts()[0]?.[1]?.headers).toEqual({ 'If-Match': '"v4"' })
    expect(current?.record).toEqual(next)
  })
})
