import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { panelResetConfirmed, panelResetStepStates, type PanelFactoryResetView } from './panel-factory-reset'

const state = vi.hoisted(() => ({ calls: [] as Array<{ path: string; method: string; body?: string; key?: string | null }>, view: null as unknown, forbidden: false }))

vi.mock('../../shared/api', async importOriginal => {
  const actual = await importOriginal<typeof import('../../shared/api')>()
  const respond = async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    state.calls.push({ path, method, body: typeof init?.body === 'string' ? init.body : undefined, key: new Headers(init?.headers).get('Idempotency-Key') })
    if (state.forbidden) throw new actual.ApiRequestError('Bu işlem için yeterli yetkiniz yok.', 403, 'PERMISSION_DENIED')
    if (path === '/settings/factory-reset' && method === 'POST') {
      const running = { ...(state.view as PanelFactoryResetView), job: { jobId: 'j1', status: 'LEASED', step: 2, progressLabel: 'Siparişler siliniyor', progressPercent: 20, error: null, createdAt: '2026-11-12T10:00:00Z', completedAt: null } }
      state.view = running
      return running
    }
    if (path === '/settings/factory-reset') return state.view
    throw new Error(`Unhandled ${method} ${path}`)
  }
  return { ...actual, hubApi: vi.fn(respond), hubPoll: vi.fn(respond) }
})

import { PanelFactoryResetSection } from './PanelFactoryResetSection'

const baseView: PanelFactoryResetView = {
  groups: [{ key: 'ORDERS', label: 'Siparişler, Paketler, İadeler ve Sorular', count: 1200 }, { key: 'LOGS', label: 'İşler, Eşitleme Kayıtları ve Tüm Loglar', count: 34000 }],
  total: 35200,
  steps: ['Süren işlerin bitmesi bekleniyor', 'Faturalar ve belgeleri siliniyor', 'Siparişler, paketler, iadeler ve sorular siliniyor', 'Tamamlandı'],
  kept: ['Hesap, kullanıcılar, roller ve açık olan bu oturum', 'Entegrasyon bağlantıları ve giriş bilgileri'],
  confirmationWord: 'PANELİ SIFIRLA',
  job: null
}

let container: HTMLDivElement
let root: Root

async function render() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => { root.render(<QueryClientProvider client={client}><div className="ss-page"><PanelFactoryResetSection /></div></QueryClientProvider>) })
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 0)) })
}

function type(input: HTMLInputElement, value: string) {
  act(() => {
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(input, value)
    input.dispatchEvent(new Event('input', { bubbles: true }))
  })
}

const goButton = () => Array.from(container.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent === 'Paneli Sıfırla')

beforeEach(() => {
  state.calls = []
  state.view = baseView
  state.forbidden = false
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

describe('panel factory reset confirmation', () => {
  it('opens only for the exact word with the Turkish İ', () => {
    expect(panelResetConfirmed('PANELİ SIFIRLA')).toBe(true)
    expect(panelResetConfirmed('  PANELİ SIFIRLA ')).toBe(true)
    expect(panelResetConfirmed('PANELI SIFIRLA')).toBe(false)
    expect(panelResetConfirmed('paneli sıfırla')).toBe(false)
    expect(panelResetConfirmed('')).toBe(false)
  })

  it('marks the steps before the running one done and the rest waiting', () => {
    const job = { jobId: 'j', status: 'LEASED', step: 2, progressLabel: null, progressPercent: 20, error: null, createdAt: '', completedAt: null }
    expect(panelResetStepStates(baseView.steps, job)).toEqual(['done', 'done', 'running', 'pending'])
    expect(panelResetStepStates(baseView.steps, { ...job, status: 'DEAD' })).toEqual(['done', 'done', 'failed', 'pending'])
    expect(panelResetStepStates(baseView.steps, { ...job, status: 'SUCCEEDED' })).toEqual(['done', 'done', 'done', 'done'])
  })

  it('shows what goes and what stays, keeps the red button locked until the word is typed, then starts the job and follows it', async () => {
    await render()
    expect(container.textContent).toContain('Paneli Tamamen Sıfırla')
    expect(container.textContent).toContain('35.200 Kayıt')
    expect(container.textContent).toContain('Entegrasyon bağlantıları ve giriş bilgileri')
    expect(container.textContent).toContain('hiç veri gelmesin istiyorsanız önce bağlantıları kapatın')
    const input = container.querySelector<HTMLInputElement>('#panel-factory-reset-confirm')!
    expect(goButton()!.disabled).toBe(true)
    type(input, 'PANELI SIFIRLA')
    expect(goButton()!.disabled).toBe(true)
    type(input, 'PANELİ SIFIRLA')
    expect(goButton()!.disabled).toBe(false)
    await act(async () => { goButton()!.click() })
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 0)) })
    const post = state.calls.find(call => call.method === 'POST')!
    expect(post.path).toBe('/settings/factory-reset')
    expect(JSON.parse(post.body!)).toEqual({ confirmation: 'PANELİ SIFIRLA' })
    expect(post.key).toBeTruthy()
    // While the job runs the form is gone and the steps show where it is.
    expect(goButton()).toBeUndefined()
    const steps = Array.from(container.querySelectorAll('.ss-fr-steps li')).map(item => item.className)
    expect(steps).toEqual(['is-done', 'is-done', 'is-running', 'is-pending'])
    expect(container.textContent).toContain('%20')
  })

  it('stays hidden from a role that may not reset', async () => {
    state.forbidden = true
    await render()
    expect(container.textContent).not.toContain('Paneli Tamamen Sıfırla')
  })
})
