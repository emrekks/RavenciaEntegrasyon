import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// The account's appearance on the server: a PUT is taken only over the version it names (If-Match).
const server = vi.hoisted(() => ({ settings: { fontFamily: 'inter' } as Record<string, unknown>, version: 2 }))

vi.mock('../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../shared/api')>()
  const hubApi = vi.fn(async (path: string, init?: RequestInit) => {
    if (path !== '/settings/appearance') throw new Error(`Test API fixture does not handle ${path}`)
    if (init?.method === 'PUT') {
      if (new Headers(init.headers).get('If-Match') !== `"v${server.version}"`) throw new original.ApiRequestError('Ayar başka bir oturumda değişti.', 412)
      server.settings = JSON.parse(String(init.body)) as Record<string, unknown>
      server.version++
    }
    return { settings: server.settings, version: server.version }
  })
  return { ...original, hubApi }
})

import { defaultAppearanceColorTheme, useAppearanceSettings } from './appearance-settings'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let container: HTMLDivElement
let root: Root
let client: QueryClient
let hook: ReturnType<typeof useAppearanceSettings>

function Probe() {
  hook = useAppearanceSettings()
  return null
}

async function settle() {
  for (let index = 0; index < 5; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 5)) })
}

beforeEach(async () => {
  localStorage.clear()
  server.settings = { fontFamily: 'inter' }
  server.version = 2
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

async function changedElsewhere(fontFamily: string) {
  server.settings = { ...server.settings, fontFamily }
  server.version++
  await act(async () => { await client.invalidateQueries({ queryKey: ['appearance-settings'] }) })
  await settle()
}

describe('useAppearanceSettings draft', () => {
  it('a clean draft follows the server', async () => {
    await changedElsewhere('arial')
    expect(hook.draft.fontFamily).toBe('arial')
    expect(hook.conflict).toBe(false)
  })

  it('keeps unsaved edits when the appearance changes elsewhere, and writes them only on purpose', async () => {
    act(() => hook.setDraft({ ...hook.draft, fontFamily: 'segoe' }))
    await changedElsewhere('arial')
    expect(hook.draft.fontFamily).toBe('segoe')
    expect(hook.conflict).toBe(true)
    await act(async () => { await expect(hook.save(hook.draft)).rejects.toThrow() })
    await settle()
    expect(hook.draft.fontFamily).toBe('segoe')
    expect(server.settings.fontFamily).toBe('arial')
    await act(async () => { await hook.overwrite() })
    await settle()
    expect(server.settings.fontFamily).toBe('segoe')
    expect(hook.conflict).toBe(false)
  })

  it('acceptServer drops the draft for the newer appearance', async () => {
    act(() => hook.setDraft({ ...hook.draft, fontFamily: 'segoe' }))
    await changedElsewhere('arial')
    act(() => hook.acceptServer())
    await settle()
    expect(hook.draft.fontFamily).toBe('arial')
    expect(hook.conflict).toBe(false)
  })

  it('an edited draft over a newer version of the same appearance is no conflict, and saves over that version', async () => {
    act(() => hook.setDraft({ ...hook.draft, fontFamily: 'segoe' }))
    server.version++
    await act(async () => { await client.invalidateQueries({ queryKey: ['appearance-settings'] }) })
    await settle()
    expect(hook.draft.fontFamily).toBe('segoe')
    expect(hook.conflict).toBe(false)
    await act(async () => { await hook.save(hook.draft) })
    await settle()
    expect(server.settings.fontFamily).toBe('segoe')
    expect(hook.draft.fontFamily).toBe('segoe')
  })
})

describe('saving a colour theme', () => {
  it('writes the theme only and keeps an unapplied font as an unsaved preview', async () => {
    act(() => hook.setDraft({ ...hook.draft, fontFamily: 'segoe' }))
    const profile = { id: 'custom-1', name: 'Örnek Tema', palette: { ...defaultAppearanceColorTheme.palette }, builtIn: false }
    await act(async () => { await hook.saveThemeChange(value => ({ ...value, colorThemes: [...value.colorThemes, profile] })) })
    await settle()
    expect(server.settings.fontFamily).toBe('inter')
    expect((server.settings.colorThemes as { id: string }[]).map(theme => theme.id)).toContain('custom-1')
    expect(hook.draft.fontFamily).toBe('segoe')
    expect(hook.draft.colorThemes.map(theme => theme.id)).toContain('custom-1')
  })
})
