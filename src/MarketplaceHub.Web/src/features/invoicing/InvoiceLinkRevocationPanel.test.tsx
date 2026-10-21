import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { InvoiceLinkRevocation } from './invoice-link-revocation'

const calls = vi.hoisted(() => ({ requests: [] as Array<{ path: string; init?: RequestInit }> }))
vi.mock('../../shared/api', () => ({
  hubApi: vi.fn(async (path: string, init?: RequestInit) => {
    calls.requests.push({ path, init })
    return { status: 'LINK_REMOVED' }
  }),
}))

import { InvoiceLinkRevocationPanel } from './InvoiceLinkRevocationPanel'

const trendyol: InvoiceLinkRevocation = { deliveryStateId: 'state-ty', packageId: 'pkg-1', platformCode: 'TRENDYOL', platformName: 'Trendyol', packageNumber: '4052072376', status: 'REVOCATION_REQUIRED', errorCode: null, mode: 'AUTOMATIC', instructions: 'Trendyol fatura linkini silme servisi sunuyor.', canAct: true, blockedReason: null, updatedAt: '2026-10-20T10:00:00Z' }
const hepsiburada: InvoiceLinkRevocation = { ...trendyol, deliveryStateId: 'state-hb', platformCode: 'HEPSIBURADA', platformName: 'Hepsiburada', packageNumber: 'HB-1', mode: 'VERIFY_ONLY', instructions: 'Hepsiburada Satıcı Paneli’nde faturayı kaldırın.' }
const n11: InvoiceLinkRevocation = { ...trendyol, deliveryStateId: 'state-n11', platformCode: 'N11', platformName: 'n11', packageNumber: 'N11-1', mode: 'MANUAL', canAct: false, instructions: 'n11 fatura linkini silme servisi sunmuyor.' }

let container: HTMLDivElement
let root: Root
const notices: Array<{ message: string; kind: string }> = []

function buttons() { return Array.from(document.body.querySelectorAll<HTMLButtonElement>('button')) }
function button(label: string) {
  const found = buttons().find(item => item.textContent?.trim() === label)
  if (!found) throw new Error(`Could not find button “${label}”`)
  return found
}
async function settle() { await act(async () => { await new Promise(resolve => setTimeout(resolve, 15)) }) }

async function render(items: InvoiceLinkRevocation[]) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  await act(async () => {
    root.render(<QueryClientProvider client={client}><InvoiceLinkRevocationPanel invoiceId="invoice-1" invoiceNumber="GIB2026000000001" items={items} onNotice={(message, kind) => notices.push({ message, kind })} /></QueryClientProvider>)
  })
}

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  calls.requests = []
  notices.length = 0
  container = document.createElement('div')
  document.body.appendChild(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
  vi.restoreAllMocks()
})

describe('InvoiceLinkRevocationPanel', () => {
  it('removes a Trendyol link only after the panel’s own confirmation and sends the explicit consent', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm')
    await render([trendyol])

    await act(async () => { button('Fatura Linkini Kaldır').click() })
    expect(calls.requests).toHaveLength(0)
    expect(document.body.textContent).toContain('Bu işlem geri alınamaz')
    expect(document.body.textContent).toContain('4052072376')

    await act(async () => { button('Linki Kaldır').click() })
    await settle()

    expect(confirmSpy).not.toHaveBeenCalled()
    expect(calls.requests).toHaveLength(1)
    const [request] = calls.requests
    expect(request.path).toBe('/invoices/invoice-1/marketplace-link-revocations/state-ty')
    expect(request.init?.method).toBe('POST')
    expect(JSON.parse(String(request.init?.body))).toEqual({ confirmed: true })
    expect((request.init?.headers as Record<string, string>)['Idempotency-Key']).toBeTruthy()
    expect(notices.at(-1)?.kind).toBe('success')
  })

  it('sends nothing when the operator cancels', async () => {
    await render([trendyol])
    await act(async () => { button('Fatura Linkini Kaldır').click() })
    await act(async () => { button('Vazgeç').click() })
    await settle()
    expect(calls.requests).toHaveLength(0)
  })

  it('offers a check for Hepsiburada and only instructions for a marketplace without a delete service', async () => {
    await render([hepsiburada, n11])
    expect(buttons().map(item => item.textContent?.trim())).toEqual(['Kaldırıldığını Kontrol Et'])
    expect(container.textContent).toContain('Hepsiburada Satıcı Paneli’nde faturayı kaldırın.')
    expect(container.textContent).toContain('n11 fatura linkini silme servisi sunmuyor.')
  })

  it('shows why the action is closed', async () => {
    await render([{ ...trendyol, canAct: false, blockedReason: 'Sunucudaki genel dış yazma anahtarı kapalı.' }])
    expect(button('Fatura Linkini Kaldır').disabled).toBe(true)
    expect(container.textContent).toContain('Sunucudaki genel dış yazma anahtarı kapalı.')
  })

  it('needs no password on a live provider: the confirmation alone sends the removal', async () => {
    await render([trendyol])
    expect(button('Fatura Linkini Kaldır').disabled).toBe(false)
    expect(container.querySelector('input[type="password"]')).toBeNull()
    expect(container.textContent).not.toContain('parola')
  })

  it('renders nothing when no link is left behind', async () => {
    await render([])
    expect(container.textContent).toBe('')
  })
})
