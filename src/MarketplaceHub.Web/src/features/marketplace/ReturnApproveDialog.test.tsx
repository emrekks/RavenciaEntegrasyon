import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ReturnApproveDialog, type ReturnApproveOrderInfo, type ReturnApproveRow } from './ReturnApproveDialog'

vi.mock('../../shared/api', async importOriginal => ({
  ...await importOriginal<typeof import('../../shared/api')>(),
  hubApi: vi.fn(async () => { throw new Error('the dialog must not send anything in this test') })
}))

const row = (id: string, orderLineId: string, title: string): ReturnApproveRow => ({ id, orderLineId, title, imageUrl: null, quantity: 1, sku: `SKU-${id}`, barcode: null, color: null, size: null, modelCode: null, hasInventoryMapping: false })
const rows = [row('a', 'line-1', 'Gömlek'), row('b', 'line-2', 'Pantolon')]
const order: ReturnApproveOrderInfo = { connectionId: 'c1', platformCode: 'TRENDYOL', platformName: 'Trendyol', orderNumber: '1001', customer: 'Ayşe', orderedAt: null, packageNumber: null, claimCode: 'CLM', reason: null }

let container: HTMLDivElement
let root: Root
const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
const render = (claimId: string) => act(() => root.render(<QueryClientProvider client={client}>
  <ReturnApproveDialog open claimId={claimId} version={1} marketplaceName="Trendyol" order={order} rows={rows} loading={false} error={null} onClose={() => {}} onDone={() => {}} />
</QueryClientProvider>))
const productBox = (title: string) => document.querySelector<HTMLInputElement>(`input[aria-label="${title} işleme dahil"]`)!

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})
afterEach(() => { act(() => root.unmount()); container.remove() })

describe('ReturnApproveDialog', () => {
  it('starts again with every product picked when it shows another claim with the same products', () => {
    render('claim-1')
    expect(productBox('Gömlek').checked).toBe(true)
    act(() => productBox('Gömlek').click())
    expect(productBox('Gömlek').checked).toBe(false)

    render('claim-2')
    expect(productBox('Gömlek').checked).toBe(true)
    expect(productBox('Pantolon').checked).toBe(true)
  })

  // T2-O1 (5 Nov 2026): a Hepsiburada claim is approved as the customer asked, chosen before anything is sent.
  const renderHepsiburada = (customerRequest: string | null) => act(() => root.render(<QueryClientProvider client={client}>
    <ReturnApproveDialog open claimId="hb-1" version={1} marketplaceName="Hepsiburada" order={{ ...order, platformCode: 'HEPSIBURADA', platformName: 'Hepsiburada' }} rows={rows} loading={false} error={null} wholeClaim finalizedWithChoice customerRequest={customerRequest} onClose={() => {}} onDone={() => {}} />
  </QueryClientProvider>))
  const choice = () => document.querySelector<HTMLSelectElement>('.rr-finalized select')!
  const approveButton = () => Array.from(document.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent === 'Onayla')!

  it('preselects Para İadesi / Değişim from the customer request of a Hepsiburada claim', () => {
    renderHepsiburada('REFUND')
    expect(choice().value).toBe('Refund')
    expect(document.body.textContent).toContain('Müşteri İsteği: Para İadesi')
    expect(approveButton().disabled).toBe(false)
  })

  it('waits for the seller to choose when the claim does not say what the customer asked for', () => {
    renderHepsiburada(null)
    expect(choice().value).toBe('')
    expect(approveButton().disabled).toBe(true)
    act(() => {
      Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value')?.set?.call(choice(), 'Change')
      choice().dispatchEvent(new Event('change', { bubbles: true }))
    })
    expect(approveButton().disabled).toBe(false)
  })

  // Report item 8: Shopify approves a whole return; the product choice is locked and no refund/exchange choice is asked.
  it('locks the product choice of a Shopify return without asking for refund or exchange', () => {
    act(() => root.render(<QueryClientProvider client={client}>
      <ReturnApproveDialog open claimId="shp-1" version={1} marketplaceName="Shopify" order={{ ...order, platformCode: 'SHOPIFY', platformName: 'Shopify' }} rows={rows} loading={false} error={null} wholeClaim onClose={() => {}} onDone={() => {}} />
    </QueryClientProvider>))
    expect(productBox('Gömlek').disabled).toBe(true)
    expect(productBox('Pantolon').checked).toBe(true)
    expect(document.querySelector('.rr-finalized select')).toBeNull()
    expect(document.body.textContent).toContain('Shopify kararı bu talepteki tüm ürün satırlarına uygulanır.')
    expect(approveButton().disabled).toBe(false)
  })
})
