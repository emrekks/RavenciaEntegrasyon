import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// WEB-SAYFALAR-11 characterization: what the Alarm Bildirimleri, Yapay Zekâ and Google Drive Yedeği sections do with their
// form copy of the saved settings (when it counts as changed, what a refetch does to an untouched and to an edited form,
// the save's version, messages and button states). Written before the sections moved onto useServerDraft.
const server = vi.hoisted(() => ({
  alert: { senderAddress: 'ravencia.alarm@gmail.com', recipients: ['owner@example.com'], appPasswordSaved: true, updatedAt: '2026-11-03T19:00:00Z', version: 2 },
  ai: { apiKeySaved: true, defaultModel: 'claude-sonnet-5-5', updatedAt: '2026-11-06T19:00:00Z', version: 5, skills: [] as unknown[] },
  offsite: {} as Record<string, unknown>,
  puts: [] as Array<{ path: string; ifMatch: string | null; body: Record<string, unknown> }>
}))

const offsiteStart = {
  enabled: true, ageRecipient: 'age1ql3z7hjy54pw3hyww5ayyfg7zqgvc7w3j2elw8zmrj2kg5sfn9aqmcac8p', folderMode: 'NAME', folderValue: 'Ravencia Yedek', keepDays: 14, keepMonths: 12,
  clientId: '123456789-abcdefghijklmnop.apps.googleusercontent.com', clientSecretSaved: true, redirectUri: 'https://panel.example.invalid/settings/google-drive-callback',
  scopes: 'openid email', connected: true, account: 'owner@gmail.com', connectedAt: '2026-11-13T09:00:00Z', authorizationInvalid: false, authorizationError: null,
  folderId: 'folder-1', lastAttemptAt: '2026-11-13T09:30:00Z', lastSuccessAt: '2026-11-13T09:31:00Z', lastSet: '20261113T085901Z', lastSize: 157286400,
  lastError: null, lastErrorAt: null, consecutiveFailures: 0, requestedAt: null, runningSince: null, nextRunAt: '2026-11-13T21:30:00Z', remoteCopies: 3, updatedAt: '2026-11-13T08:00:00Z', version: 4
}

vi.mock('../../../shared/api', async importOriginal => {
  const actual = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    const key = path === '/settings/alert-email' ? 'alert' : path === '/settings/ai' ? 'ai' : path === '/settings/offsite-backup' ? 'offsite' : null
    if (path === '/settings/ai/usage?limit=50') return { items: [] }
    if (!key) throw new Error(`Test API fixture does not handle ${path}`)
    const current = server[key] as { version: number }
    if (method === 'PUT') {
      const ifMatch = new Headers(init?.headers).get('If-Match')
      const body = JSON.parse(String(init?.body)) as Record<string, unknown>
      server.puts.push({ path, ifMatch, body })
      if (ifMatch !== `"v${current.version}"`) throw new actual.ApiRequestError('Ayar başka bir oturumda değişti; sayfayı yenileyin.', 412)
      if (key === 'alert') server.alert = { ...server.alert, senderAddress: String(body.senderAddress), recipients: body.recipients as string[], updatedAt: '2026-11-03T20:00:00Z', version: server.alert.version + 1 }
      if (key === 'ai') server.ai = { ...server.ai, defaultModel: String(body.defaultModel), updatedAt: '2026-11-06T20:00:00Z', version: server.ai.version + 1 }
      if (key === 'offsite') server.offsite = { ...server.offsite, enabled: body.enabled, version: (server.offsite.version as number) + 1 }
    }
    return structuredClone(server[key])
  }
  return { ...actual, hubApi: vi.fn(respond), hubPoll: vi.fn(respond) }
})

import { AlertEmailSettingsSection } from './AlertEmailSection'
import { AiSettingsSection } from './AiSettingsSection'
import { OffsiteBackupSection } from './OffsiteBackupSection'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let container: HTMLDivElement
let root: Root
let client: QueryClient

async function settle() {
  for (let index = 0; index < 4; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 5)) })
}

async function render(node: React.ReactNode) {
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter>{node}</MemoryRouter></QueryClientProvider>) })
  await settle()
}

function input(element: HTMLInputElement, value: string) {
  act(() => {
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set?.call(element, value)
    element.dispatchEvent(new Event('input', { bubbles: true }))
  })
}
const button = (label: string) => Array.from(document.querySelectorAll<HTMLButtonElement>('button')).find(item => item.textContent?.trim() === label)
const barCopy = () => document.querySelector('.ss-bar-copy span')?.textContent ?? null
async function refetch(key: string) {
  await act(async () => { await client.invalidateQueries({ queryKey: [key] }) })
  await settle()
}
async function click(element: HTMLElement | undefined) {
  await act(async () => { element!.click() })
  await settle()
}

beforeEach(() => {
  server.alert = { senderAddress: 'ravencia.alarm@gmail.com', recipients: ['owner@example.com'], appPasswordSaved: true, updatedAt: '2026-11-03T19:00:00Z', version: 2 }
  server.ai = { apiKeySaved: true, defaultModel: 'claude-sonnet-5-5', updatedAt: '2026-11-06T19:00:00Z', version: 5, skills: [] }
  server.offsite = { ...offsiteStart }
  server.puts = []
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

describe('Alarm Bildirimleri form copy', () => {
  const sender = () => container.querySelector<HTMLInputElement>('input[type="email"]')!

  it('starts from the saved settings, counts a change as unsaved and blocks the test mail until it is saved', async () => {
    await render(<AlertEmailSettingsSection />)
    expect(sender().value).toBe('ravencia.alarm@gmail.com')
    expect(button('Ayarları Kaydet')!.disabled).toBe(true)
    expect(barCopy()).toMatch(/^Son kayıt/)
    // Case and spaces alone are no change.
    input(sender(), '  RAVENCIA.alarm@gmail.com ')
    expect(button('Ayarları Kaydet')!.disabled).toBe(true)
    input(sender(), 'yeni@gmail.com')
    expect(button('Ayarları Kaydet')!.disabled).toBe(false)
    expect(barCopy()).toBe('Kaydedilmemiş değişiklikler var')
    expect(button('Test E-postası Gönder')!.disabled).toBe(true)
    expect(container.textContent).toContain('Test için önce değişiklikleri kaydedin.')
  })

  it('keeps an edit across a refetch of the same version, and drops it for a newer version', async () => {
    await render(<AlertEmailSettingsSection />)
    input(sender(), 'yeni@gmail.com')
    await refetch('alert-email-settings')
    expect(sender().value).toBe('yeni@gmail.com')
    server.alert = { ...server.alert, senderAddress: 'baska@gmail.com', version: 3 }
    await refetch('alert-email-settings')
    expect(sender().value).toBe('baska@gmail.com')
    expect(button('Ayarları Kaydet')!.disabled).toBe(true)
  })

  it('saves over the version read, says so and starts again from the saved copy', async () => {
    await render(<AlertEmailSettingsSection />)
    input(sender(), 'yeni@gmail.com')
    await click(button('Ayarları Kaydet'))
    expect(server.puts.at(-1)).toMatchObject({ ifMatch: '"v2"', body: { senderAddress: 'yeni@gmail.com', appPassword: null } })
    expect(document.body.textContent).toMatch(/Alarm E-postası Ayarları Kaydedildi/)
    expect(sender().value).toBe('yeni@gmail.com')
    expect(button('Ayarları Kaydet')!.disabled).toBe(true)
  })

  it('shows a refused save (412) as an error and keeps the edit', async () => {
    await render(<AlertEmailSettingsSection />)
    input(sender(), 'yeni@gmail.com')
    server.alert = { ...server.alert, version: 9 }
    await click(button('Ayarları Kaydet'))
    expect(document.body.textContent).toContain('Ayar başka bir oturumda değişti')
    expect(sender().value).toBe('yeni@gmail.com')
    expect(button('Ayarları Kaydet')!.disabled).toBe(false)
    expect(button('Güncel Ayarı Yükle')).toBeUndefined()
  })
})

describe('Yapay Zekâ form copy', () => {
  const keyField = () => container.querySelector<HTMLInputElement>('input[aria-label="Claude API Anahtarı"]')

  it('starts from the saved settings; a key typed counts as a change and a newer version clears it', async () => {
    await render(<AiSettingsSection />)
    expect(button('Ayarları Kaydet')!.disabled).toBe(true)
    expect(keyField()).toBeNull()
    await click(button('Değiştir'))
    input(keyField()!, 'sk-ant-yeni')
    expect(button('Ayarları Kaydet')!.disabled).toBe(false)
    expect(button('Bağlantıyı Sına')!.disabled).toBe(true)
    await refetch('ai-settings')
    expect(keyField()!.value).toBe('sk-ant-yeni')
    server.ai = { ...server.ai, version: 6 }
    await refetch('ai-settings')
    // A newer version starts the form again: the key field closes and empties.
    expect(keyField()).toBeNull()
    expect(button('Ayarları Kaydet')!.disabled).toBe(true)
  })

  it('saves over the version read with the key only when it was changed', async () => {
    await render(<AiSettingsSection />)
    await click(button('Değiştir'))
    input(keyField()!, 'sk-ant-yeni')
    await click(button('Ayarları Kaydet'))
    expect(server.puts.at(-1)).toMatchObject({ ifMatch: '"v5"', body: { apiKey: 'sk-ant-yeni', defaultModel: 'claude-sonnet-5-5' } })
    expect(document.body.textContent).toMatch(/Yapay Zekâ Ayarları Kaydedildi/)
    expect(keyField()).toBeNull()
    expect(button('Ayarları Kaydet')!.disabled).toBe(true)
  })

  it('shows a refused save (412) as an error and keeps the key typed', async () => {
    await render(<AiSettingsSection />)
    await click(button('Değiştir'))
    input(keyField()!, 'sk-ant-yeni')
    server.ai = { ...server.ai, version: 8 }
    await click(button('Ayarları Kaydet'))
    expect(document.body.textContent).toContain('Ayar başka bir oturumda değişti')
    expect(keyField()!.value).toBe('sk-ant-yeni')
    expect(button('Güncel Ayarı Yükle')).toBeUndefined()
  })
})

describe('Google Drive Yedeği form copy', () => {
  const autoSend = () => container.querySelector<HTMLInputElement>('input[aria-label="Otomatik Gönderim"]')!
  async function toggle() {
    await act(async () => { autoSend().click() })
    await settle()
  }

  it('shows the save bar only while the form differs from the saved settings', async () => {
    await render(<OffsiteBackupSection mfa={false} />)
    expect(autoSend().checked).toBe(true)
    expect(button('Ayarları Kaydet')).toBeUndefined()
    await toggle()
    expect(autoSend().checked).toBe(false)
    expect(button('Ayarları Kaydet')!.disabled).toBe(false)
    expect(barCopy()).toBe('Kaydedilmemiş değişiklikler var')
    expect(button('Şimdi Gönder')!.disabled).toBe(true)
    await toggle()
    expect(button('Ayarları Kaydet')).toBeUndefined()
  })

  it('keeps an edit across a status refetch of the same version, and drops it for a newer version', async () => {
    await render(<OffsiteBackupSection mfa={false} />)
    await toggle()
    server.offsite = { ...server.offsite, remoteCopies: 4 }
    await refetch('offsite-backup')
    expect(autoSend().checked).toBe(false)
    server.offsite = { ...server.offsite, enabled: true, version: 5 }
    await refetch('offsite-backup')
    expect(autoSend().checked).toBe(true)
    expect(button('Ayarları Kaydet')).toBeUndefined()
  })

  it('saves over the version read after the password, says so and starts again from the saved copy', async () => {
    await render(<OffsiteBackupSection mfa={false} />)
    await toggle()
    await click(button('Ayarları Kaydet'))
    input(document.querySelector<HTMLInputElement>('#ss-offsite-confirm input[type="password"]')!, 'my-password')
    await act(async () => { document.querySelector<HTMLFormElement>('#ss-offsite-confirm')!.requestSubmit() })
    await settle()
    expect(server.puts.at(-1)).toMatchObject({ ifMatch: '"v4"', body: { enabled: false } })
    expect(document.body.textContent).toMatch(/Google Drive Yedeği Ayarları Kaydedildi/)
    expect(autoSend().checked).toBe(false)
    expect(button('Ayarları Kaydet')).toBeUndefined()
  })

  it('shows a refused save (412) in the password window and keeps the edit', async () => {
    await render(<OffsiteBackupSection mfa={false} />)
    await toggle()
    server.offsite = { ...server.offsite, version: 9 }
    await click(button('Ayarları Kaydet'))
    input(document.querySelector<HTMLInputElement>('#ss-offsite-confirm input[type="password"]')!, 'my-password')
    await act(async () => { document.querySelector<HTMLFormElement>('#ss-offsite-confirm')!.requestSubmit() })
    await settle()
    expect(document.querySelector('#ss-offsite-confirm .ss-ferr')).not.toBeNull()
    expect(autoSend().checked).toBe(false)
    expect(button('Güncel Ayarı Yükle')).toBeUndefined()
  })
})
