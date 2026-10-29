import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { byText, cleanup, click, render, waitFor } from '../../test/render'

const api = vi.hoisted(() => ({ posts: [] as string[] }))

vi.mock('../../shared/api', async importOriginal => ({
  ...(await importOriginal<typeof import('../../shared/api')>()),
  hubApi: vi.fn(async (path: string, init?: RequestInit) => {
    if (init?.method === 'POST') {
      api.posts.push(path)
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

beforeEach(() => { api.posts = [] })
afterEach(cleanup)

function renderDialog(onClose = vi.fn(), onDone = vi.fn()) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<QueryClientProvider client={client}><ReturnBulkDecisionDialog mode="APPROVE" items={items} onClose={onClose} onDone={onDone} /></QueryClientProvider>)
  return { onClose, onDone }
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
})
