import { act } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { byText, cleanup, click, render, waitFor } from '../../test/render'

const api = vi.hoisted(() => ({ posts: [] as string[], bodies: {} as Record<string, unknown> }))

vi.mock('../../shared/api', async importOriginal => ({
  ...(await importOriginal<typeof import('../../shared/api')>()),
  hubApi: vi.fn(async (path: string, init?: RequestInit) => {
    if (init?.method === 'POST') {
      api.posts.push(path)
      api.bodies[path] = JSON.parse(String(init.body))
      if (path === '/returns/b/actions') throw new Error('Pazar yeri yanıt vermedi.')
      return { status: 'APPROVED', lines: [] }
    }
    if (path === '/returns/a') return { lines: [{ id: 'a-1', orderLineId: 'ol-a', quantity: 2, remainingQuantity: 2, status: 'ACTION_REQUIRED', hasInventoryMapping: false, title: 'Gömlek', imageUrl: null }] }
    if (path === '/returns/b') return { lines: [{ id: 'b-1', orderLineId: 'ol-b', quantity: 1, remainingQuantity: 1, status: 'ACTION_REQUIRED', hasInventoryMapping: false, title: 'Pantolon', imageUrl: null }] }
    return []
  })
}))

import { ReturnBulkDecisionDialog, type BulkDecisionItem } from './ReturnBulkDecisionDialog'

const line = (id: string, title: string, quantity: number) => ({ id, title, imageUrl: null, quantity, sku: `SKU-${id}`, barcode: null, color: null, size: null, modelCode: null, reason: null })
const items: BulkDecisionItem[] = [
  { id: 'a', connectionId: 'c1', version: 1, orderNumber: '#100', customerName: 'Ayşe', orderedAt: null, packageNumber: null, platformCode: 'TRENDYOL', platformName: 'Trendyol', reason: { label: 'Bedeni/Ebatı Küçük Geldi', note: ['Müşteri Notu', 'Dar kalıp'], sellerFault: false }, lines: [line('ol-a', 'Gömlek', 2)] },
  { id: 'b', connectionId: 'c1', version: 1, orderNumber: '#200', customerName: 'Mehmet', orderedAt: null, packageNumber: null, platformCode: 'TRENDYOL', platformName: 'Trendyol', reason: null, lines: [line('ol-b', 'Pantolon', 1)] }
]

beforeEach(() => { api.posts = []; api.bodies = {} })
afterEach(cleanup)

function renderDialog(onClose = vi.fn(), onDone = vi.fn()) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<QueryClientProvider client={client}><ReturnBulkDecisionDialog mode="APPROVE" items={items} onClose={onClose} onDone={onDone} /></QueryClientProvider>)
  return { onClose, onDone, client }
}

describe('bulk return approval dialog', () => {
  it('shows the customer reason on the lines that have one and nothing on the others', async () => {
    renderDialog()
    const first = await waitFor(() => byText(document.body, '.rr-bcard-p', 'Gömlek'))
    expect(first.textContent).toContain('İade Sebebi: Bedeni/Ebatı Küçük Geldi')
    expect(first.textContent).toContain('Müşteri Notu: Dar kalıp')
    expect(byText(document.body, '.rr-bcard-p', 'Pantolon').textContent).not.toContain('İade Sebebi')
  })

  it('asks with the order and unit counts, then closes and reports the failed orders through the toast', async () => {
    const { onClose, onDone } = renderDialog()
    await waitFor(() => byText(document.body, '.rr-bcard-p', 'Gömlek').textContent?.includes('SKU-ol-a'))
    click(byText(document.body, 'button', '2 İadeyi Onayla'))
    expect(api.posts).toEqual([])

    const question = await waitFor(() => document.querySelector('.rr-confirm-q'))
    await waitFor(() => question.textContent === '2 sipariş ve 3 ürün onaylanacak. Onaylamak istiyor musunuz?')
    click(byText(document.querySelectorAll('.rr-modal')[1]!, 'button', 'Onayla'))

    await waitFor(() => onClose.mock.calls.length === 1)
    expect(api.posts).toEqual(['/returns/a/actions', '/returns/b/actions'])
    expect(onDone).toHaveBeenCalledTimes(1)
    const [message, kind] = onDone.mock.calls[0]!
    expect(kind).toBe('info')
    expect(message).toContain('Onaylanamayan sipariş: #200. Neden: Pazar yeri yanıt vermedi.')
    expect(Array.from(document.querySelectorAll('button')).some(button => button.textContent === 'Kapat')).toBe(false)
  })

  it('reads each decided return detail again, so its next action sends the new version (no 409)', async () => {
    const { onClose, client } = renderDialog()
    await waitFor(() => byText(document.body, '.rr-bcard-p', 'Gömlek').textContent?.includes('SKU-ol-a'))
    await waitFor(() => client.getQueryState(['return', 'b'])?.status === 'success')
    const invalidate = vi.spyOn(client, 'invalidateQueries')
    click(byText(document.body, 'button', '2 İadeyi Onayla'))
    await waitFor(() => document.querySelector('.rr-confirm-q'))
    click(byText(document.querySelectorAll('.rr-modal')[1]!, 'button', 'Onayla'))
    await waitFor(() => onClose.mock.calls.length === 1)
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['return', 'a'] })
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['return', 'b'] })
  })
})

// T2-O1 (5 Nov 2026): each Hepsiburada claim is approved as the customer asked (Para İadesi or Değişim), never as a
// refund for all; a claim that does not say waits for the seller's choice.
describe('bulk Hepsiburada approval', () => {
  const hepsiburada: BulkDecisionItem[] = [
    { id: 'h1', connectionId: 'c2', version: 1, orderNumber: '#300', customerName: 'Zeynep', orderedAt: null, packageNumber: null, platformCode: 'HEPSIBURADA', platformName: 'Hepsiburada', reason: null, lines: [line('ol-h1', 'Elbise', 1)], customerRequest: 'CHANGE' },
    { id: 'h2', connectionId: 'c2', version: 1, orderNumber: '#400', customerName: 'Ali', orderedAt: null, packageNumber: null, platformCode: 'HEPSIBURADA', platformName: 'Hepsiburada', reason: null, lines: [line('ol-h2', 'Etek', 1)], customerRequest: null }
  ]
  const choice = (orderNumber: string) => document.querySelector<HTMLSelectElement>(`select[aria-label="${orderNumber} için Para İadesi / Değişim"]`)!

  it('preselects what each customer asked for and sends that choice per claim', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const onClose = vi.fn()
    render(<QueryClientProvider client={client}><ReturnBulkDecisionDialog mode="APPROVE" items={hepsiburada} onClose={onClose} onDone={vi.fn()} /></QueryClientProvider>)
    expect(choice('#300').value).toBe('Change')
    expect(document.body.textContent).toContain('Müşteri İsteği: Değişim')
    expect(choice('#400').value).toBe('')
    expect(document.body.textContent).toContain('Müşteri isteği bilinmiyor')
    const submit = byText<HTMLButtonElement>(document.body, 'button', '2 İadeyi Onayla')
    expect(submit.disabled).toBe(true)

    act(() => {
      Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value')?.set?.call(choice('#400'), 'Refund')
      choice('#400').dispatchEvent(new Event('change', { bubbles: true }))
    })
    expect(submit.disabled).toBe(false)
    click(submit)
    await waitFor(() => document.querySelector('.rr-confirm-q'))
    click(byText(document.querySelectorAll('.rr-modal')[1]!, 'button', 'Onayla'))
    await waitFor(() => onClose.mock.calls.length === 1)
    expect(api.bodies['/returns/h1/actions']).toMatchObject({ action: 'APPROVE', finalizedWith: 'Change' })
    expect(api.bodies['/returns/h2/actions']).toMatchObject({ action: 'APPROVE', finalizedWith: 'Refund' })
  })
})
